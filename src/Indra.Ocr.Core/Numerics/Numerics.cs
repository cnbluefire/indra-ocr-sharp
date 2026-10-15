using System.Globalization;
using System.Numerics;

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

    /// <summary>
    /// numpy's <b>float32</b> sum, i.e. <c>np.sum(a)</c> / <c>a.sum(axis)</c> on a float32 array:
    /// the accumulator is float32 and the order is numpy's <c>pairwise_sum_FLOAT</c>.
    /// </summary>
    /// <remarks>
    /// numpy dispatches contiguous float32 reductions to a CPU-specific SIMD kernel
    /// (8/16-lane accumulators) whose tree is not part of numpy's contract — the fallback, and
    /// the code that runs when SIMD is unavailable, is the pairwise sum reproduced here. The
    /// SIMD tree differs from it only in the last ulp; see <c>docs/090-orientation-deskew.md</c>
    /// for where that can be observed.
    /// </remarks>
    public static float Sum32(ReadOnlySpan<float> a) => PairwiseSum32(a);

    /// <summary>numpy's <c>float32_array.mean()</c>: the float32 pairwise sum divided by the count in float32.</summary>
    public static float Mean32(ReadOnlySpan<float> a)
        => a.Length == 0 ? float.NaN : Sum32(a) / a.Length;

    // numpy's npy_pairwise_sum_FLOAT: a straight sum below 8 elements, 8 accumulators up to
    // PW_BLOCKSIZE=128 elements, and a recursive bisection above it. Every accumulator is
    // float32, so the result is order-dependent and has to be reproduced exactly.
    private const int PairwiseBlockSize = 128;

    private static float PairwiseSum32(ReadOnlySpan<float> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            float s = 0f;
            for (int i = 0; i < n; i++) s += a[i];
            return s;
        }
        if (n <= PairwiseBlockSize)
        {
            float r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3];
            float r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            int i = 8, limit = n - (n % 8);
            for (; i < limit; i += 8)
            {
                r0 += a[i]; r1 += a[i + 1]; r2 += a[i + 2]; r3 += a[i + 3];
                r4 += a[i + 4]; r5 += a[i + 5]; r6 += a[i + 6]; r7 += a[i + 7];
            }
            float res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[i];
            return res;
        }
        int n2 = n / 2;
        return PairwiseSum32(a[..n2]) + PairwiseSum32(a[n2..]);
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
        // The addition may have dropped bits of x (x - (u - 1) is exact); when it did, add the
        // correction log1p(x) = log(u) + (x - (u - 1)) / u. Applying it only in the branch where
        // bits were lost is what makes this match the C library's log1p for tiny x.
        if (u - 1.0 == x) return Math.Log(u);
        return Math.Log(u) - ((u - 1.0) - x) / u;
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

    // ---------------------------------------------------------------- python round()

    /// <summary>
    /// Python's <c>round(value, digits)</c>: the nearest multiple of <c>10**-digits</c> as a
    /// <b>decimal</b>, ties to even, converted to the nearest double.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not what <see cref="Math.Round(double, int)"/> computes. .NET scales by
    /// <c>10**digits</c> first, i.e. it rounds a rounded value: <c>Math.Round(0.15, 1)</c> is
    /// <c>0.2</c> because <c>0.15 * 10</c> lands exactly on <c>1.5</c>, while Python's
    /// <c>round(0.15, 1)</c> is <c>0.1</c> because the double <c>0.15</c> is really
    /// <c>0.1499999999999999944...</c>. The pipeline writes box coordinates and confidences with
    /// <c>round(...)</c>, so the port reproduces Python's decimal rounding exactly: it
    /// decomposes the double into <c>mant * 2**exp</c>, forms the exact rational
    /// <c>|value| * 10**digits</c>, rounds that to an integer with ties to even, and divides back.
    /// </para>
    /// <para>
    /// The final division is done in double, which is correctly rounded as long as
    /// <c>10**|digits|</c> is exactly representable (<c>|digits| &lt;= 22</c>, the last exact
    /// power) and the rounded integer is below <c>2**53</c> — both true for page coordinates.
    /// Validated against Python over 425 110 values (random doubles, float32-derived doubles and
    /// boundaries) at 0…4 digits: 0 differences; see <c>docs/120-layout-post.md</c>.
    /// </para>
    /// </remarks>
    public static double RoundHalfEven(double value, int digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return value;                     // Python: round(nan, n) is nan, round(inf, n) is inf
        if (value == 0.0)
            return value;                     // 0.0 and -0.0 are their own nearest multiples

        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = bits < 0;
        int rawExponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        int exp2;
        if (rawExponent == 0)
        {
            exp2 = -1074;                     // subnormal
        }
        else
        {
            mantissa |= 1L << 52;             // implicit leading bit
            exp2 = rawExponent - 1075;
        }

        BigInteger numerator = mantissa;
        BigInteger denominator = BigInteger.One;
        int shift = exp2 + digits;
        if (shift >= 0)
            numerator <<= shift;
        else
            denominator <<= -shift;
        if (digits >= 0)
            numerator *= BigInteger.Pow(5, digits);
        else
            denominator *= BigInteger.Pow(5, -digits);

        BigInteger q = BigInteger.DivRem(numerator, denominator, out BigInteger r);
        BigInteger twice = r * 2;
        if (twice > denominator || (twice == denominator && !q.IsEven))
            q += 1;

        double scaled = digits >= 0
            ? ScaledToDouble(q, digits)
            : (double)q * PowerOfTen(-digits);
        return negative ? -scaled : scaled;
    }

    /// <summary>
    /// The nearest double to the exact rational <c>q / 10**digits</c> (q &gt;= 0).
    /// </summary>
    /// <remarks>
    /// While the integer and the power are exact (<c>|q| &lt; 2**53</c>, <c>digits &lt;= 22</c>) the
    /// IEEE division is already correctly rounded. Beyond that the exact rational is written as a
    /// decimal string and parsed, which .NET also rounds correctly (nearest, ties to even) — the
    /// page coordinates this is used on never leave the fast path.
    /// </remarks>
    private static double ScaledToDouble(BigInteger q, int digits)
    {
        if (q.IsZero)
            return 0.0;
        if (digits <= 22 && BigInteger.Abs(q) < (BigInteger.One << 53))
            return (double)q / PowerOfTen(digits);
        if (digits == 0)
            return (double)q;

        string integer = q.ToString(CultureInfo.InvariantCulture);
        if (integer.Length <= digits)
            integer = integer.PadLeft(digits + 1, '0');
        string text = integer[..^digits] + "." + integer[^digits..];
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // 10**n as a double for n >= 0: exact up to n = 22, which is what RoundHalfEven needs.
    private static double PowerOfTen(int n)
    {
        double p = 1.0;
        for (int i = 0; i < n; i++)
            p *= 10.0;
        return p;
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
