using System;

namespace Indra.Ocr;

/// <summary>
/// numpy's <c>np.log</c> for float32, ported bit-exactly (see <c>docs/110-order-post.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="NpyExp"/>, <c>np.log</c> on a contiguous float32 array never reaches the C
/// library: numpy's <c>loops_exponent_log.dispatch</c> runs its own vectorised log (a port of
/// the Intel SVML kernel) for <em>every</em> element — including one-element arrays, because the
/// contiguous path hands the whole buffer (tail included, via masked loads) to the kernel. On
/// this AVX2+FMA3 machine that differs from the CRT's <c>logf</c> in about 4% of inputs by 1 ulp.
/// The reading-order features feed the order model, so the last ulp is observable here too.
/// </para>
/// <para>
/// Sources: <c>numpy/_core/src/umath/loops_exponent_log.dispatch.c.src</c>
/// (<c>simd_log_FLOAT</c>, <c>fma_get_exponent</c>, <c>fma_get_mantissa</c>) and
/// <c>numpy/_core/src/umath/npy_simd_data.h</c> (the coefficients).
/// </para>
/// <para>
/// The scalar loop of <c>np.logaddexp</c>, by contrast, <em>does</em> call the platform's
/// <c>expf</c>/<c>log1pf</c> (see <see cref="Order.OrderPost"/>), and <c>np.exp</c> does not
/// call <c>expf</c> at all — the three float32 transcendental paths really are different code.
/// </para>
/// </remarks>
public static class NpyLog
{
    private const float FltMin = 1.17549435082228751e-38f;   // FLT_MIN
    private const float LogE2 = 0.693147180559945309417f;    // NPY_LOGE2f
    private const float Sqrt1_2 = 0.70710678118654752440f;   // NPY_SQRT1_2f
    private const float P0 = 0.000000000000000000000e+00f;   // NPY_COEFF_P0_LOGf
    private const float P1 = 9.999999999999998702752e-01f;
    private const float P2 = 2.112677543073053063722e+00f;
    private const float P3 = 1.480000633576506585156e+00f;
    private const float P4 = 3.808837741388407920751e-01f;
    private const float P5 = 2.589979117907922693523e-02f;
    private const float Q0 = 1.000000000000000000000e+00f;   // NPY_COEFF_Q0_LOGf
    private const float Q1 = 2.612677543073109236779e+00f;
    private const float Q2 = 2.453006071784736363091e+00f;
    private const float Q3 = 9.864942958519418960339e-01f;
    private const float Q4 = 1.546476374983906719538e-01f;
    private const float Q5 = 5.875095403124574342950e-03f;

    /// <summary>2^100, the scale the denormal paths multiply by (raw bits <c>0x71800000</c>).</summary>
    private static readonly float TwoPower100 = BitConverter.Int32BitsToSingle(unchecked((int)0x71800000));

    /// <summary>
    /// numpy's <c>NPY_NANF</c> is the <em>positive</em> quiet NaN (0x7FC00000) and the kernel
    /// writes <c>-NPY_NANF</c> (0xFFC00000) for negative inputs; .NET's <see cref="float.NaN"/>
    /// is neither of those bit patterns.
    /// </summary>
    private static readonly float PositiveNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00000));
    private static readonly float NegativeNaN = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00000));

    /// <summary>numpy's <c>np.log(np.float32(x))</c>.</summary>
    public static float Log(float x)
    {
        bool nanMask = float.IsNaN(x);
        bool zeroMask = x == 0f;
        bool infMask = x == float.PositiveInfinity;
        bool negMask = x < 0f;

        // Negative lanes are replaced by 0 before the kernels run; NaN lanes are not.
        float v = negMask ? 0f : x;

        float exponent = GetExponent(v);
        v = GetMantissa(v);

        // f <= sqrt(1/2): double the mantissa, one lower exponent (keeps x-1 in [-0.29, 0.42]).
        if (v <= Sqrt1_2)
        {
            v = v + v;
            exponent -= 1f;
        }
        v -= 1f;

        float num = MathF.FusedMultiplyAdd(P5, v, P4);
        num = MathF.FusedMultiplyAdd(num, v, P3);
        num = MathF.FusedMultiplyAdd(num, v, P2);
        num = MathF.FusedMultiplyAdd(num, v, P1);
        num = MathF.FusedMultiplyAdd(num, v, P0);
        float den = MathF.FusedMultiplyAdd(Q5, v, Q4);
        den = MathF.FusedMultiplyAdd(den, v, Q3);
        den = MathF.FusedMultiplyAdd(den, v, Q2);
        den = MathF.FusedMultiplyAdd(den, v, Q1);
        den = MathF.FusedMultiplyAdd(den, v, Q0);

        float poly = MathF.FusedMultiplyAdd(exponent, LogE2, num / den);

        if (nanMask) return PositiveNaN;
        if (negMask) return NegativeNaN;
        if (zeroMask) return float.NegativeInfinity;
        if (infMask) return float.PositiveInfinity;
        return poly;
    }

    /// <summary>
    /// <c>fma_get_exponent</c>: the unbiased binary exponent, one higher than <c>ilogb</c>,
    /// denormals handled by pre-scaling with 2^100 (a denormal's raw exponent field is 0, so it
    /// would need <c>-126</c> more).
    /// </summary>
    private static float GetExponent(float x)
    {
        bool denormal = x < FltMin;
        float temp = (denormal ? x : 0f) * TwoPower100;
        int biased = (int)((uint)BitConverter.SingleToInt32Bits(denormal ? temp : x) >> 23);
        float exponent = biased - 0x7E;
        return denormal ? exponent - 100f : exponent;
    }

    /// <summary><c>fma_get_mantissa</c>: the significand as a value in <c>[0.5, 1)</c>.</summary>
    private static float GetMantissa(float x)
    {
        bool denormal = x < FltMin;
        float temp = (denormal ? x : 0f) * TwoPower100;
        int bits = BitConverter.SingleToInt32Bits(denormal ? temp : x);
        return BitConverter.Int32BitsToSingle((bits & 0x7FFFFF) | (126 << 23));
    }

    /// <summary>Element-wise <see cref="Log(float)"/>.</summary>
    public static void Log(ReadOnlySpan<float> x, Span<float> y)
    {
        if (y.Length < x.Length) throw new ArgumentException("destination is too small", nameof(y));
        for (int i = 0; i < x.Length; i++) y[i] = Log(x[i]);
    }

    /// <summary>Element-wise <see cref="Log(float)"/> into a new array.</summary>
    public static float[] Log(ReadOnlySpan<float> x)
    {
        var y = new float[x.Length];
        Log(x, y);
        return y;
    }
}
