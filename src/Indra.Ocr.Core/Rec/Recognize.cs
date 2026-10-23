using System;
using System.Collections.Generic;
using Indra.Ocr.Imaging;
using Indra.Ocr.Maths;
using Indra.Ocr.Text;

namespace Indra.Ocr.Rec;

/// <summary>
/// The line recognizer's raw output for one padded batch — the three tensors
/// <c>sess.run(None, {"x": arr, "widths": widths})</c> returns.
/// </summary>
public sealed class RecOutput
{
    public RecOutput(long[] ids, float[] conf, long[] lengths, int timesteps,
                     float[]? blank = null, float[]? topValues = null, long[]? topIndices = null,
                     int topClasses = 0)
    {
        Ids = ids;
        Conf = conf;
        Lengths = lengths;
        Timesteps = timesteps;
        Blank = blank; TopValues = topValues; TopIndices = topIndices; TopClasses = topClasses;
    }

    /// <summary>CTC class ids, <c>[batch * timesteps]</c> (0 = blank).</summary>
    public long[] Ids { get; }

    /// <summary>Per-timestep peak probability, <c>[batch * timesteps]</c>.</summary>
    public float[] Conf { get; }

    /// <summary>Valid timesteps per line, <c>[batch]</c>.</summary>
    public long[] Lengths { get; }

    /// <summary>The width of one row of <see cref="Ids"/> / <see cref="Conf"/>.</summary>
    public int Timesteps { get; }
    public float[]? Blank { get; }
    public float[]? TopValues { get; }
    public long[]? TopIndices { get; }
    public int TopClasses { get; }
}

/// <summary>
/// The line recognizer (load-time-agnostic): a padded batch <c>[B, 1, height, width]</c> plus the
/// per-line widths in, the CTC ids, their peak probabilities and the valid lengths out.
/// </summary>
/// <remarks>
/// Both the Latin/multi recognizer (<c>rec.onnx</c>) and the CJK one (<c>rec_cjk.onnx</c>) have
/// this signature; the port treats them as one seam so a test can stand in for either.
/// </remarks>
public interface IRecModel
{
    RecOutput Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height, int width);
}

/// <summary>
/// The hanzi line classifier (<c>script.onnx</c>): the same input as the recognizer, one
/// probability per line that it holds Chinese characters.
/// </summary>
public interface IScriptModel
{
    /// <summary>Runs the model; returns flattened <c>[batch, groups]</c> probabilities.</summary>
    float[] Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height, int width);
}

public sealed record RecognizerGroup(string Name, IRecModel Model, Vocab Vocab);

/// <summary>
/// One token of a recognized line: the recognizer's class id and the page x of the CTC peak it
/// came from — or, for the CJK recognizer, the character itself.
/// </summary>
/// <remarks>
/// The CJK recognizer has its own vocabulary, so its ids mean nothing in <see cref="Vocab"/>;
/// like the reference's text-layer tokens its tokens are characters (see
/// <c>pipeline.OrtOCR.recognize</c> and <see cref="Recognizer"/>).
/// </remarks>
public readonly struct RecToken
{
    public RecToken(long id, string? character, double x, int? contentIndex = null)
    {
        Id = id;
        Character = character;
        X = x;
        ContentIndex = contentIndex;
    }

    /// <summary>The vocab class id; 0 when <see cref="Character"/> is set.</summary>
    public long Id { get; }

    /// <summary>The character of a CJK token, else <c>null</c>.</summary>
    public string? Character { get; }

    /// <summary>The token's page x (the CTC peak's timestep mapped back through the crop).</summary>
    public double X { get; }

    /// <summary>
    /// The PDF content index of a text-layer token, else <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The reference's tokens are 2-tuples <c>(id, x)</c> from the recognizer and 3-tuples
    /// <c>(char, x, content)</c> from the text layer; <c>assemble</c> reads the third element
    /// only <c>if len(tk) &gt; 2</c> to decide whether the PDF content order may be kept. A
    /// nullable property is the port of "the third element is there or not".
    /// </remarks>
    public int? ContentIndex { get; }

    public bool IsCharacter => Character is not null;

    public override string ToString()
    {
        string head = IsCharacter ? $"('{Character}', {X}" : $"({Id}, {X}";
        return ContentIndex is null ? head + ")" : head + $", {ContentIndex})";
    }
}

