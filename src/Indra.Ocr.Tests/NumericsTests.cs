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
