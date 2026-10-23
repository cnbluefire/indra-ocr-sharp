using System.Text.Json;
using Indra.Ocr;
using Indra.Ocr.Imaging;
using Indra.Ocr.Onnx;
using Indra.Ocr.Pipeline;
using Microsoft.ML.OnnxRuntime;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for the page-uprighting half of the pipeline (<c>indra_ocr/pipeline.py</c>):
/// <see cref="Orientation.OrientInput"/>, <see cref="Orientation.Turns"/>,
/// <see cref="Orientation.EstimateSkew"/>, <see cref="Orientation.RotatePage"/>,
/// <see cref="Orientation.Rotate90"/>, <see cref="Orientation.UnrotateBox"/> and
/// <see cref="Orientation.Upright"/> �?see <c>docs/090-orientation-deskew.md</c>.
/// </summary>
/// <remarks>
/// The pages are real (<c>temp/inputs/*.png</c>) or synthetic with a known tilt, so the deskew
/// search has to find the angle the generator put in. The orientation cases run the <b>real</b>
/// <c>orient.onnx</c> through <see cref="OnnxOrientModel"/> when the model folder is present; a
/// stand-in model (the mean ink of each quadrant) keeps the logic under test without it.
/// </remarks>
public class OrientationTests
{
    private static readonly JsonElement Fx = Fixtures.Load("orient.json");

    /// <summary>The fixture's stand-in for <c>orient.onnx</c>: the mean ink of each quadrant.</summary>
    private sealed class StandIn : IOrientModel
    {
        public void Run(ReadOnlySpan<float> x, Span<float> p)
        {
            int side = (int)Math.Sqrt(x.Length);
            int h = side / 2;
            Span<float> q = stackalloc float[h * h];
            float[] means = new float[4];
            for (int k = 0; k < 4; k++)
            {
                int y0 = (k >> 1) * h, x0 = (k & 1) * h;
                for (int y = 0; y < h; y++)
                    x.Slice((y0 + y) * side + x0, h).CopyTo(q.Slice(y * h));
                means[k] = Numerics.Mean32(q);
            }
            means.CopyTo(p);
        }
    }

    // ------------------------------------------------------------------ orient_input