/// <summary>One recognized line: its text, its confidence and its tokens.</summary>
/// <remarks>
/// <see cref="Confidence"/> is a <see cref="double"/> on purpose: the reference computes a
/// float32 mean and widens it (<c>float(pm[keep].mean())</c>), and that widened value is what the
/// JSON output carries.
/// </remarks>
public sealed class RecognizedLine
{
    public RecognizedLine(string text, double confidence, IReadOnlyList<RecToken> tokens)
    {
        Text = text;
        Confidence = confidence;
        Tokens = tokens;
    }

    public string Text { get; }

    public double Confidence { get; }

    public IReadOnlyList<RecToken> Tokens { get; }

    /// <summary>The reference's initial <c>("", 0.0, [])</c>, for lines a group never fills in.</summary>
    public static RecognizedLine Empty { get; } = new("", 0.0, Array.Empty<RecToken>());

    public void Deconstruct(out string text, out double confidence, out IReadOnlyList<RecToken> tokens)
    {
        text = Text;
        confidence = Confidence;
        tokens = Tokens;
    }
}

/// <summary>
/// Port of <c>pipeline.OrtOCR.recognize</c> (and <c>hanzi_lines</c>): crop each line box, route the
/// lines that hold Chinese characters to the CJK recognizer, run both recognizers over
/// width-similar batches, read the CTC output and map every kept timestep back to a page x.
/// </summary>
/// <remarks>
/// The CenterMask step comes first when the model folder has <c>mask.onnx</c> (the same step as
/// training, see <see cref="LineMask.CropLines"/>). Lines under <see cref="MinConf"/> come back
/// empty: on empty form fields, checkboxes and rules the recognizer reads <c>"-"</c>, <c>"I"</c>
/// and other marks with a low peak probability, and the pipeline would rather leave the line
/// blank than print them.
/// <para>
/// With math models loaded (<c>--math</c>) the crops are also offered to
/// <see cref="MathReader.Flags"/>, and a line the classifier is sure about is read again by
/// MathRec — a confident reading that holds LaTeX replaces the recognized line, as one token
/// (it is never split).
/// </para>
/// </remarks>
public sealed class Recognizer
{
    /// <summary>A recognized line under this confidence (mean CTC peak probability) gets no text.</summary>
    public const float MinConf = 0.5f;

    private readonly IRecModel _rec;
    private readonly Vocab _vocab;
    private readonly IMaskModel? _mask;
    private readonly IScriptModel? _script;
    private readonly IRecModel? _recCjk;
    private readonly Vocab? _vocabCjk;
    private readonly IReadOnlyDictionary<string, RecognizerGroup> _groups;
    private readonly IReadOnlyList<string> _scriptGroups;
    private readonly int _window;
    private readonly IReadOnlyDictionary<string, LanguageModelSettings> _languageModels;
    private readonly int _height;
    private readonly int _batch;
    private readonly MathReader? _math;

