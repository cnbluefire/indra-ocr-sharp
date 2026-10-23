using System.Text.Json;
using Indra.Ocr.Text;
using Microsoft.ML.OnnxRuntime;

namespace Indra.Ocr.Onnx;

/// <summary>
/// The recognizer's model set: the ONNX sessions a model folder holds, plus the metadata
/// (<c>meta.json</c>) and the vocabularies that go with them.
/// </summary>
/// <remarks>
/// Mirrors the loading half of <c>OrtOCR.__init__</c>. <c>det</c>, <c>rec</c> and
/// <c>meta.json</c> are required; <c>mask</c>, <c>order</c> and <c>orient</c> are optional (the
/// shipped model set has no <c>mask.onnx</c>); <c>script</c> and <c>rec_cjk</c> come as a pair
/// and switch on the CJK recognizer.
/// </remarks>
public sealed class OcrModelGroup
{
    internal OcrModelGroup(string name, InferenceSession session, Vocab vocab)
    {
        Name = name;
        Session = session;
        Vocab = vocab;
    }

    public string Name { get; }
    public InferenceSession Session { get; }
    public Vocab Vocab { get; }
}

public sealed class OcrModels : IDisposable
{
    private readonly SessionOptions? _ownedOptions;

    private OcrModels(
        string directory,
        InferenceSession det,
        InferenceSession rec,
        InferenceSession? mask,
        InferenceSession? order,
        InferenceSession? orient,
        InferenceSession? script,
        IReadOnlyDictionary<string, OcrModelGroup> groups,
        IReadOnlyList<string> scriptGroups,
        Vocab vocab,
        int detLongSide,
        int recHeight,
        int recWindow,
        SessionOptions? ownedOptions)
    {
        Directory = directory;
        Det = det;
        Rec = rec;
        Mask = mask;
        Order = order;
        Orient = orient;
        Script = script;
        Groups = groups;
        ScriptGroups = scriptGroups;
        Vocab = vocab;
        DetLongSide = detLongSide;
        RecHeight = recHeight;
        RecWindow = recWindow;
        _ownedOptions = ownedOptions;
    }

    /// <summary>The model folder these sessions were loaded from.</summary>
    public string Directory { get; }

    public InferenceSession Det { get; }
    public InferenceSession Rec { get; }

    /// <summary>CenterMask line mask; loaded only when the folder has <c>mask.onnx</c>.</summary>
    public InferenceSession? Mask { get; }

    /// <summary>Reading-order model; <c>null</c> when the folder has no <c>order.onnx</c>.</summary>
    public InferenceSession? Order { get; }

    /// <summary>Page-orientation classifier; <c>null</c> when absent.</summary>
    public InferenceSession? Orient { get; }

    /// <summary>Script-group classifier; set when at least one group recognizer is available.</summary>
    public InferenceSession? Script { get; }

    /// <summary>The CJK line recognizer; set together with <see cref="Script"/>.</summary>
    public InferenceSession? RecCjk => Groups.TryGetValue("cjk", out var g) ? g.Session : null;

    /// <summary>The line recognizer's vocabulary (Latin/multi).</summary>
    public Vocab Vocab { get; }

    /// <summary>The CJK recognizer's vocabulary, or <c>null</c> without the CJK models.</summary>
    public Vocab? VocabCjk => Groups.TryGetValue("cjk", out var g) ? g.Vocab : null;

    /// <summary>Every optional group recognizer keyed by its metadata name.</summary>
    public IReadOnlyDictionary<string, OcrModelGroup> Groups { get; }

    /// <summary><c>script.onnx</c>'s class order (<c>script_groups</c> in metadata).</summary>
    public IReadOnlyList<string> ScriptGroups { get; }

    /// <summary><c>det_long_side</c> from <c>meta.json</c>.</summary>
    public int DetLongSide { get; }

    /// <summary><c>rec_height</c> from <c>meta.json</c>.</summary>
    public int RecHeight { get; }

    /// <summary>Tall-window multiplier for the base recognizer (<c>rec_window</c>; default 1).</summary>
    public int RecWindow { get; }

    /// <summary>Whether the CJK recognizer is available.</summary>
    public bool HasCjk => RecCjk is not null;

