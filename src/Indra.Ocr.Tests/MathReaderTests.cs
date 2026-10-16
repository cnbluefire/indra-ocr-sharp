using System.Text.Json;
using Indra.Ocr.Imaging;
using Indra.Ocr.Maths;
using Indra.Ocr.Onnx;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;
using Microsoft.ML.OnnxRuntime;

namespace Indra.Ocr.Tests;

/// <summary>
/// Parity tests for step 200 — <see cref="MathReader"/>, the port of <c>math.py</c>: the math line
/// classifier and MathRec, the attention decoder that reads a line as text with inline LaTeX (see
/// <c>docs/200-math.md</c>).
/// </summary>
/// <remarks>
/// The fixture's real section runs the four models over the page and the detector's boxes the
/// reference recorded, so the test needs neither <c>det.onnx</c> nor an image codec.
/// <para>
/// Deterministic stand-ins for the four graphs keep the decoding loop under test without the model
/// files, and they check the loop's invariants — the cache shapes, the scalar <c>pos</c>, the feed
/// filtering, the token fed back — by throwing when the port gets one wrong.
/// </para>
/// </remarks>
public class MathReaderTests
{
    private static readonly JsonElement Fx = Fixtures.Load("math.json");

    private static string[] Sorted(IEnumerable<string> names) =>
        names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ------------------------------------------------------------------ the fixture's stand-ins

    /// <summary>
    /// The fixture's stand-in classifier (the generator's <c>StandinCls</c>): p = 0.9 for the first
    /// line of a batch and 0.1 for the rest, so a different batching shows up in the p's.
    /// </summary>
    private sealed class StandInCls : IMathModel
    {
        /// <summary>The batch sizes the classifier was called with, in order.</summary>
        public List<int> Batches { get; } = new();

        public List<string[]> Calls { get; } = new();

        public IReadOnlyList<string> InputNames => new[] { "x", "widths" };

        public IReadOnlyList<MathTensor> Run(IReadOnlyDictionary<string, MathTensor> feed)
        {
            Calls.Add(Sorted(feed.Keys));
            int batch = feed["x"].Dimensions[0];
            Batches.Add(batch);
            var p = new float[batch];
            for (int i = 0; i < batch; i++) p[i] = i == 0 ? 0.9f : 0.1f;
            return new[] { MathTensor.Float32(p, batch) };
        }

        public void Dispose() { }
    }

    /// <summary>The generator's <c>StandinEnc</c>: <c>memory</c> [B, T, C] and <c>mask</c> [B, T].</summary>
    private sealed class StandInEnc : IMathModel
    {
        public const int Steps = 3, Channels = 192;

        public List<string[]> Calls { get; } = new();

        public IReadOnlyList<string> InputNames => new[] { "x", "widths" };

        public IReadOnlyList<MathTensor> Run(IReadOnlyDictionary<string, MathTensor> feed)
        {
            Calls.Add(Sorted(feed.Keys));
            int batch = feed["x"].Dimensions[0];
            return new[]
            {
                MathTensor.Float32(new float[batch * Steps * Channels], batch, Steps, Channels),
                MathTensor.Float32(new float[batch * Steps], batch, Steps),
            };
        }

        public void Dispose() { }
    }

    /// <summary>
    /// The generator's <c>StandinInit</c>: <c>cross_k</c> / <c>cross_v</c> [L, B, H, T, D] from the
    /// memory's shape. Its input names are the export's — the init graph does not read the mask.
    /// </summary>
    private sealed class StandInInit : IMathModel
    {
        public List<string[]> Calls { get; } = new();

        public IReadOnlyList<string> InputNames => new[] { "memory" };

        public IReadOnlyList<MathTensor> Run(IReadOnlyDictionary<string, MathTensor> feed)
        {
            Calls.Add(Sorted(feed.Keys));
            int[] memory = feed["memory"].Dimensions;
            int batch = memory[0], steps = memory[1];
            var k = new float[3 * batch * 8 * steps * 24];
            var v = new float[k.Length];
            Array.Fill(v, 1f);
            return new[]
            {
                MathTensor.Float32(k, 3, batch, 8, steps, 24),
                MathTensor.Float32(v, 3, batch, 8, steps, 24),
            };
        }

        public void Dispose() { }
    }

