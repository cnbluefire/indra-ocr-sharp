namespace Indra.Ocr;

/// <summary>
/// Numeric helpers that mirror the numpy operations used by the Python pipeline. Every array
/// argument is a span so buffers can be passed without copying; results are returned as
/// freshly allocated arrays.
/// </summary>
/// <remarks>
/// Reductions that numpy performs in float64 (median, percentile, bincount-weighted, sums of
/// pairwise logits) return <see cref="double"/>; element-wise array math that numpy keeps in
/// float32 is done in <see cref="float"/>.
/// </remarks>
public static class Numerics
{
    // ---------------------------------------------------------------- reductions

    /// <summary>numpy.median of a float32 array: the reduction stays in float32 (like numpy),
    /// then is widened to double for the caller.</summary>
    public static double Median(ReadOnlySpan<float> values)
    {
        if (values.Length == 0) return double.NaN;
        var copy = values.ToArray();
        Array.Sort(copy);
        int n = copy.Length;
        int mid = n / 2;
        float r = (n & 1) == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2f;
        return r;
    }

    /// <summary>numpy.median of a float64 array.</summary>
    public static double Median(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return double.NaN;
        var copy = values.ToArray();
        Array.Sort(copy);
        int n = copy.Length;
        int mid = n / 2;
        return (n & 1) == 1 ? copy[mid] : 0.5 * (copy[mid - 1] + copy[mid]);
    }

    /// <summary>numpy.percentile(v, q) with the default "linear" interpolation.</summary>
    public static double Percentile(ReadOnlySpan<double> values, double q)
    {
        if (values.Length == 0) return double.NaN;
        var copy = values.ToArray();
        Array.Sort(copy);
        if (copy.Length == 1) return copy[0];
        double pos = q / 100.0 * (copy.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, copy.Length - 1);
        double frac = pos - lo;
        return copy[lo] + frac * (copy[hi] - copy[lo]);
    }

    public static double Mean(ReadOnlySpan<double> a)
    {
        if (a.Length == 0) return double.NaN;
        double s = 0;
        foreach (var v in a) s += v;
        return s / a.Length;
    }

    public static double Mean(ReadOnlySpan<float> a)
    {
        if (a.Length == 0) return double.NaN;
        double s = 0;
        foreach (var v in a) s += v;
        return s / a.Length;
    }

    // ---------------------------------------------------------------- ordering

    public static int ArgMax(ReadOnlySpan<float> a)
    {
        if (a.Length == 0) throw new ArgumentException("empty");
        int k = 0;
        for (int i = 1; i < a.Length; i++)
            if (a[i] > a[k]) k = i;
        return k;
    }

    public static int ArgMax(ReadOnlySpan<double> a)
    {
        if (a.Length == 0) throw new ArgumentException("empty");
        int k = 0;
        for (int i = 1; i < a.Length; i++)
            if (a[i] > a[k]) k = i;
        return k;
    }

    public static int ArgMin(ReadOnlySpan<float> a)
    {
        if (a.Length == 0) throw new ArgumentException("empty");
        int k = 0;
        for (int i = 1; i < a.Length; i++)
            if (a[i] < a[k]) k = i;
        return k;
    }

    public static int ArgMin(ReadOnlySpan<double> a)
    {
        if (a.Length == 0) throw new ArgumentException("empty");
        int k = 0;
        for (int i = 1; i < a.Length; i++)
            if (a[i] < a[k]) k = i;
        return k;
    }

    /// <summary>Indices that sort <paramref name="a"/> ascending. Stable (ties keep index order).</summary>
    public static int[] ArgSort(ReadOnlySpan<double> a)
    {
        var keys = a.ToArray();
        var idx = new int[keys.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (x, y) => keys[x] != keys[y] ? keys[x].CompareTo(keys[y]) : x.CompareTo(y));
        return idx;
    }

    public static int[] ArgSort(ReadOnlySpan<float> a)
    {
        var keys = a.ToArray();
        var idx = new int[keys.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (x, y) => keys[x] != keys[y] ? keys[x].CompareTo(keys[y]) : x.CompareTo(y));
        return idx;
    }

    /// <summary>Indices that sort <paramref name="a"/> descending (stable).</summary>
    public static int[] ArgSortDescending(ReadOnlySpan<float> a)
    {
        var keys = a.ToArray();
        var idx = new int[keys.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (x, y) => keys[x] != keys[y] ? keys[y].CompareTo(keys[x]) : x.CompareTo(y));
        return idx;
    }