    [Fact]
    public void Orient_input_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("orient_input").EnumerateArray())
        {
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);
            int side = c.GetProperty("side").GetInt32();
            var expected = B64.ZFloats(c.GetProperty("ink").GetString()!);
            Assert.Equal(side * side, expected.Length);

            var ink = new float[side * side];
            Orientation.OrientInput(new GrayImage(w, h, page), ink, side);
            AssertSameFloats(expected, ink, c.GetProperty("name").GetString()!);
            cases++;
        }
        Assert.True(cases >= 9, $"only {cases} orient_input cases");
    }

    // ------------------------------------------------------------------ orientation model

    [Fact]
    public void Turns_match_the_reference()
    {
        if (TestModels.Directory is null || !File.Exists(Path.Combine(TestModels.Directory, "orient.onnx")))
            return;   // resources/models is externally downloaded; the fixture is committed
        using var options = OnnxSessions.CreateOptions(1);
        using var session = OnnxSessions.Open(Path.Combine(TestModels.Directory, "orient.onnx"), options);
        var model = new OnnxOrientModel(session);

        int cases = 0, turns = 0;
        Span<float> p = stackalloc float[4];
        foreach (var c in Fx.GetProperty("turns").EnumerateArray())
        {
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);
            string name = c.GetProperty("name").GetString()!;
            var gray = new GrayImage(w, h, page);

            var expected = Fixtures.FloatArray(c.GetProperty("p"));
            var ink = new float[Orientation.OrientSide * Orientation.OrientSide];
            Orientation.OrientInput(gray, ink);
            model.Run(ink, p);
            AssertSameFloats(expected, p, name + " p");
            Assert.Equal(c.GetProperty("turns").GetInt32(), Orientation.Turns(model, gray));
            if (c.GetProperty("turns").GetInt32() != 0) turns++;
            cases++;
        }
        Assert.True(cases >= 14, $"only {cases} turns cases");
        Assert.True(turns >= 3, $"only {turns} cases needed a turn");
    }

    [Fact]
    public void Turns_match_the_stand_in_model()
    {
        int cases = 0, turns = 0;
        Span<float> p = stackalloc float[4];
        foreach (var c in Fx.GetProperty("turns_standin").EnumerateArray())
        {
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);
            string name = c.GetProperty("name").GetString()!;
            var expected = Fixtures.FloatArray(c.GetProperty("p"));
            var ink = new float[Orientation.OrientSide * Orientation.OrientSide];
            Orientation.OrientInput(new GrayImage(w, h, page), ink);
            new StandIn().Run(ink, p);

            // The stand-in's quadrant means are numpy's *strided* 2-D reduction, which is a
            // different iteration from the port's contiguous pairwise tree and differs in the
            // last ulp (docs/090). The decision is asserted exactly.
            for (int k = 0; k < 4; k++)
                Assert.True(Math.Abs(p[k] - expected[k]) <= 1e-5f,
                            $"{name}: p[{k}] = {p[k]:R}, expected {expected[k]:R}");

            Assert.Equal(c.GetProperty("turns").GetInt32(),
                         Orientation.Turns(new StandIn(), new GrayImage(w, h, page)));
            turns += c.GetProperty("turns").GetInt32();
            cases++;
        }
        Assert.True(cases >= 14, $"only {cases} standin cases");
        // The stand-in's quadrant means never reach ORIENT_MIN on these pages, so every case
        // must stay upright (the real model's turns are asserted in the test above).
        Assert.Equal(0, turns);
    }

    [Fact]
    public void Turns_uses_the_first_maximum_and_the_float32_threshold()
    {
        // p[k] > 0.95 is compared in float32 (NEP 50), and argmax keeps the first maximum.
        Assert.Equal(1, Orientation.Turns(new Fixed([0.96f, 0.99f, 0.99f, 0.99f]), BlankPage()));
        Assert.Equal(0, Orientation.Turns(new Fixed([0.99f, 0.96f, 0.99f, 0.96f]), BlankPage()));
        Assert.Equal(1, Orientation.Turns(new Fixed([0.0f, 0.96f, 0.0f, 0.0f]), BlankPage()));
        Assert.Equal(3, Orientation.Turns(new Fixed([0f, 0f, 0f, 1f]), BlankPage()));
        // 0.95f is 0.949999988079071, so exactly-0.95f does not turn (the comparison is strict).
        Assert.Equal(0, Orientation.Turns(new Fixed([0f, 0.95f, 0f, 0f]), BlankPage()));
        Assert.Equal(1, Orientation.Turns(new Fixed([0f, 0.9500001f, 0f, 0f]), BlankPage()));
        // A tie at the maximum keeps the lowest index, exactly like numpy's argmax.
        Assert.Equal(2, Orientation.Turns(new Fixed([0.1f, 0.1f, 0.99f, 0.99f]), BlankPage()));
    }

    private sealed class Fixed : IOrientModel
    {
        private readonly float[] _p;

        public Fixed(float[] p) => _p = p;

        public void Run(ReadOnlySpan<float> x, Span<float> p)
        {
            Assert.Equal(Orientation.OrientSide * Orientation.OrientSide, x.Length);
            new ReadOnlySpan<float>(_p).CopyTo(p);
        }
    }

    private static readonly byte[] BlankPixels = new byte[8 * 8];

    private static GrayImage BlankPage()
    {
        Array.Fill(BlankPixels, (byte)255);
        return new GrayImage(8, 8, BlankPixels);
    }

    // ------------------------------------------------------------------ estimate_skew

    [Fact]
    public void Estimate_skew_matches_the_reference()
    {
        // The thumbnail is bit-exact, and since step 155 so are the sharpness values (numpy's
        // contiguous float32 reduction is the pairwise tree the port transcribes; docs/090). The
        // tolerance below is headroom for the scalar WarpAffine32f, and its observed deviation
        // over the fixture is 0.
        // The angle the search returns �?the thing the pipeline uses �?is compared exactly.
        int cases = 0, profiled = 0;
        double worst = 0;
        string worstAt = "";
        foreach (var c in Fx.GetProperty("skew").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);
            int side = c.GetProperty("side").GetInt32();
            var gray = new GrayImage(w, h, page);
            cases++;

            var ink = Orientation.SkewThumbnail(gray, side, out int tw, out int th);
            var thumb = c.GetProperty("thumbnail");
            Assert.Equal(thumb.GetProperty("w").GetInt32(), tw);
            Assert.Equal(thumb.GetProperty("h").GetInt32(), th);
            if (c.TryGetProperty("ink", out var stored))
                AssertSameFloats(B64.ZFloats(stored.GetString()!), ink, name + " thumbnail");

            // The blank-page branch: numpy's mean and the port's disagree in the last ulp, but
            // the decision does not move.
            bool blank = Numerics.Mean32(ink) < Orientation.BlankInk;
            Assert.Equal(c.GetProperty("blank").GetBoolean(), blank);

            Assert.Equal(c.GetProperty("angle").GetDouble(), Orientation.EstimateSkew(gray, side));

            if (blank) continue;   // the reference stops before searching
            foreach (string grid in new[] { "coarse", "fine" })
            {
                var angles = new List<double>();
                var values = new List<float>();
                foreach (var pair in c.GetProperty(grid).EnumerateArray())
                {
                    double angle = pair[0].GetDouble();
                    float expected = pair[1].GetSingle();
                    float actual = Orientation.Sharpness(ink, tw, th, angle);
                    angles.Add(angle);
                    values.Add(actual);
                    double dev = Math.Abs(actual - expected) / Math.Max(1.0, Math.Abs(expected));
                    if (dev > worst) { worst = dev; worstAt = $"{name} {grid} {angle}"; }
                }
                // ... and the ranking over the grid, which is what the search acts on.
                int mine = 0;
                for (int i = 1; i < values.Count; i++) if (values[i] > values[mine]) mine = i;
                int reference = 0;
                for (int i = 1; i < values.Count; i++)
                    if (c.GetProperty(grid)[i][1].GetSingle() > c.GetProperty(grid)[reference][1].GetSingle())
                        reference = i;
                Assert.True(mine == reference,
                            $"{name} {grid}: sharpness peak at {angles[mine]}, expected {angles[reference]}");
            }
            profiled++;
        }
        Assert.True(cases >= 12, $"only {cases} skew cases");
        Assert.True(profiled >= 9, $"only {profiled} skew cases had a profile to rank");
        Assert.True(worst <= 1e-5, $"sharpness deviates by {worst:E2} ({worstAt})");
    }

    [Fact]
    public void Estimate_skew_searches_coarse_then_half_then_tenth_degrees()
    {
        var coarse = NpyArange.Range(-Orientation.DeskewMax, Orientation.DeskewMax + 1e-6, 2.0);
        Assert.Equal(46, coarse.Length);
        Assert.Equal([-45.0, 45.0], new[] { coarse[0], coarse[^1] });
        var fine = NpyArange.Range(-0.5, 0.5 + 1e-6, 0.1);
        Assert.Equal(11, fine.Length);
        Assert.Equal(-1.1102230246251565e-16, fine[5]);   // not exactly 0: delta = (-0.4) - (-0.5)
        Assert.Equal(0.4999999999999998, fine[^1]);
    }

    // ------------------------------------------------------------------ rotations

    [Fact]
    public void Rotate_page_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("rotate_page").EnumerateArray())
        {
            var (page, w, h) = Page(c.GetProperty("page").GetString()!);
            var expected = B64.ZBytes(c.GetProperty("out").GetString()!);
            var actual = new byte[w * h];
            Orientation.RotatePage(new GrayImage(w, h, page), c.GetProperty("degrees").GetDouble(),
                                   actual, w);
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i])
                    Assert.Fail($"{c.GetProperty("name").GetString()} [{i / w},{i % w}]: " +
                                $"{actual[i]}, expected {expected[i]}");
            cases++;
        }
        Assert.True(cases >= 16, $"only {cases} rotate_page cases");
    }

    [Fact]
    public void Rotate90_matches_numpy()
    {
        // np.rot90 of a 3x4 array, for all four quarter turns (docs/090 for the index maps).
        var m = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        var src = new GrayImage(4, 3, m);
        var expected = new Dictionary<int, (int W, int H, byte[] Pixels)>
        {
            [0] = (4, 3, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]),
            [1] = (3, 4, [3, 7, 11, 2, 6, 10, 1, 5, 9, 0, 4, 8]),      // counter-clockwise
            [2] = (4, 3, [11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0]),
            [3] = (3, 4, [8, 4, 0, 9, 5, 1, 10, 6, 2, 11, 7, 3]),      // clockwise
        };
        foreach (var (k, want) in expected)
        {
            var dst = new byte[12];
            Orientation.Rotate90(src, dst, k);
            Assert.Equal(want.Pixels, dst);
            // A second rotation must compose, too (used by `upright` for -k).
            if (k != 0)
            {
                var round = new byte[12];
                Orientation.Rotate90(new GrayImage(want.W, want.H, want.Pixels), round, 4 - k);
                Assert.Equal(m, round);
            }
        }
    }

    // ------------------------------------------------------------------ unrotate_box

    [Fact]
    public void Unrotate_box_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("unrotate_box").EnumerateArray())
        {
            var box = Fixtures.FloatArray(c.GetProperty("box"));
            var expected = Fixtures.DoubleArray(c.GetProperty("out"));
            var actual = new double[4];
            Orientation.UnrotateBox(box, c.GetProperty("degrees").GetDouble(),
                                    c.GetProperty("w").GetInt32(), c.GetProperty("h").GetInt32(), actual);
            Assert.Equal(expected, actual);
            cases++;
        }
        Assert.True(cases >= 400, $"only {cases} unrotate_box cases");
    }

    [Fact]
    public void Round_tenth_matches_python()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("round").EnumerateArray())
        {
            float v = c.GetProperty("v").GetSingle();
            double expected = c.GetProperty("out").GetDouble();
            double actual = Orientation.RoundTenth(v);
            // -0.0 == 0.0, so compare bits to keep the sign honest.
            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                        $"round({v:R}, 1) = {actual:R}, expected {expected:R}");
            cases++;
        }
        Assert.True(cases >= 20, $"only {cases} round cases");
    }

    // ------------------------------------------------------------------ upright

    [Fact]
    public void Upright_matches_the_reference()
    {
        InferenceSession? session = null;
        SessionOptions? options = null;
        if (TestModels.Directory is not null && File.Exists(Path.Combine(TestModels.Directory, "orient.onnx")))
        {
            options = OnnxSessions.CreateOptions(1);
            session = OnnxSessions.Open(Path.Combine(TestModels.Directory, "orient.onnx"), options);
        }
        try
        {
            int cases = 0, considered = 0;
            foreach (var c in Fx.GetProperty("upright").EnumerateArray())
            {
                string kind = c.GetProperty("model").GetString()!;
                if (kind == "real" && session is null) continue;   // no model folder
                considered++;
                IOrientModel? model = kind switch
                {
                    "real" => new OnnxOrientModel(session!),
                    "standin" => new StandIn(),
                    _ => null,
                };
                var (page, w, h) = Page(c.GetProperty("page").GetString()!);
                string name = c.GetProperty("name").GetString()!;

                var result = Orientation.Upright(model, new GrayImage(w, h, page),
                                                 Orientation.OrientSide,
                                                 c.GetProperty("side").GetInt32(),
                                                 out int gotW, out int gotH,
                                                 out int undone, out double skew);
                Assert.True(c.GetProperty("undone").GetInt32() == undone,
                            $"{name}: undone {undone}, expected {c.GetProperty("undone").GetInt32()}");
                Assert.True(c.GetProperty("skew").GetDouble() == skew,
                            $"{name}: skew {skew:R}, expected {c.GetProperty("skew").GetDouble():R}");

                int wantW = c.GetProperty("w").GetInt32(), wantH = c.GetProperty("h").GetInt32();
                Assert.Equal(wantW, gotW);
                Assert.Equal(wantH, gotH);
                if (result is null)
                {
                    // Nothing to do: the reference hands back the very same array.
                    Assert.Equal(0, undone);
                    Assert.Equal(0.0, skew);
                    continue;
                }
                cases++;
                var expected = B64.ZBytes(c.GetProperty("out").GetString()!);
                Assert.Equal(expected.Length, result.Length);
                for (int i = 0; i < expected.Length; i++)
                    if (expected[i] != result[i])
                        Assert.Fail($"{name} [{i / wantW},{i % wantW}]: {result[i]}, expected {expected[i]}");
            }
            Assert.True(considered >= (session is null ? 6 : 14),
                        $"only {considered} upright cases were considered");
            Assert.True(cases >= (session is null ? 1 : 6),
                        $"only {cases} upright cases changed the page");
        }
        finally
        {
            session?.Dispose();
            options?.Dispose();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertSameFloats(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string tag)
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

    private static (byte[] Gray, int Width, int Height) Page(string name)
    {
        var page = Fx.GetProperty("pages").GetProperty(name);
        return (B64.ZBytes(page.GetProperty("gray").GetString()!),
                page.GetProperty("w").GetInt32(),
                page.GetProperty("h").GetInt32());
    }
}