    /// <summary>
    /// The generator's <c>StandinStep</c>: a scripted decoder. <c>script[r]</c> is the token ids
    /// row <c>r</c> of a batch emits, one per step, or <c>null</c> for a row that emits the
    /// low-probability filler instead (every logit equal, so the reader's peak probability is
    /// <c>1 / vocab</c> and the early stop fires after 8 tokens); a row whose script runs out emits
    /// EOS. It also asserts the loop's invariants, so the port fails loudly when it breaks one.
    /// </summary>
    private sealed class StandInStep : IMathModel
    {
        private const float Hi = 20f;

        private readonly IReadOnlyList<int[]?> _script;
        private readonly int _vocab;

        public StandInStep(IReadOnlyList<int[]?> script, int vocab)
        {
            _script = script;
            _vocab = vocab;
        }

        public List<string[]> Calls { get; } = new();

        public IReadOnlyList<string> InputNames =>
            new[] { "token", "pos", "self_k", "self_v", "cross_k", "cross_v", "mask" };

        public IReadOnlyList<MathTensor> Run(IReadOnlyDictionary<string, MathTensor> feed)
        {
            Calls.Add(Sorted(feed.Keys));
            long[] token = feed["token"].Longs!;
            int pos = (int)feed["pos"].Longs![0];
            int[] cache = feed["self_k"].Dimensions;
            int batch = cache[1];
            if (cache[0] != 3 || cache[2] != 8 || cache[3] != pos || cache[4] != 24)
                throw new InvalidOperationException(
                    $"self_k is {string.Join('x', cache)} at pos {pos}, expected 3x{batch}x8x{pos}x24");

            var logits = new float[batch * _vocab];
            for (int r = 0; r < batch; r++)
            {
                int[]? want = _script[r % _script.Count];
                if (want is null) continue;                       // all equal: top = 1 / vocab
                if (pos < want.Length)
                {
                    logits[r * _vocab + want[pos]] = Hi;
                    if (pos > 0 && token[r] != want[pos - 1])
                        throw new InvalidOperationException(
                            $"row {r} step {pos}: token {token[r]}, expected {want[pos - 1]}");
                }
                else
                {
                    logits[r * _vocab + MathReader.Eos] = Hi;
                }
            }

            var k = new float[3 * batch * 8 * (pos + 1) * 24];
            var v = new float[k.Length];
            Array.Fill(v, 1f);
            return new[]
            {
                MathTensor.Float32(logits, batch, _vocab),
                MathTensor.Float32(k, 3, batch, 8, pos + 1, 24),
                MathTensor.Float32(v, 3, batch, 8, pos + 1, 24),
            };
        }

        public void Dispose() { }
    }

    private static MathReader StandInReader(JsonElement config, JsonElement tokens, IMathModel cls,
                                            IMathModel enc, IMathModel init, IMathModel step)
    {
        var list = new List<string>();
        foreach (var t in tokens.EnumerateArray()) list.Add(t.GetString()!);
        return MathReader.Create(cls, enc, init, step,
                                 height: config.GetProperty("height").GetInt32(),
                                 maxTokens: config.GetProperty("max_tokens").GetInt32(),
                                 layers: config.GetProperty("layers").GetInt32(),
                                 heads: config.GetProperty("heads").GetInt32(),
                                 channels: config.GetProperty("channels").GetInt32(),
                                 tokens: list);
    }

    private static string[] Sets(JsonElement e) =>
        e.EnumerateArray()
         .Select(s => string.Join(",", s.EnumerateArray().Select(n => n.GetString()!)))
         .OrderBy(s => s, StringComparer.Ordinal)
         .ToArray();

    private static string[] Sets(IEnumerable<string[]> calls) =>
        calls.Select(c => string.Join(",", c)).Distinct().OrderBy(s => s, StringComparer.Ordinal)
             .ToArray();

    private static (byte[] Gray, int Width, int Height) Page(JsonElement p) =>
        (B64.ZBytes(p.GetProperty("gray").GetString()!), p.GetProperty("w").GetInt32(),
         p.GetProperty("h").GetInt32());

    private static bool[] Bools(JsonElement e) =>
        e.EnumerateArray().Select(v => v.GetBoolean()).ToArray();

    // ------------------------------------------------------------------ detokenize

