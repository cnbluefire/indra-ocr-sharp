using System.Text.Json;

namespace Indra.Ocr.Tests;

public class NumericsTests
{
    private static readonly JsonElement Fx = Fixtures.Load("numerics.json");

    [Fact]
    public void Median_matches_numpy()
    {
        foreach (var c in Fx.GetProperty("median").EnumerateArray())
        {
            var input = Fixtures.FloatArray(c.GetProperty("input"));
            Assert.Equal(c.GetProperty("expected").GetDouble(), Numerics.Median(input), 9);
        }
    }

    [Fact]
    public void Percentile_matches_numpy()
    {
        foreach (var c in Fx.GetProperty("percentile").EnumerateArray())
        {
            var input = Fixtures.DoubleArray(c.GetProperty("input"));
            double q = c.GetProperty("q").GetDouble();
            Assert.Equal(c.GetProperty("expected").GetDouble(), Numerics.Percentile(input, q), 9);
        }
    }

    [Fact]
    public void Argsort_matches_numpy()
    {
        foreach (var c in Fx.GetProperty("argsort").EnumerateArray())
        {
            var input = Fixtures.FloatArray(c.GetProperty("input"));
            Assert.Equal(Fixtures.IntArray(c.GetProperty("expected")), Numerics.ArgSort(input));
        }
    }

    [Fact]
    public void Argsort_ties_keep_index_order()
    {
        // Our sort is stable. numpy's default quicksort is a CPU-specific SIMD network and
        // returns a *different*, machine-dependent order for exactly-equal keys (on this
        // machine: [6,1,4,3,2,0,5]). See AGENTS.md, "Known, principled deviation".
        var c = Fx.GetProperty("argsort_ties");
        var input = Fixtures.FloatArray(c.GetProperty("input"));
        Assert.Equal(new[] { 6, 1, 4, 3, 0, 2, 5 }, Numerics.ArgSort(input));
    }

    [Fact]
    public void Argmax_argmin_match_numpy()
    {
        foreach (var c in Fx.GetProperty("argmax").EnumerateArray())
        {
            var input = Fixtures.FloatArray(c.GetProperty("input"));
            Assert.Equal(c.GetProperty("argmax").GetInt32(), Numerics.ArgMax(input));
            Assert.Equal(c.GetProperty("argmin").GetInt32(), Numerics.ArgMin(input));
        }
    }

    [Fact]
    public void Bincount_matches_numpy()
    {
        foreach (var c in Fx.GetProperty("bincount").EnumerateArray())
        {
            var x = Fixtures.IntArray(c.GetProperty("x"));
            int minLength = c.GetProperty("minlength").GetInt32();
            var got = Numerics.Bincount(x, minLength);
            var exp = c.GetProperty("expected").EnumerateArray().Select(v => v.GetInt64()).ToArray();
            Assert.Equal(exp, got);
        }
    }

    [Fact]
    public void Bincount_weighted_matches_numpy()
    {
        foreach (var c in Fx.GetProperty("bincount_weighted").EnumerateArray())
        {
            var x = Fixtures.IntArray(c.GetProperty("x"));
            var w = Fixtures.FloatArray(c.GetProperty("w"));
            int minLength = c.GetProperty("minlength").GetInt32();
            var got = Numerics.Bincount(x, w, minLength);
            var exp = Fixtures.DoubleArray(c.GetProperty("expected"));
            Assert.Equal(exp.Length, got.Length);
            for (int i = 0; i < exp.Length; i++) Assert.Equal(exp[i], got[i], 9);
        }
    }

    [Fact]
    public void LogAddExp_matches_numpy()
    {
        foreach (var c in Fx.GetProperty("logaddexp").EnumerateArray())
        {
            double x = c.GetProperty("x").GetDouble();
            double y = c.GetProperty("y").GetDouble();
            Assert.Equal(c.GetProperty("expected").GetDouble(), Numerics.LogAddExp(x, y), 9);
        }
    }

    [Fact]
    public void Npy_exp_matches_numpy_float32()
    {
        // numpy's exp is its own vector kernel, not the CRT's expf, and it is ~40% likely to
        // differ by 1 ulp: the port reproduces the kernel (see docs/070-det-post.md). The
        // corpus holds the discriminating cases — exact quadrant ties, the denormal scalef
        // branch, both range boundaries — so this has to be a bit comparison.
        var c = Fx.GetProperty("exp_f32");
        var x = B64.Floats(c.GetProperty("x").GetString()!);
        var expected = B64.Floats(c.GetProperty("y").GetString()!);
        Assert.Equal(c.GetProperty("n").GetInt32(), x.Length);
        Assert.Equal(x.Length, expected.Length);
        for (int i = 0; i < x.Length; i++)
        {
            float got = NpyExp.Exp(x[i]);
            uint gb = BitConverter.SingleToUInt32Bits(got), eb = BitConverter.SingleToUInt32Bits(expected[i]);
            if (gb == eb || (float.IsNaN(got) && float.IsNaN(expected[i]))) continue;
            Assert.Fail($"exp({x[i]:R} [0x{BitConverter.SingleToUInt32Bits(x[i]):X8}]) = " +
                        $"0x{gb:X8}, expected 0x{eb:X8}");
        }
    }

