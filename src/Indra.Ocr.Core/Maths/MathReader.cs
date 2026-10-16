using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Indra.Ocr.Imaging;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

namespace Indra.Ocr.Maths;

/// <summary>
/// One tensor of a math graph: float32 or int64 values plus their shape.
/// </summary>
/// <remarks>
/// The seam between <see cref="MathReader"/> and ONNX Runtime. The four graphs are fed and read as
/// named tensors; carrying the values and the shape in a type of our own keeps
/// <c>Microsoft.ML.OnnxRuntime</c> out of the step, so a test can stand in for the four sessions
/// (<see cref="IMathModel"/>) the way <see cref="IRecModel"/> lets a test stand in for the
/// recognizer.
/// </remarks>
public sealed class MathTensor
{
    private MathTensor(int[] dimensions, float[]? floats, long[]? longs)
    {
        Dimensions = dimensions;
        Floats = floats;
        Longs = longs;
    }

    /// <summary>A float32 tensor of the given shape.</summary>
    public static MathTensor Float32(float[] values, params int[] dimensions)
        => new(dimensions, values, null);

    /// <summary>
    /// An int64 tensor of the given shape; no dimensions is the scalar the decoder's <c>pos</c>
    /// input is (a rank-0 tensor, which ONNX Runtime accepts).
    /// </summary>
    public static MathTensor Int64(long[] values, params int[] dimensions)
        => new(dimensions, null, values);

    /// <summary>The shape; empty for a scalar.</summary>
    public int[] Dimensions { get; }

    /// <summary>The values when this is a float32 tensor, else <c>null</c>.</summary>
    public float[]? Floats { get; }

    /// <summary>The values when this is an int64 tensor, else <c>null</c>.</summary>
    public long[]? Longs { get; }

    public bool IsFloat32 => Floats is not null;
}

/// <summary>
/// One of the four math graphs, as a function from named tensors to named outputs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InputNames"/> is what the graph itself still reads: the exports drop unused inputs
/// (the init graph does not read the mask), and the reference filters its feed by those names
/// before every call. The port does the same, so the graphs can be re-exported without touching
/// the reader.
/// </para>
/// <para>
/// The outputs are positional, like the reference's <c>run(None, feed)</c> unpacking.
/// </para>
/// </remarks>
public interface IMathModel : IDisposable
{
    /// <summary>The input names this graph reads.</summary>
    IReadOnlyList<string> InputNames { get; }

    /// <summary>Runs the graph over <paramref name="feed"/> (names not in
    /// <see cref="InputNames"/> are ignored) and returns its outputs in order.</summary>
    IReadOnlyList<MathTensor> Run(IReadOnlyDictionary<string, MathTensor> feed);
}

/// <summary>One math reading: the text with inline LaTeX, and the mean token probability.</summary>
public readonly record struct MathReading(string Text, double Confidence);

/// <summary>
/// Port of <c>math.py</c>: the math line classifier (<c>mathcls.onnx</c>) and MathRec, the
/// attention decoder that reads a line as text with inline LaTeX (<c>math_enc.onnx</c>,
/// <c>math_dec_init.onnx</c>, <c>math_dec_step.onnx</c>).
/// </summary>
/// <remarks>
/// <para>
/// The CTC recognizer reads one symbol per column, so it cannot write a fraction or a sum with
/// limits. MathRec reads a taller crop — the metadata's <c>height</c>, 64, against the
/// recognizer's 32 — with an attention decoder, one token per step, with the decoder's keys and
/// values cached between steps.
/// </para>
/// <para>
/// The namespace is <c>Indra.Ocr.Maths</c>, not <c>Indra.Ocr.Math</c>: a member of
/// <c>Indra.Ocr</c> named <c>Math</c> captures every unqualified <c>Math.</c> in the assembly
/// (an enclosing namespace's own members win over <c>using System;</c>), so <c>Math.Max</c>
/// would resolve into it — "the type or namespace name 'Max' does not exist in the namespace
/// 'Indra.Ocr.Math'".
/// </para>
/// <para>
/// The class is a step like <see cref="Recognizer"/>: it takes the four graphs and the metadata
/// and does the arithmetic. Reading the folder — the file names, the sessions, the JSON — is
/// <c>Indra.Ocr.Onnx.MathModels.Load</c>.
/// </para>
/// </remarks>
public sealed class MathReader : IDisposable
{
    /// <summary><c>MATH_MIN</c>: the classifier probability above which a line is read as math.</summary>
    public const float Min = 0.5f;