    /// <summary>Order the values of <paramref name="a"/> by an explicit key pair (stable), like Python sorted().</summary>
    public static int[] ArgSortByKey(int n, Func<int, (double, double)> key)
    {
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (x, y) => key(x).CompareTo(key(y)));
        return idx;
    }

    // ---------------------------------------------------------------- bincount

    /// <summary>numpy.bincount(x, minlength).</summary>
    public static long[] Bincount(ReadOnlySpan<int> x, int minLength)
    {
        int n = Math.Max(minLength, x.Length == 0 ? 0 : MaxPlusOne(x));
        var outv = new long[n];
        foreach (var v in x)
        {
            if (v < 0) throw new ArgumentException("bincount: negative value");
            outv[v]++;
        }
        return outv;
    }

    /// <summary>numpy.bincount(x, weights=w, minlength).</summary>
    public static double[] Bincount(ReadOnlySpan<int> x, ReadOnlySpan<float> w, int minLength)
    {
        if (w.Length != x.Length) throw new ArgumentException("bincount: weights length", nameof(w));
        int n = Math.Max(minLength, x.Length == 0 ? 0 : MaxPlusOne(x));
        var outv = new double[n];
        for (int i = 0; i < x.Length; i++) outv[x[i]] += w[i];
        return outv;
    }

    private static int MaxPlusOne(ReadOnlySpan<int> x)
    {
        int m = 0;
        foreach (var v in x) if (v > m) m = v;
        return m + 1;
    }

    // ---------------------------------------------------------------- elementwise

    /// <summary>log(1 + x), accurate for small x (like C's log1p / numpy.log1p).</summary>
    public static double Log1p(double x)
    {
        if (double.IsNaN(x)) return x;
        if (x == -1.0) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x)) return x;
        double u = 1.0 + x;
        if (u == 1.0) return x;
        if (u - 1.0 == x) return Math.Log(u) - ((u - 1.0) - x) / u;
        return Math.Log(u);
    }

    /// <summary>numpy.logaddexp(x, y) == log(exp(x) + exp(y)), matching numpy's edge handling.</summary>
    public static double LogAddExp(double x, double y)
    {
        if (double.IsNegativeInfinity(x)) return y;
        if (double.IsNegativeInfinity(y)) return x;
        if (x == y) return x + Math.Log(2.0);
        if (x > y) (x, y) = (y, x);
        return Log1p(Math.Exp(x - y)) + y;
    }

    public static double[] CumSum(ReadOnlySpan<double> a)
    {
        var o = new double[a.Length];
        double s = 0;
        for (int i = 0; i < a.Length; i++) o[i] = s += a[i];
        return o;
    }

    public static float[] CumSum(ReadOnlySpan<float> a)
    {
        var o = new float[a.Length];
        float s = 0;
        for (int i = 0; i < a.Length; i++) o[i] = s += a[i];
        return o;
    }

    /// <summary>numpy.searchsorted on a sorted array; returns the insertion index.</summary>
    public static int SearchSorted(ReadOnlySpan<double> sorted, double v, bool right)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (right ? sorted[mid] <= v : sorted[mid] < v) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ---------------------------------------------------------------- matrices

    /// <summary>
    /// Matrix product <c>a[m, k] * b[k, n] -&gt; [m, n]</c> (float64). The operands are flat
    /// row-major spans; the result is a row-major <c>[m, n]</c> matrix.
    /// </summary>
    public static double[,] MatMul(ReadOnlySpan<double> a, int aRows, int aCols,
                                   ReadOnlySpan<double> b, int bCols)
    {
        if (a.Length != aRows * aCols) throw new ArgumentException("a shape", nameof(a));
        if (b.Length != aCols * bCols) throw new ArgumentException("b shape", nameof(b));
        var o = new double[aRows, bCols];
        for (int i = 0; i < aRows; i++)
            for (int t = 0; t < aCols; t++)
            {
                double av = a[i * aCols + t];
                if (av == 0) continue;
                for (int j = 0; j < bCols; j++) o[i, j] += av * b[t * bCols + j];
            }
        return o;
    }

    /// <summary>Matrix product <c>a[m, k] * b[k, n] -&gt; [m, n]</c> (float32).</summary>
    public static float[,] MatMulF(ReadOnlySpan<float> a, int aRows, int aCols,
                                   ReadOnlySpan<float> b, int bCols)
    {
        if (a.Length != aRows * aCols) throw new ArgumentException("a shape", nameof(a));
        if (b.Length != aCols * bCols) throw new ArgumentException("b shape", nameof(b));
        var o = new float[aRows, bCols];
        for (int i = 0; i < aRows; i++)
            for (int t = 0; t < aCols; t++)
            {
                float av = a[i * aCols + t];
                if (av == 0) continue;
                for (int j = 0; j < bCols; j++) o[i, j] += av * b[t * bCols + j];
            }
        return o;
    }
}
