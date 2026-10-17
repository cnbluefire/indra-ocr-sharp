using System.Text.Json;
using Indra.Ocr.Det;
using Indra.Ocr.Imaging;
using Indra.Ocr.Onnx;
using Microsoft.ML.OnnxRuntime;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for <c>pipeline.OrtOCR.detect</c> (<see cref="Detect"/>): one detection pass
/// (<see cref="Detect.DetectAt"/>), the adaptive second pass (<see cref="Detect.AdaptiveLongSide"/>)
/// and the scale/clip tail (<see cref="Detect.Run"/>) — see <c>docs/095-adaptive-detect.md</c>.
/// </summary>
/// <remarks>
/// The fixture's real cases run the real <c>det.onnx</c> through <see cref="OnnxDetModel"/>; a
/// deterministic stand-in (bars at a fixed height and distance) needs no model file and pins the
/// branch logic — including the 2048 cap, the "candidate equals the first pass" boundary and the
/// clipping — on every machine.
/// </remarks>
public class DetectTests
{
    private static readonly JsonElement Fx = Fixtures.Load("adaptive_detect.json");

    /// <summary>
    /// The fixture's stand-in for <c>det.onnx</c>: <c>bars</c> horizontal bars of fixed height,
    /// core logit 10 and both distance channels at <c>dist</c>, so every box decodes
    /// <c>2 * dist</c> tall. <c>edge</c> spans the full width so the clipping matters. Mirrors
    /// <c>gen_detect_fixtures.py</c>'s <c>StandinDet</c> exactly (integer arithmetic only).
    /// </summary>
    private sealed class StandIn : IDetModel
    {
        private readonly int _bars;
        private readonly float _dist;
        private readonly bool _edge;

        public StandIn(int bars, float dist, bool edge)
        {
            _bars = bars;
            _dist = dist;
            _edge = edge;
        }