    /// <param name="rec">The line recognizer.</param>
    /// <param name="vocab">Its vocabulary.</param>
    /// <param name="mask">CenterMask, when the model folder has <c>mask.onnx</c>.</param>
    /// <param name="script">The hanzi line classifier; give all three CJK arguments or none.</param>
    /// <param name="recCjk">The CJK recognizer.</param>
    /// <param name="vocabCjk">The CJK vocabulary.</param>
    /// <param name="height"><c>rec_height</c> from <c>meta.json</c> (32).</param>
    /// <param name="batch">Most lines per recognizer call (64).</param>
    /// <param name="math">The math reader, when the model folder has the math models and the
    /// caller asked for <c>--math</c>.</param>
    public Recognizer(IRecModel rec, Vocab vocab, IMaskModel? mask = null, IScriptModel? script = null,
                       IRecModel? recCjk = null, Vocab? vocabCjk = null,
                       int height = RecPreproc.LineHeight, int batch = 64, MathReader? math = null,
                       IReadOnlyDictionary<string, RecognizerGroup>? groups = null,
                       IReadOnlyList<string>? scriptGroups = null, int window = 1,
                       IReadOnlyDictionary<string, LanguageModelSettings>? languageModels = null)
    {
        _rec = rec ?? throw new ArgumentNullException(nameof(rec));
        _vocab = vocab ?? throw new ArgumentNullException(nameof(vocab));
        _mask = mask;
        _script = script;
        _recCjk = recCjk;
        _vocabCjk = vocabCjk;
        _height = height;
        _batch = batch;
        _math = math;
        _groups = groups ?? new Dictionary<string, RecognizerGroup>();
        _scriptGroups = scriptGroups ?? new[] { "base", "cjk", "rtl", "other" };
        _window = window;
        _languageModels = languageModels ?? new Dictionary<string, LanguageModelSettings>();

        // The reference sets script, rec_cjk and vocab_cjk together; routing needs all three.
        if (_groups.Count == 0 && script is not null && (recCjk is null || vocabCjk is null))
            throw new ArgumentException("the CJK recognizer needs script, rec_cjk and vocab_cjk",
                                        nameof(script));
        if (_groups.Count == 0 && script is null && (recCjk is not null || vocabCjk is not null))
            throw new ArgumentException("rec_cjk / vocab_cjk need the script classifier", nameof(script));
    }

    /// <summary>Whether the CJK recognizer is in use (the model folder has it).</summary>
    public bool HasCjk => _script is not null;