    /// <summary>Loads a model folder with freshly created session options this object owns.</summary>
    public static OcrModels Load(
        string onnxDirectory,
        int threads = 4,
        bool? recInt8 = null,
        IReadOnlyList<string>? providers = null)
    {
        var options = OnnxSessions.CreateOptions(threads, providers);
        try
        {
            return LoadCore(onnxDirectory, options, recInt8, ownedOptions: options);
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads a model folder with caller-supplied session options. The options are <b>not</b>
    /// disposed with this object: the pipeline shares one instance across the OCR and math
    /// sessions, exactly as Python does.
    /// </summary>
    public static OcrModels Load(string onnxDirectory, SessionOptions options, bool? recInt8 = null) =>
        LoadCore(onnxDirectory, options, recInt8, ownedOptions: null);

    private static OcrModels LoadCore(
        string onnxDirectory, SessionOptions options, bool? recInt8, SessionOptions? ownedOptions)
    {
        if (!System.IO.Directory.Exists(onnxDirectory))
            throw new DirectoryNotFoundException($"model folder {onnxDirectory} not found");

        string Need(string name)
        {
            var path = Path.Combine(onnxDirectory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(path);
            return path;
        }

        bool Has(string name) => File.Exists(Path.Combine(onnxDirectory, name));
        InferenceSession? Optional(string name) => Has(name) ? OnnxSessions.Open(Need(name), options) : null;

        var sessions = new List<InferenceSession>();
        try
        {
            var det = OnnxSessions.Open(Need("det.onnx"), options);
            sessions.Add(det);

            recInt8 ??= Has("rec.int8.onnx");
            var rec = OnnxSessions.Open(Need(recInt8.Value ? "rec.int8.onnx" : "rec.onnx"), options);
            sessions.Add(rec);

            var mask = Optional("mask.onnx");
            if (mask is not null)
                sessions.Add(mask);
            var order = Optional("order.onnx");
            if (order is not null)
                sessions.Add(order);
            var orient = Optional("orient.onnx");
            if (orient is not null)
                sessions.Add(orient);

            using var metaDoc = JsonDocument.Parse(File.ReadAllText(Need("meta.json")));
            var meta = metaDoc.RootElement;
            var vocab = Vocab.FromJson(meta.GetProperty("vocab").GetString()!);

            // Scripts: the classifier routes a line to rec_<group>.onnx and vocab_<group>.
            InferenceSession? script = null;
            var groups = new Dictionary<string, OcrModelGroup>(StringComparer.Ordinal);
            foreach (JsonProperty property in meta.EnumerateObject())
            {
                if (!property.Name.StartsWith("vocab_", StringComparison.Ordinal)) continue;
                string name = property.Name.Substring("vocab_".Length);
                string graph = $"rec_{name}.onnx";
                if (!Has(graph)) continue;
                var session = OnnxSessions.Open(Need(graph), options);
                sessions.Add(session);
                groups.Add(name, new OcrModelGroup(name, session,
                    Vocab.FromJson(property.Value.GetString()!)));
            }

            if (groups.Count > 0 && Has("script.onnx"))
            {
                script = OnnxSessions.Open(Need("script.onnx"), options);
                sessions.Add(script);
            }

            string[] scriptGroups = meta.TryGetProperty("script_groups", out JsonElement groupNames)
                ? groupNames.EnumerateArray().Select(v => v.GetString()!).ToArray()
                : ["base", "cjk", "rtl", "other"];

            int detLongSide = meta.GetProperty("det_long_side").GetInt32();
            int recHeight = meta.GetProperty("rec_height").GetInt32();
            int recWindow = meta.TryGetProperty("rec_window", out JsonElement window)
                ? window.GetInt32()
                : 1;

            return new OcrModels(onnxDirectory, det, rec, mask, order, orient, script, groups,
                scriptGroups, vocab, detLongSide, recHeight, recWindow, ownedOptions);
        }
        catch
        {
            foreach (var s in sessions)
                s.Dispose();
            throw;
        }
    }

    /// <summary>Whether a folder holds every file a model set needs.</summary>
    public static bool IsComplete(string onnxDirectory) =>
        File.Exists(Path.Combine(onnxDirectory, "det.onnx")) &&
        (File.Exists(Path.Combine(onnxDirectory, "rec.onnx")) ||
         File.Exists(Path.Combine(onnxDirectory, "rec.int8.onnx"))) &&
        File.Exists(Path.Combine(onnxDirectory, "meta.json"));

    public void Dispose()
    {
        Det.Dispose();
        Rec.Dispose();
        Mask?.Dispose();
        Order?.Dispose();
        Orient?.Dispose();
        Script?.Dispose();
        foreach (OcrModelGroup group in Groups.Values)
            group.Session.Dispose();
        _ownedOptions?.Dispose();
    }
}