    public static IEnumerable<object[]> DetokenizeCases()
    {
        foreach (var c in Fx.GetProperty("detokenize").EnumerateArray())
        {
            var tokens = new List<string>();
            foreach (var t in c.GetProperty("tokens").EnumerateArray()) tokens.Add(t.GetString()!);
            yield return new object[] { tokens, c.GetProperty("text").GetString()! };
        }
    }

    [Theory]
    [MemberData(nameof(DetokenizeCases))]
    public void Detokenize_matches_the_reference(List<string> tokens, string expected) =>
        Assert.Equal(expected, MathReader.Detokenize(tokens));

    [Fact]
    public void Detokenize_keeps_the_unclosed_math() =>
        // `"$" + " ".join(math) + "$"` for a run that never closes, and nothing at all for a
        // lone "$" (the reference only appends when `math` is non-empty).
        Assert.Equal("$x _ 1$", MathReader.Detokenize(new[] { "$", "x", "_", "1" }));

    // ------------------------------------------------------------------ the real models

    /// <summary>The four graphs of <c>temp/math-models</c> (downloaded material, not in git).</summary>
    private sealed class RealReader : IDisposable
    {
        private readonly SessionOptions _options;

        private RealReader(SessionOptions options, MathReader reader)
        {
            _options = options;
            Reader = reader;
        }

        public MathReader Reader { get; }

        public static RealReader? TryOpen()
        {
            if (TestModels.MathDirectory is null)
                return null;
            var options = OnnxSessions.CreateOptions(threads: 1);
            try
            {
                MathReader? reader = MathModels.Load(TestModels.MathDirectory, options);
                if (reader is null)
                {
                    options.Dispose();
                    return null;
                }
                return new RealReader(options, reader);
            }
            catch
            {
                options.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Reader.Dispose();
            _options.Dispose();
        }
    }

    [Fact]
    public void The_reader_reports_the_metadata()
    {
        using var real = RealReader.TryOpen();
        if (real is null) return;                       // temp/math-models is downloaded material

        var cfg = Fx.GetProperty("real").GetProperty("config");
        Assert.Equal(cfg.GetProperty("height").GetInt32(), real.Reader.Height);
        Assert.Equal(cfg.GetProperty("max_tokens").GetInt32(), real.Reader.MaxTokens);
        Assert.Equal(cfg.GetProperty("layers").GetInt32(), real.Reader.Layers);
        Assert.Equal(cfg.GetProperty("heads").GetInt32(), real.Reader.Heads);
        Assert.Equal(cfg.GetProperty("head_dim").GetInt32(), real.Reader.HeadDim);
    }

    [Fact]
    public void The_real_classifier_matches_the_reference()
    {
        using var real = RealReader.TryOpen();
        if (real is null) return;

        var fx = Fx.GetProperty("real");
        var (gray, w, h) = Page(fx.GetProperty("page"));
        var boxes = B64.ZFloats(fx.GetProperty("boxes").GetString()!);
        int recHeight = fx.GetProperty("rec_height").GetInt32();

        // The crops `flags` sees are the recognizer's, made without CenterMask.
        var crops = LineMask.CropLines(new GrayImage(w, h, gray), OrtOcr.Quads(boxes), recHeight);

        float[] expected = B64.ZFloats(fx.GetProperty("probabilities").GetString()!);
        float[] actual = real.Reader.Probabilities(crops, height: recHeight);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == actual[i],
                        $"line {i}: p {actual[i]:R}, expected {expected[i]:R}");

        bool[] flags = real.Reader.Flags(crops, height: recHeight);
        Assert.Equal(Bools(fx.GetProperty("flags")), flags);
        Assert.True(flags.Count(f => f) >= 3, $"only {flags.Count(f => f)} math lines flagged");
    }