    /// <summary>
    /// Recognizes the line boxes of one page. <paramref name="boxes"/> is <c>[N, 5]</c>
    /// (<c>x0, y0, x1, y1, score</c>, as <see cref="Det.Detect.Run"/> returns them); only the first
    /// four columns are used. The result is one entry per box, in the same order.
    /// </summary>
    public List<RecognizedLine> Run(GrayImage gray, ReadOnlySpan<float> boxes, bool vertical = false)
    {
        var boxes4 = Boxes4(boxes);
        bool[]? dark = _window > 1 ? Darkness(gray, boxes4) : null;
        var crops = _mask is null && dark is not null
            ? CropLines(gray, boxes4, 1, dark)
            : LineMask.CropLines(gray, boxes4, _height, _mask);
        var tall = _window > 1 ? CropLines(gray, boxes4, _window, dark!) : crops;
        var lines = new List<RecognizedLine>(crops.Count);
        for (int i = 0; i < crops.Count; i++) lines.Add(RecognizedLine.Empty);
        var bestConfidence = Enumerable.Repeat(-1.0, crops.Count).ToArray();

        // One group normally; two when the hanzi classifier splits the lines by script.
        var groups = new List<(string Name, IRecModel Model, Vocab Vocab, List<int> Sel, bool AsChars)>();
        if (vertical && _groups.TryGetValue("cjk", out RecognizerGroup? cjk))
        {
            groups.Add(("cjk", cjk.Model, cjk.Vocab,
                        Enumerable.Range(0, crops.Count).ToList(), true));
        }
        else if (_script is not null && _groups.Count > 0 && crops.Count > 0)
        {
            (string[] names, bool[] sure) = LineGroups(crops);
            var baseSel = new List<int>();
            for (int j = 0; j < names.Length; j++)
                if (names[j] == "base" || !sure[j]) baseSel.Add(j);
            groups.Add(("base", _rec, _vocab, baseSel, false));
            foreach (KeyValuePair<string, RecognizerGroup> pair in _groups)
            {
                string name = pair.Key;
                RecognizerGroup group = pair.Value;
                var sel = new List<int>();
                for (int j = 0; j < names.Length; j++) if (names[j] == name) sel.Add(j);
                groups.Add((name, group.Model, group.Vocab, sel, true));
            }
        }
        else if (_script is not null && crops.Count > 0)
        {
            var hanzi = HanziLines(crops);
            var latinSel = new List<int>();
            var cjkSel = new List<int>();
            for (int j = 0; j < hanzi.Length; j++) (hanzi[j] ? cjkSel : latinSel).Add(j);
            groups.Add(("base", _rec, _vocab, latinSel, false));
            groups.Add(("cjk", _recCjk!, _vocabCjk!, cjkSel, true));
        }
        else
        {
            var all = new List<int>(crops.Count);
            for (int j = 0; j < crops.Count; j++) all.Add(j);
            groups.Add(("base", _rec, _vocab, all, false));
        }

        foreach (var (name, model, vocab, sel, asChars) in groups)
        {
            var widths = new int[sel.Count];
            for (int i = 0; i < sel.Count; i++) widths[i] = CropWidth(crops[sel[i]]);

            foreach (int[] part in RecPreproc.WidthBatches(widths, _batch))
            {
                var idx = new int[part.Length];
                var batchLines = new List<float[]>(part.Length);
                for (int k = 0; k < part.Length; k++)
                {
                    idx[k] = sel[part[k]];
                    batchLines.Add(crops[idx[k]]);
                }

                var batchWidths = new long[part.Length];
                int inputHeight = model == _rec ? _height * _window : _height;
                if (model == _rec && _window > 1)
                {
                    batchLines.Clear();
                    for (int k = 0; k < idx.Length; k++) batchLines.Add(tall[idx[k]]);
                }
                var x = RecPreproc.PadBatch(batchLines, inputHeight, batchWidths, out int width);
                var output = model.Run(x, batchWidths, part.Length, inputHeight, width);
                var stride = (double)width / Math.Max(output.Timesteps, 1);

                for (int k = 0; k < idx.Length; k++)
                {
                    LanguageModelSettings? lm = _languageModels.TryGetValue(name, out var settings)
                        ? settings : null;
                    RecognizedLine line = ReadLine(boxes, idx[k], crops, vocab, asChars, output, k,
                                                   stride, lm);
                    if (line.Confidence <= bestConfidence[idx[k]]) continue;
                    bestConfidence[idx[k]] = line.Confidence;
                    lines[idx[k]] = line;
                }
            }
        }

        // The reference reads the math lines last, from the crops it has just made: a confident
        // reading with LaTeX replaces the line (one token holds the whole line, so it is never
        // split). The token has no PDF content index — this is the OCR path, not the text layer.
        if (_math is not null && crops.Count > 0)
        {
            bool[] isMath = _math.Flags(crops, _batch, _height);
            var math = new List<int>();
            for (int j = 0; j < isMath.Length; j++)
                if (isMath[j])
                    math.Add(j);

            if (math.Count > 0)
            {
                List<MathReading> readings = _math.Read(gray, Subset(boxes, math));
                for (int k = 0; k < math.Count; k++)
                {
                    int j = math[k];
                    MathReading reading = readings[k];
                    if (reading.Text.Contains('$') && reading.Confidence >= MathReader.MinConf)
                    {
                        double xc = (double)(boxes[j * 5] + boxes[j * 5 + 2]) / 2;
                        lines[j] = new RecognizedLine(reading.Text, 1.0,
                                                      new[] { new RecToken(0, reading.Text, xc) });
                    }
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// <c>hanzi_lines</c>: per line box, does its crop hold Chinese characters
    /// (<c>script.onnx</c>)? Needs the CJK model set; the crops are the ones
    /// <see cref="Run"/> uses.
    /// </summary>
    public bool[] HanziLines(GrayImage gray, ReadOnlySpan<float> boxes)
    {
        if (_script is null) throw new InvalidOperationException("no script model");
        return HanziLines(LineMask.CropLines(gray, Boxes4(boxes), _height, _mask));
    }

    /// <summary>
    /// Per line crop: does it hold Chinese characters (<c>script.onnx</c>)?
    /// </summary>
    private bool[] HanziLines(List<float[]> crops)    {
        var hanzi = new bool[crops.Count];
        var widths = new int[crops.Count];
        for (int i = 0; i < crops.Count; i++) widths[i] = CropWidth(crops[i]);

        foreach (int[] part in RecPreproc.WidthBatches(widths, _batch))
        {
            var batchLines = new List<float[]>(part.Length);
            for (int k = 0; k < part.Length; k++) batchLines.Add(crops[part[k]]);

            var batchWidths = new long[part.Length];
            var x = RecPreproc.PadBatch(batchLines, _height, batchWidths, out int width);
            var p = _script!.Run(x, batchWidths, part.Length, _height, width);
            if (p.Length < part.Length)
                throw new InvalidOperationException(
                    $"script model returned {p.Length} probabilities for {part.Length} lines");
            for (int k = 0; k < part.Length; k++) hanzi[part[k]] = p[k] > 0.5f;
        }
        return hanzi;
    }

    private (string[] Names, bool[] Sure) LineGroups(List<float[]> crops)
    {
        var names = Enumerable.Repeat("base", crops.Count).ToArray();
        var sure = Enumerable.Repeat(true, crops.Count).ToArray();
        var widths = crops.Select(CropWidth).ToArray();
        foreach (int[] part in RecPreproc.WidthBatches(widths, _batch))
        {
            var batchLines = part.Select(i => crops[i]).ToList();
            var batchWidths = new long[part.Length];
            float[] x = RecPreproc.PadBatch(batchLines, _height, batchWidths, out int width);
            float[] p = _script!.Run(x, batchWidths, part.Length, _height, width);
            int cols = p.Length / part.Length;
            for (int k = 0; k < part.Length; k++)
            {
                if (cols == 1)
                {
                    float probability = p[k];
                    names[part[k]] = probability > 0.5f && _groups.ContainsKey("cjk")
                        ? "cjk"
                        : "base";
                    // The old one-output classifier was a hard Hanzi switch.  The uncertain-line
                    // double read belongs to the new multi-class export only.
                    sure[part[k]] = true;
                    continue;
                }
                int best = 0;
                float prob = p[k * cols];
                for (int c = 1; c < cols; c++)
                    if (p[k * cols + c] > prob) { best = c; prob = p[k * cols + c]; }
                string group = best < _scriptGroups.Count ? _scriptGroups[best] : "base";
                names[part[k]] = _groups.ContainsKey(group) ? group : "base";
                sure[part[k]] = prob > 0.95f;
            }
        }
        return (names, sure);
    }

    public (string[] Names, bool[] Sure) BoxGroups(GrayImage gray, ReadOnlySpan<float> boxes)
    {
        if (_script is null || boxes.Length == 0)
            return (Enumerable.Repeat("base", boxes.Length / 5).ToArray(), new bool[boxes.Length / 5]);
        return LineGroups(LineMask.CropLines(gray, Boxes4(boxes), _height, _mask));
    }

    private static bool[] Darkness(GrayImage gray, ReadOnlySpan<float> boxes4)
    {
        var result = new bool[boxes4.Length / 4];
        for (int i = 0; i < result.Length; i++)
            result[i] = RecPreproc.IsDarkLine(gray, boxes4.Slice(i * 4, 4));
        return result;
    }

    private List<float[]> CropLines(GrayImage gray, ReadOnlySpan<float> boxes4, int window, bool[] dark)
    {
        var result = new List<float[]>(dark.Length);
        for (int i = 0; i < dark.Length; i++)
            result.Add(RecPreproc.CropLine(gray, boxes4.Slice(i * 4, 4), out _, _height,
                                           window: window, dark: dark[i]));
        return result;
    }

    /// <summary>
    /// One line of a batch: the CTC collapse, the page x of every kept timestep, the confidence
    /// and the <see cref="MinConf"/> gate.
    /// </summary>
    private RecognizedLine ReadLine(ReadOnlySpan<float> boxes, int j, List<float[]> crops, Vocab vocab,
                                    bool asChars, RecOutput output, int k, double stride,
                                    LanguageModelSettings? lm = null)
    {
        int t = output.Timesteps;
        int n = Math.Min((int)output.Lengths[k], t);       // numpy's seq[k, :n] never passes T
        int off = k * t;

        // keep = (seq != 0) & concatenate([[True], seq[1:] != seq[:-1]]): the first timestep of a
        // run of equal ids, and never the blank.
        var kept = new List<int>(n);
        for (int i = 0; i < n; i++)
            if (output.Ids[off + i] != 0 && (i == 0 || output.Ids[off + i] != output.Ids[off + i - 1]))
                kept.Add(i);

        float x0 = boxes[j * 5], y0 = boxes[j * 5 + 1], x1 = boxes[j * 5 + 2], y1 = boxes[j * 5 + 3];
        double h = Math.Max(1.0, (double)y1 - y0);
        double ix0 = Math.Floor(x0 - RecPreproc.PadX * h);
        double cw = Math.Ceiling(x1 + RecPreproc.PadX * h) - ix0;
        double f = cw / CropWidth(crops[j]);

        var tokens = new List<RecToken>(kept.Count);
        var ids = new int[kept.Count];
        var confs = new float[kept.Count];
        for (int q = 0; q < kept.Count; q++)
        {
            int i = kept[q];
            long id = output.Ids[off + i];
            ids[q] = (int)id;
            confs[q] = output.Conf[off + i];
            double x = ix0 + (i + 0.5) * stride * f;
            tokens.Add(asChars ? new RecToken(0, vocab.Characters[(int)id - 1], x)
                               : new RecToken(id, null, x));
        }

        float conf = kept.Count == 0 ? 0f : Numerics.Mean32(confs);
        if (lm is LanguageModelSettings settings && conf >= MinConf && conf < settings.Gate &&
            output.Blank is not null && output.TopValues is not null && output.TopIndices is not null)
        {
            (int[] beamIds, int[] steps) = BeamSearch.Run(
                output.Blank.AsSpan(off, n),
                output.TopValues.AsSpan(k * t * output.TopClasses, n * output.TopClasses),
                output.TopIndices.AsSpan(k * t * output.TopClasses, n * output.TopClasses),
                output.TopClasses, settings);
            ids = beamIds;
            tokens.Clear();
            for (int q = 0; q < ids.Length; q++)
            {
                double x = ix0 + (steps[q] + 0.5) * stride * f;
                int id = ids[q];
                tokens.Add(asChars ? new RecToken(0, vocab.Characters[id - 1], x)
                                   : new RecToken(id, null, x));
            }
        }
        return conf >= MinConf
            ? new RecognizedLine(vocab.Decode(ids), conf, tokens)
            : new RecognizedLine("", conf, Array.Empty<RecToken>());
    }

    private static class BeamSearch
    {
        private sealed record Beam(double Blank, double NonBlank, double Lm, List<int> Steps);

        public static (int[] Ids, int[] Steps) Run(ReadOnlySpan<float> blank,
            ReadOnlySpan<float> topValues, ReadOnlySpan<long> topIndices, int classes,
            LanguageModelSettings settings)
        {
            var beams = new Dictionary<string, (List<int> Prefix, Beam Value)>
            {
                [""] = (new List<int>(), new Beam(0, double.NegativeInfinity, 0, new List<int>())),
            };
            for (int t = 0; t < blank.Length; t++)
            {
                double b0 = blank[t];
                var next = new Dictionary<string, (List<int>, Beam)>();
                foreach (var entry in beams.Values)
                {
                    List<int> prefix = entry.Prefix; Beam value = entry.Value;
                    Add(next, prefix, LogAdd(value.Blank, value.NonBlank) + b0,
                        double.NegativeInfinity, value.Lm, value.Steps);
                    int last = prefix.Count == 0 ? -1 : prefix[^1];
                    for (int c = 0; c < classes; c++)
                    {
                        int token = (int)topIndices[t * classes + c];
                        double lp = topValues[t * classes + c];
                        if (token == 0 || lp < Math.Log(0.003)) continue;
                        if (token == last)
                            Add(next, prefix, double.NegativeInfinity, value.NonBlank + lp,
                                value.Lm, value.Steps);
                        double from = token == last ? value.Blank + lp
                                                    : LogAdd(value.Blank, value.NonBlank) + lp;
                        if (double.IsNegativeInfinity(from)) continue;
                        var extended = new List<int>(prefix) { token };
                        var steps = new List<int>(value.Steps) { t };
                        double lmp = settings.Model.LogProbability(prefix, token);
                        Add(next, extended, double.NegativeInfinity, from,
                            value.Lm + settings.Alpha * lmp + settings.Beta, steps);
                    }
                }
                beams = next.Values.OrderByDescending(v => LogAdd(v.Item2.Blank, v.Item2.NonBlank) + v.Item2.Lm)
                                   .Take(8).ToDictionary(v => Key(v.Item1), v => v);
            }
            var best = beams.Values.OrderByDescending(
                v => LogAdd(v.Value.Blank, v.Value.NonBlank) + v.Value.Lm).First();
            return (best.Prefix.ToArray(), best.Value.Steps.ToArray());
        }

        private static void Add(Dictionary<string, (List<int>, Beam)> into, List<int> prefix,
            double blank, double nonBlank, double lm, List<int> steps)
        {
            string key = Key(prefix);
            if (!into.TryGetValue(key, out var old))
                into[key] = (new List<int>(prefix), new Beam(blank, nonBlank, lm, new List<int>(steps)));
            else
                into[key] = (old.Item1, old.Item2 with
                {
                    Blank = LogAdd(old.Item2.Blank, blank),
                    NonBlank = LogAdd(old.Item2.NonBlank, nonBlank),
                });
        }

        private static string Key(List<int> ids) => string.Join(",", ids);
        private static double LogAdd(double a, double b)
        {
            if (double.IsNegativeInfinity(a)) return b;
            if (double.IsNegativeInfinity(b)) return a;
            double m = Math.Max(a, b);
            return m + Math.Log(Math.Exp(a - m) + Math.Exp(b - m));
        }
    }

    private int CropWidth(float[] crop) => crop.Length / _height;

    /// <summary>
    /// The first four columns of a <c>[N, 5]</c> box array, as the contiguous <c>[N, 4]</c> the
    /// crop helpers take (the reference slices <c>boxes[:, :4]</c>).
    /// </summary>
    private static float[] Boxes4(ReadOnlySpan<float> boxes)
    {
        int count = boxes.Length / 5;
        var flat = new float[count * 4];
        for (int i = 0; i < count; i++) boxes.Slice(i * 5, 4).CopyTo(flat.AsSpan(i * 4));
        return flat;
    }

    /// <summary>
    /// The <c>[n, 5]</c> rows of <paramref name="boxes"/> at <paramref name="idx"/> — the
    /// reference's <c>boxes[math]</c>, which keeps all five columns.
    /// </summary>
    private static float[] Subset(ReadOnlySpan<float> boxes, List<int> idx)
    {
        var flat = new float[idx.Count * 5];
        for (int k = 0; k < idx.Count; k++) boxes.Slice(idx[k] * 5, 5).CopyTo(flat.AsSpan(k * 5));
        return flat;
    }
}