    /// <summary>
    /// <c>MATH_MIN_CONF</c>: a math reading replaces a line only at this mean token probability or
    /// more. On print the model has not seen (old scans) it writes confident-looking LaTeX that is
    /// wrong, at low probability.
    /// </summary>
    public const double MinConf = 0.9;

    /// <summary>
    /// <c>EARLY_STOP_MARGIN</c>: a line whose mean token probability is this far below
    /// <see cref="MinConf"/> after 8 tokens stops decoding — it would be rejected anyway.
    /// </summary>
    public const double EarlyStopMargin = 0.1;

    /// <summary>The decoder's special ids: begin, end, and the first real token.</summary>
    public const int Bos = 1, Eos = 2, NSpecial = 4;

    private readonly IMathModel _cls;
    private readonly IMathModel _enc;
    private readonly IMathModel _init;
    private readonly IMathModel _step;
    private readonly string[] _itos;

    private MathReader(IMathModel cls, IMathModel enc, IMathModel init, IMathModel step,
                       int height, int maxTokens, int layers, int heads, int channels,
                       string[] itos)
    {
        _cls = cls;
        _enc = enc;
        _init = init;
        _step = step;
        _itos = itos;
        Height = height;
        MaxTokens = maxTokens;
        Layers = layers;
        Heads = heads;
        HeadDim = channels / heads;
    }