    [Fact]
    public void Npy_sigmoid_matches_numpy_float32()
    {
        // 1 / (1 + exp(-x)) with numpy's exp, all in float32 — the decoder's ink probability.
        // The expected values are np.float32(1.0) / (np.float32(1.0) + np.exp(-np.float32(x))).
        var cases = new (float X, uint Bits)[]
        {
            (0f, 0x3F000000), (1f, 0x3F3B26A8), (-1f, 0x3E89B2B0),
            (37.5f, 0x3F800000), (-37.5f, 0x246EAE2B),
            (20f, 0x3F800000), (-20f, 0x310DA432),
            (88.7f, 0x3F800000), (-100f, 0x00000000),
        };
        foreach (var (x, bits) in cases)
        {
            float got = NpyExp.Sigmoid(x);
            Assert.Equal(bits, BitConverter.SingleToUInt32Bits(got));
        }
    }

    [Fact]
    public void CumSum_matches_numpy()
    {
        var c = Fx.GetProperty("cumsum");
        var input = Fixtures.DoubleArray(c.GetProperty("input"));
        Assert.Equal(Fixtures.DoubleArray(c.GetProperty("expected")), Numerics.CumSum(input));
    }

    [Fact]
    public void SearchSorted_matches_numpy()
    {
        foreach (var c in Fx.GetProperty("searchsorted").EnumerateArray())
        {
            var sorted = Fixtures.DoubleArray(c.GetProperty("sorted"));
            double v = c.GetProperty("v").GetDouble();
            Assert.Equal(c.GetProperty("left").GetInt32(), Numerics.SearchSorted(sorted, v, right: false));
            Assert.Equal(c.GetProperty("right").GetInt32(), Numerics.SearchSorted(sorted, v, right: true));
        }
    }

    [Fact]
    public void Float32_reductions_match_numpy()
    {
        // numpy's contiguous float32 sum/mean is the *pairwise* tree
        // (npy_pairwise_sum_FLOAT: eight accumulators up to 128 elements, then a bisection
        // whose split is rounded down to a multiple of 8). Every accumulator is float32, so
        // the tree is order-dependent and the port reproduces it — bit for bit, not to a
        // tolerance. `estimate_skew`'s profile and the recognizer's confidence are these
        // reductions.
        foreach (var c in Fx.GetProperty("sum32").EnumerateArray())
        {
            var x = B64.Floats(c.GetProperty("x").GetString()!);
            Assert.Equal(c.GetProperty("n").GetInt32(), x.Length);
            Assert.Equal(c.GetProperty("sum_bits").GetUInt32(),
                         BitConverter.SingleToUInt32Bits(Numerics.Sum32(x)));
            Assert.Equal(c.GetProperty("mean_bits").GetUInt32(),
                         BitConverter.SingleToUInt32Bits(Numerics.Mean32(x)));
        }

        // r.sum(1): every row of a 2-D array, the reduction the sharpness profile does.
        var rows = Fx.GetProperty("sum32_rows");
        var all = B64.Floats(rows.GetProperty("x").GetString()!);
        var shape = Fixtures.IntArray(rows.GetProperty("shape"));
        int i = 0;
        foreach (var want in rows.GetProperty("sum_bits").EnumerateArray())
        {
            Assert.Equal(want.GetUInt32(), BitConverter.SingleToUInt32Bits(
                Numerics.Sum32(all.AsSpan(i * shape[1], shape[1]))));
            i++;
        }
        Assert.Equal(shape[0], i);
    }

    private static double[] ToDoubleMatrix(JsonElement e, out int rows, out int cols)
    {
        rows = e.GetArrayLength();
        cols = e[0].GetArrayLength();
        var m = new double[rows * cols];
        int i = 0;
        foreach (var row in e.EnumerateArray())
        {
            int j = 0;
            foreach (var v in row.EnumerateArray()) m[i * cols + j++] = v.GetDouble();
            i++;
        }
        return m;
    }

    private static float[] ToFloatMatrix(JsonElement e, out int rows, out int cols)
    {
        rows = e.GetArrayLength();
        cols = e[0].GetArrayLength();
        var m = new float[rows * cols];
        int i = 0;
        foreach (var row in e.EnumerateArray())
        {
            int j = 0;
            foreach (var v in row.EnumerateArray()) m[i * cols + j++] = v.GetSingle();
            i++;
        }
        return m;
    }

    [Fact]
    public void MatMul_matches_numpy()
    {
        var c = Fx.GetProperty("matmul");
        double[] a = ToDoubleMatrix(c.GetProperty("a"), out int m, out int k);
        double[] b = ToDoubleMatrix(c.GetProperty("b"), out int k2, out int n);
        Assert.Equal(k, k2);
        var got = Numerics.MatMul(a, m, k, b, n);
        int i = 0;
        foreach (var row in c.GetProperty("expected").EnumerateArray())
        {
            int j = 0;
            foreach (var v in row.EnumerateArray())
                Assert.Equal(v.GetDouble(), got[i, j++], 9);
            i++;
        }
        Assert.Equal(m, i);
    }

    [Fact]
    public void MatMulF_matches_numpy_float32()
    {
        var c = Fx.GetProperty("matmulf");
        float[] a = ToFloatMatrix(c.GetProperty("a"), out int m, out int k);
        float[] b = ToFloatMatrix(c.GetProperty("b"), out int k2, out int n);
        Assert.Equal(k, k2);
        var got = Numerics.MatMulF(a, m, k, b, n);
        int i = 0;
        foreach (var row in c.GetProperty("expected").EnumerateArray())
        {
            int j = 0;
            foreach (var v in row.EnumerateArray())
                Assert.Equal(v.GetSingle(), got[i, j++], 5);
            i++;
        }
    }
}