        public void Run(ReadOnlySpan<float> x, int height, int width, Span<float> logits)
        {
            logits.Clear();
            int oh = height / DetPost.OutStride, ow = width / DetPost.OutStride;
            int hw = oh * ow;
            if (_bars <= 0) return;
            int gap = Math.Max(2, oh / (_bars * 3));
            int hb = Math.Max(1, oh / (_bars * 4));
            for (int i = 0; i < _bars; i++)
            {
                int y = 2 + i * (hb + gap);
                if (y + hb > oh) break;
                int x0 = _edge ? 0 : ow / 8;
                int x1 = _edge ? ow : ow - ow / 8;
                for (int yy = y; yy < y + hb; yy++)
                {
                    int row = yy * ow;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        logits[row + xx] = 10f;
                        logits[hw + row + xx] = _dist;
                        logits[2 * hw + row + xx] = _dist;
                    }
                }
            }
        }
    }

    /// <summary>Opens the shipped <c>det.onnx</c>, or reports that the model folder is absent.</summary>
    private sealed class DetSession : IDisposable
    {
        private readonly SessionOptions _options;

        private DetSession(InferenceSession session, SessionOptions options)
        {
            Session = session;
            _options = options;
        }

        public InferenceSession Session { get; }

        public static DetSession? TryOpen()
        {
            if (TestModels.Directory is null ||
                !File.Exists(Path.Combine(TestModels.Directory, "det.onnx")))
                return null;   // resources/models is externally downloaded; the fixture is committed
            var options = OnnxSessions.CreateOptions(1);
            try
            {
                return new DetSession(OnnxSessions.Open(Path.Combine(TestModels.Directory, "det.onnx"), options),
                                      options);
            }
            catch
            {
                options.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Session.Dispose();
            _options.Dispose();
        }
    }

    private static IEnumerable<JsonElement> AllCases()
    {
        foreach (var c in Fx.GetProperty("cases").EnumerateArray()) yield return c;
        foreach (var c in Fx.GetProperty("standin").EnumerateArray()) yield return c;
    }

    /// <summary>The model a case was generated with (null when a real case meets no model folder).</summary>
    private static IDetModel? ModelFor(JsonElement c, InferenceSession? session)
    {
        if (c.GetProperty("model").GetString() == "real")
            return session is null ? null : new OnnxDetModel(session);
        var s = c.GetProperty("standin");
        return new StandIn(s.GetProperty("bars").GetInt32(), s.GetProperty("dist").GetSingle(),
                           s.GetProperty("edge").GetBoolean());
    }

    private static (byte[] Gray, int Width, int Height) Page(string name)
    {
        var p = Fx.GetProperty("pages").GetProperty(name);
        return (B64.ZBytes(p.GetProperty("gray").GetString()!),
                p.GetProperty("w").GetInt32(), p.GetProperty("h").GetInt32());
    }

    [Fact]
    public void Detect_at_matches_the_reference()
    {
        using var det = DetSession.TryOpen();
        int cases = 0, boxes = 0;
        foreach (var c in AllCases())
        {
            var model = ModelFor(c, det?.Session);
            if (model is null) continue;
            string name = c.GetProperty("name").GetString()!;
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);

            var expected = B64.ZFloats(c.GetProperty("boxes0").GetString()!);
            var actual = Detect.DetectAt(model, new GrayImage(w, h, page),
                                         c.GetProperty("long_side").GetInt32(), out double scale);
            AssertSameFloats(expected, actual, name + " boxes0");
            Assert.Equal(c.GetProperty("scale0").GetDouble(), scale);
            Assert.Equal(c.GetProperty("n0").GetInt32(), actual.Length / 5);
            boxes += actual.Length / 5;
            cases++;
        }
        // The floors are coverage guards, and how much the fixture can cover depends on the model
        // folder: `det.onnx` is externally downloaded (`resources/models`), so a machine without it
        // runs the stand-in cases only. `DetSession.TryOpen` answers null only when the file is
        // absent -- a load failure throws -- so the lower floor cannot hide a broken model.
        Assert.True(cases >= (det is null ? 10 : 20), $"only {cases} cases");
        Assert.True(boxes >= (det is null ? 50 : 100), $"only {boxes} boxes");
    }

    [Fact]
    public void Adaptive_long_side_matches_the_reference()
    {
        // This one needs no model at all: the rule only looks at the first pass's boxes.
        int cases = 0, reruns = 0, noRerun = 0, gates = 0;
        foreach (var c in AllCases())
        {
            string name = c.GetProperty("name").GetString()!;
            var boxes0 = B64.ZFloats(c.GetProperty("boxes0").GetString()!);
            int longSide = c.GetProperty("long_side").GetInt32();
            int expectedLs = c.GetProperty("ls").GetInt32();
            bool adaptive = c.GetProperty("adaptive").GetBoolean();

            int ls = Detect.AdaptiveLongSide(boxes0, longSide, out double median);
            if (adaptive)
            {
                Assert.True(ls == expectedLs, $"{name}: ls {ls}, expected {expectedLs}");

                var expectedMedian = c.GetProperty("median_h");
                if (expectedMedian.ValueKind == JsonValueKind.Null)
                    Assert.True(double.IsNaN(median), $"{name}: median {median}, expected none");
                else
                    Assert.True(median == expectedMedian.GetDouble(),
                                $"{name}: median {median:R}, expected {expectedMedian.GetDouble():R}");
            }
            else
            {
                // detect() skips the whole branch, but the rule itself does not know the flag.
                Assert.Equal(longSide, expectedLs);
                Assert.True(ls >= longSide && !double.IsNaN(median), $"{name}: ls {ls}");
            }

            if (ls != longSide) reruns++;
            else if (adaptive && boxes0.Length / 5 >= 8 && !double.IsNaN(median)
                     && median < Detect.AdaptiveMinH) noRerun++;
            if (c.GetProperty("model").GetString() == "standin" &&
                (boxes0.Length / 5 < 8 || !adaptive)) gates++;
            cases++;
        }
        Assert.True(cases >= 20, $"only {cases} cases");
        Assert.True(reruns >= 5, $"only {reruns} cases needed a second pass");
        Assert.True(noRerun >= 1, "no case exercised the second-pass candidate that lost");
        Assert.True(gates >= 3, $"only {gates} stand-in cases hit a gate");
    }

    [Fact]
    public void Adaptive_long_side_gates_and_bounds()
    {
        // Eight boxes; only the median decides.  y0 = 0 keeps every height exact in float32.
        static float[] Boxes(params float[] heights)
        {
            var b = new float[heights.Length * 5];
            for (int i = 0; i < heights.Length; i++) b[i * 5 + 3] = heights[i];
            return b;
        }

        static float[] Uniform(float h, int n) => Boxes(Enumerable.Repeat(h, n).ToArray());

        // 7 boxes is not enough to adapt, however short they are.
        Assert.Equal(1024, Detect.AdaptiveLongSide(Uniform(1f, 7), 1024, out double med7));
        Assert.True(double.IsNaN(med7));
        // A median at (or above) ADAPTIVE_MIN_H already has the target height.
        Assert.Equal(1024, Detect.AdaptiveLongSide(Uniform(9f, 8), 1024, out double med9));
        Assert.Equal(9.0, med9);
        Assert.Equal(1024, Detect.AdaptiveLongSide(Uniform(30f, 40), 1024, out _));
        // Just under: 1024 * 12 / 8.9 = 1380.7 -> 1380 -> 43 * 32 = 1376.
        Assert.Equal(1376, Detect.AdaptiveLongSide(Uniform(8.9f, 8), 1024, out _));
        // A page with no lines: the median of the box heights is float32, and one below 1.0 is
        // clamped to 1.0, so the candidate saturates at long_side * 12.
        Assert.Equal(2048, Detect.AdaptiveLongSide(Uniform(0.5f, 8), 1024, out double med05));
        Assert.Equal(0.5, med05);
        Assert.Equal(2048, Detect.AdaptiveLongSide(Uniform(0.5f, 8), 256, out _));
        // The cap and the equal boundary.
        Assert.Equal(2048, Detect.AdaptiveLongSide(Uniform(6f, 8), 1024, out _));
        Assert.Equal(2048, Detect.AdaptiveLongSide(Uniform(6f, 8), 2048, out _));
    }

    [Fact]
    public void Detect_matches_the_reference()
    {
        using var det = DetSession.TryOpen();
        int cases = 0, reruns = 0, clipped = 0;
        foreach (var c in AllCases())
        {
            var model = ModelFor(c, det?.Session);
            if (model is null) continue;
            string name = c.GetProperty("name").GetString()!;
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);

            var expected = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            var actual = Detect.Run(model, new GrayImage(w, h, page),
                                    c.GetProperty("long_side").GetInt32(),
                                    c.GetProperty("adaptive").GetBoolean());
            AssertSameFloats(expected, actual, name + " boxes");
            Assert.Equal(c.GetProperty("n").GetInt32(), actual.Length / 5);
            if (c.GetProperty("ls").GetInt32() != c.GetProperty("long_side").GetInt32()) reruns++;
            if (c.GetProperty("clipped").GetInt32() > 0) clipped++;
            cases++;
        }
        Assert.True(cases >= (det is null ? 10 : 20), $"only {cases} cases");
        Assert.True(reruns >= 4, $"only {reruns} cases ran the adaptive second pass");
        Assert.True(clipped >= 2, $"only {clipped} cases had a box clipped");
    }

    [Fact]
    public void Detect_defaults_to_adaptive()
    {
        // The pipeline calls detect() with self.adaptive; the default is the same on unless asked.
        var c = Fx.GetProperty("standin").EnumerateArray().First();
        var s = c.GetProperty("standin");
        var model = new StandIn(s.GetProperty("bars").GetInt32(), s.GetProperty("dist").GetSingle(),
                                s.GetProperty("edge").GetBoolean());
        var (page, w, h) = Page(c.GetProperty("page").GetString()!);
        var withDefault = Detect.Run(model, new GrayImage(w, h, page),
                                     c.GetProperty("long_side").GetInt32());
        var explicitOn = Detect.Run(model, new GrayImage(w, h, page),
                                    c.GetProperty("long_side").GetInt32(), true);
        Assert.Equal(withDefault, explicitOn);
    }

    private static void AssertSameFloats(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual,
                                         string tag)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i]) continue;
            if (float.IsNaN(expected[i]) && float.IsNaN(actual[i])) continue;
            Assert.Fail($"{tag}[{i}]: {actual[i]:R} (0x{BitConverter.SingleToUInt32Bits(actual[i]):X8}), " +
                        $"expected {expected[i]:R} (0x{BitConverter.SingleToUInt32Bits(expected[i]):X8})");
        }
    }
}