    /// <summary>
    /// Builds a reader over four graphs and the metadata's numbers — <c>MathReader.__init__</c>
    /// without the sessions.
    /// </summary>
    /// <param name="cls">The classifier (<c>mathcls.onnx</c>).</param>
    /// <param name="enc">The encoder (<c>math_enc.onnx</c>).</param>
    /// <param name="init">The decoder's first step (<c>math_dec_init.onnx</c>).</param>
    /// <param name="step">The decoder's loop step (<c>math_dec_step.onnx</c>).</param>
    /// <param name="height">The crop height MathRec reads (64), not the recognizer's.</param>
    /// <param name="maxTokens">Most decoder steps per line (<c>max_tokens</c>).</param>
    /// <param name="layers">Decoder layers (<c>dec_layers</c>).</param>
    /// <param name="heads">Decoder attention heads (<c>dec_heads</c>).</param>
    /// <param name="channels">Decoder width (<c>channels</c>); <c>channels / heads</c> is the head
    /// dimension.</param>
    /// <param name="tokens">The token vocabulary; the reference prefixes four empty strings for
    /// the special ids.</param>
    public static MathReader Create(IMathModel cls, IMathModel enc, IMathModel init, IMathModel step,
                                    int height, int maxTokens, int layers, int heads, int channels,
                                    IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(cls);
        ArgumentNullException.ThrowIfNull(enc);
        ArgumentNullException.ThrowIfNull(init);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(tokens);
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (maxTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxTokens));
        if (layers <= 0) throw new ArgumentOutOfRangeException(nameof(layers));
        if (heads <= 0) throw new ArgumentOutOfRangeException(nameof(heads));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        var itos = new string[NSpecial + tokens.Count];
        for (int i = 0; i < NSpecial; i++) itos[i] = "";
        for (int i = 0; i < tokens.Count; i++) itos[NSpecial + i] = tokens[i];
        return new MathReader(cls, enc, init, step, height, maxTokens, layers, heads, channels, itos);
    }

    /// <summary>The crop height MathRec reads (<c>height</c> from the metadata).</summary>
    public int Height { get; }

    /// <summary>Most decoder steps per line (<c>max_tokens</c>).</summary>
    public int MaxTokens { get; }

    /// <summary>Decoder layers (<c>dec_layers</c>).</summary>
    public int Layers { get; }

    /// <summary>Decoder attention heads (<c>dec_heads</c>).</summary>
    public int Heads { get; }

    /// <summary>Head dimension (<c>channels // dec_heads</c>).</summary>
    public int HeadDim { get; }

    /// <summary>
    /// <c>detokenize</c>: tokens to text; inside math (between <c>"$"</c> tokens) they are joined
    /// by spaces. The result is Python's <c>" ".join(text.split())</c>, which drops the
    /// whitespace-only tokens and collapses every run of whitespace to one space.
    /// </summary>
    public static string Detokenize(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var outParts = new List<string>();
        var math = new List<string>();
        bool inMath = false;
        foreach (string token in tokens)
        {
            if (token == "$")
            {
                if (inMath)
                {
                    outParts.Add("$" + string.Join(" ", math) + "$");
                    math.Clear();
                }

                inMath = !inMath;
            }
            else if (inMath)
            {
                math.Add(token);
            }
            else
            {
                outParts.Add(token);
            }
        }

        if (inMath && math.Count > 0)
            outParts.Add("$" + string.Join(" ", math) + "$");
        return UnicodeText.CollapseSpaces(string.Concat(outParts));
    }

    /// <summary>
    /// <c>math.flags</c>: per line crop (the recognizer's crops, <paramref name="height"/> rows
    /// high), does it hold math?
    /// </summary>
    /// <param name="crops">The line ink maps the recognizer made.</param>
    /// <param name="batch">Most lines per classifier call (64).</param>
    /// <param name="height">The crop height; the reference reads it off the array's shape, and a
    /// <see cref="float"/>[] has none, so the caller says it — it is the recognizer's height
    /// (<c>rec_height</c>), not MathRec's.</param>
    public bool[] Flags(IReadOnlyList<float[]> crops, int batch = 64, int height = RecPreproc.LineHeight)
    {
        float[] probabilities = Probabilities(crops, batch, height);
        var flags = new bool[probabilities.Length];
        for (int i = 0; i < flags.Length; i++) flags[i] = probabilities[i] > Min;
        return flags;
    }

    /// <summary>
    /// The classifier's raw probabilities — what <c>math.flags</c> compares against
    /// <see cref="Min"/> and throws away.
    /// </summary>
    /// <remarks>
    /// Exposed because it is what the parity fixture compares bit for bit: a bool would hide a
    /// crop, a padding or a resize that went wrong in the last ulp.
    /// </remarks>
    public float[] Probabilities(IReadOnlyList<float[]> crops, int batch = 64,
                                 int height = RecPreproc.LineHeight)
    {
        ArgumentNullException.ThrowIfNull(crops);
        var probabilities = new float[crops.Count];
        if (crops.Count == 0) return probabilities;

        var widths = new int[crops.Count];
        for (int i = 0; i < crops.Count; i++) widths[i] = crops[i].Length / height;

        foreach (int[] idx in RecPreproc.WidthBatches(widths, batch))
        {
            float[] x = Batch(crops, idx, height, out long[] batchWidths, out int padded);
            IReadOnlyList<MathTensor> result = _cls.Run(new Dictionary<string, MathTensor>
            {
                ["x"] = MathTensor.Float32(x, idx.Length, 1, height, padded),
                ["widths"] = MathTensor.Int64(batchWidths, idx.Length),
            });
            float[] p = result[0].Floats!;
            for (int k = 0; k < idx.Length; k++) probabilities[idx[k]] = p[k];
        }

        return probabilities;
    }

    /// <summary>
    /// <c>math.read</c>: (text with inline LaTeX, mean token probability) for each box.
    /// </summary>
    /// <param name="gray">The page the boxes were found on.</param>
    /// <param name="boxes">The boxes, <c>[n, 5]</c> (<c>x0 y0 x1 y1 score</c>, as the detector
    /// produced them); the crop uses the first four columns, like the reference's
    /// <c>boxes[:, :4]</c>.</param>
    /// <param name="batch">Most lines per decoding loop (64).</param>
    /// <remarks>
    /// Few, wide batches: each batch is one decoding loop of up to <c>max_tokens</c> steps, and a
    /// step costs about the same for 8 lines as for 40 — hence <c>max_ratio 4.0</c> and the wider
    /// area cap.
    /// </remarks>
    public List<MathReading> Read(GrayImage gray, ReadOnlySpan<float> boxes, int batch = 64)
    {
        var crops = LineMask.CropLines(gray, Quads(boxes), Height);
        var readings = new List<MathReading>(crops.Count);
        for (int i = 0; i < crops.Count; i++) readings.Add(new MathReading("", 0.0));
        if (crops.Count == 0) return readings;

        var widths = new int[crops.Count];
        for (int i = 0; i < crops.Count; i++) widths[i] = crops[i].Length / Height;

        foreach (int[] idx in RecPreproc.WidthBatches(widths, batch, maxRatio: 4.0,
                                                      maxArea: 16 * 64 * 640))
        {
            int b = idx.Length;
            float[] x = Batch(crops, idx, Height, out long[] batchWidths, out int padded);
            IReadOnlyList<MathTensor> encoded = _enc.Run(new Dictionary<string, MathTensor>
            {
                ["x"] = MathTensor.Float32(x, b, 1, Height, padded),
                ["widths"] = MathTensor.Int64(batchWidths, b),
            });
            MathTensor memory = encoded[0];
            MathTensor mask = encoded[1];

            // The decoder's first step caches the cross-attention keys and values; the feed is
            // filtered by the graph's own inputs, because the export dropped the mask.
            IReadOnlyList<MathTensor> cross = _init.Run(Feed(_init, new Dictionary<string, MathTensor>
            {
                ["memory"] = memory,
                ["mask"] = mask,
            }));
            MathTensor crossK = cross[0];
            MathTensor crossV = cross[1];

            // The self-attention cache starts empty (a zero-length position axis) and is replaced
            // by the step's outputs. The reference aliases: sv = sk.
            MathTensor sk = MathTensor.Float32(Array.Empty<float>(), Layers, b, Heads, 0, HeadDim);
            MathTensor sv = sk;
            var tok = new long[b];
            Array.Fill(tok, Bos);
            var seqs = new List<int>[b];
            var probs = new List<double>[b];
            for (int i = 0; i < b; i++)
            {
                seqs[i] = new List<int>();
                probs[i] = new List<double>();
            }

            var done = new bool[b];
            for (int t = 0; t < MaxTokens - 1; t++)
            {
                IReadOnlyList<MathTensor> step = _step.Run(Feed(_step, new Dictionary<string, MathTensor>
                {
                    ["token"] = MathTensor.Int64(tok, b),
                    ["pos"] = MathTensor.Int64(new[] { (long)t }),
                    ["self_k"] = sk,
                    ["self_v"] = sv,
                    ["cross_k"] = crossK,
                    ["cross_v"] = crossV,
                    ["mask"] = mask,
                }));
                MathTensor logits = step[0];
                sk = step[1];
                sv = step[2];

                int vocab = logits.Dimensions[^1];
                int alive = 0;
                for (int row = 0; row < b; row++)
                {
                    ReadOnlySpan<float> rowLogits = logits.Floats.AsSpan(row * vocab, vocab);
                    int best = Numerics.ArgMax(rowLogits);
                    tok[row] = best;

                    // top = 1.0 / e.sum(-1): the softmax probability of the argmax token, in
                    // float32 (NEP 50 keeps the Python scalar weak, so 1.0 does not widen it).
                    float max = rowLogits[best];
                    var e = new float[vocab];
                    for (int i = 0; i < vocab; i++) e[i] = MathF.Exp(rowLogits[i] - max);
                    float top = 1f / Numerics.Sum32(e);

                    if (done[row]) continue;
                    probs[row].Add(top);
                    if (best == Eos)
                    {
                        done[row] = true;
                    }
                    else if (probs[row].Count >= 8
                             && Numerics.Mean64(CollectionsMarshal.AsSpan(probs[row]))
                                < MinConf - EarlyStopMargin)
                    {
                        done[row] = true;             // rejected: keeps a low mean probability
                    }
                    else if (best >= NSpecial)
                    {
                        seqs[row].Add(best);
                    }

                    if (!done[row]) alive++;
                }

                if (alive == 0) break;
            }

            for (int k = 0; k < b; k++)
            {
                var tokens = new List<string>(seqs[k].Count);
                foreach (int id in seqs[k]) tokens.Add(_itos[id]);
                double confidence = probs[k].Count > 0
                    ? Numerics.Mean64(CollectionsMarshal.AsSpan(probs[k]))
                    : 0.0;
                readings[idx[k]] = new MathReading(Detokenize(tokens), confidence);
            }
        }

        return readings;
    }

    public void Dispose()
    {
        _cls.Dispose();
        _enc.Dispose();
        _init.Dispose();
        _step.Dispose();
    }

    /// <summary>The lines at <paramref name="idx"/> padded to one batch, with their widths.</summary>
    private float[] Batch(IReadOnlyList<float[]> crops, int[] idx, int height, out long[] widths,
                          out int padded)
    {
        var lines = new List<float[]>(idx.Length);
        foreach (int j in idx) lines.Add(crops[j]);
        widths = new long[idx.Length];
        return RecPreproc.PadBatch(lines, height, widths, out padded);
    }

    /// <summary>The reference's <c>{k: v for k, v in feed.items() if k in names}</c>.</summary>
    private static Dictionary<string, MathTensor> Feed(IMathModel model,
                                                       Dictionary<string, MathTensor> all)
    {
        var feed = new Dictionary<string, MathTensor>();
        foreach (KeyValuePair<string, MathTensor> kv in all)
            if (model.InputNames.Contains(kv.Key))
                feed[kv.Key] = kv.Value;
        return feed;
    }

    /// <summary>
    /// The first four columns of a <c>[n, 5]</c> box array (the reference's <c>boxes[:, :4]</c>),
    /// as the contiguous <c>[n, 4]</c> the crop helper takes.
    /// </summary>
    private static float[] Quads(ReadOnlySpan<float> boxes)
    {
        int count = boxes.Length / 5;
        var quads = new float[count * 4];
        for (int i = 0; i < count; i++) boxes.Slice(i * 5, 4).CopyTo(quads.AsSpan(i * 4));
        return quads;
    }
}