    [Fact]
    public void The_real_reader_matches_the_reference()
    {
        using var real = RealReader.TryOpen();
        if (real is null) return;

        var fx = Fx.GetProperty("real");
        var (gray, w, h) = Page(fx.GetProperty("page"));
        var boxes = B64.ZFloats(fx.GetProperty("boxes").GetString()!);

        var readings = real.Reader.Read(new GrayImage(w, h, gray), boxes);
        Assert.Equal(fx.GetProperty("n").GetInt32(), readings.Count);

        int replaced = 0;
        double worst = 0.0;
        foreach (var e in fx.GetProperty("readings").EnumerateArray())
        {
            int index = e.GetProperty("index").GetInt32();
            string text = e.GetProperty("text").GetString()!;
            double conf = e.GetProperty("confidence").GetDouble();
            Assert.True(text == readings[index].Text,
                        $"reading {index}: '{readings[index].Text}', expected '{text}'");
            double relative = Math.Abs(conf - readings[index].Confidence) / Math.Max(Math.Abs(conf), 1e-30);
            worst = Math.Max(worst, relative);
            if (text.Contains('$') && conf >= MathReader.MinConf) replaced++;
        }

        // The text is exact; the confidence is the decoder's mean token probability, and the
        // peak probability of a step (`top`) is a numpy float32 sum, whose SIMD kernel can
        // differ from the port's pairwise tree in the last ulp. The fixture's worst is 3.5e-9
        // relative (see docs/200-math.md); the mean itself is bit-exact (NumericsTests).
        Assert.True(worst <= 1e-6, $"worst relative confidence difference {worst:R}");
        Assert.True(replaced >= 2, $"only {replaced} readings would replace the line");
    }

    // ------------------------------------------------------------------ the stand-ins

    [Fact]
    public void The_stand_in_classifier_matches_the_reference()
    {
        var fx = Fx.GetProperty("standin").GetProperty("flags");
        var crops = new List<float[]>();
        foreach (var c in fx.GetProperty("crops").EnumerateArray())
            crops.Add(B64.ZFloats(c.GetProperty("f").GetString()!));

        var cls = new StandInCls();
        using var reader = StandInReader(fx.GetProperty("config"), fx.GetProperty("tokens"), cls,
                                         new StandInEnc(), new StandInInit(),
                                         new StandInStep(Array.Empty<int[]>(), 14));

        float[] expected = B64.ZFloats(fx.GetProperty("probabilities").GetString()!);
        float[] actual = reader.Probabilities(crops, height: 32);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == actual[i],
                        $"crop {i}: p {actual[i]:R}, expected {expected[i]:R}");

        // The batches of that one run: one classifier call per batch, with the batch sizes the
        // fixture recorded (compared before `Flags`, which runs the classifier again).
        int[] batches = fx.GetProperty("batches").EnumerateArray()
                          .Select(b => b.GetArrayLength()).ToArray();
        Assert.Equal(batches, cls.Batches);
        Assert.All(cls.Calls, c => Assert.Equal(new[] { "widths", "x" }, c));

        Assert.Equal(Bools(fx.GetProperty("flags")), reader.Flags(crops, height: 32));
    }

    [Fact]
    public void The_stand_in_decoder_matches_the_reference()
    {
        var fx = Fx.GetProperty("standin").GetProperty("read");
        var script = new List<int[]?>();
        foreach (var s in fx.GetProperty("script").EnumerateArray())
        {
            script.Add(s.ValueKind == JsonValueKind.Null
                ? null
                : s.EnumerateArray().Select(v => v.GetInt32()).ToArray());
        }

        var enc = new StandInEnc();
        var init = new StandInInit();
        var step = new StandInStep(script, vocab: fx.GetProperty("tokens").GetArrayLength() + 4);
        using var reader = StandInReader(fx.GetProperty("config"), fx.GetProperty("tokens"),
                                         new StandInCls(), enc, init, step);

        var (gray, w, h) = Page(fx.GetProperty("page"));
        var boxes = B64.ZFloats(fx.GetProperty("boxes").GetString()!);
        var readings = reader.Read(new GrayImage(w, h, gray), boxes);

        var expected = fx.GetProperty("readings").EnumerateArray().ToList();
        Assert.Equal(expected.Count, readings.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].GetProperty("text").GetString() == readings[i].Text,
                        $"reading {i}: '{readings[i].Text}', " +
                        $"expected '{expected[i].GetProperty("text").GetString()}'");
            // Every probability in the script is dyadic or 1/vocab, so the mean is exact.
            Assert.Equal(expected[i].GetProperty("confidence").GetDouble(), readings[i].Confidence);
        }

        var calls = fx.GetProperty("calls");
        Assert.Equal(Sets(calls.GetProperty("enc")), Sets(enc.Calls));
        Assert.Equal(Sets(calls.GetProperty("init")), Sets(init.Calls));
        Assert.Equal(Sets(calls.GetProperty("step")), Sets(step.Calls));
    }
}
