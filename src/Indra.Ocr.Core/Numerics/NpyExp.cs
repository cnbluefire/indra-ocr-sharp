using System;

namespace Indra.Ocr;

/// <summary>
/// numpy's <c>np.exp</c> for float32, ported bit-exactly (see <c>docs/070-det-post.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>np.exp</c> on a float32 array does not call the C library: numpy's
/// <c>loops_exponent_log.dispatch</c> implements its own vectorised exp (a port of the Intel
/// SVML kernel) and — for the AVX2+FMA3 build this machine picks — uses that kernel for
/// <em>every</em> element, including single scalars. It therefore differs from the CRT's
/// <c>expf</c> (and from <c>(float)exp((double)x)</c>) in about 40% of all inputs, by 1 ulp.
/// The detector's sigmoid feeds a <c>&gt; 0.5</c> threshold and the decoded line boxes, so
/// the last ulp is observable: the port reproduces the kernel instead of using
/// <see cref="MathF.Exp"/>.
/// </para>
/// <para>
/// The kernel rounds <c>x/ln 2</c> to an integer with the "magic number" trick, reduces the
/// range with Cody-Waite, evaluates a degree-5/2 rational approximation with <b>fused</b>
/// multiply-adds, and scales by <c>2^k</c> by adding <c>k &lt;&lt; 23</c> to the raw bits
/// (with a denormal fix-up for <c>k &lt;= -125</c>). Every step is reproduced below; the
/// FMA chains need a true fused operation, hence <see cref="MathF.FusedMultiplyAdd"/>.
/// </para>
/// <para>
/// Sources: <c>numpy/_core/src/umath/loops_exponent_log.dispatch.c.src</c>
/// (<c>simd_exp_FLOAT</c>, <c>simd_range_reduction</c>, <c>fma_scalef_ps</c>) and
/// <c>numpy/_core/src/umath/npy_simd_data.h</c> (the constants).
/// </para>
/// </remarks>
public static class NpyExp
{
    private const float XMax = 88.72283935546875f;              // NPY_MAXLOGF
    private const float XMin = -103.97208404541015625f;         // NPY_MINLOGF
    private const float Log2E = 1.442695040888963407359924681001892137f;
    private const float C1 = -6.93145752e-1f;                   // high part of ln 2
    private const float C2 = -1.42860677e-6f;                   // low part of ln 2
    private const float CvtMagic = 12582912f;   // 0x1.8p23 = 1.5 * 2^23; rounds to nearest even
    private const float P0 = 9.999999999980870924916e-01f;
    private const float P1 = 7.257664613233124478488e-01f;
    private const float P2 = 2.473615434895520810817e-01f;
    private const float P3 = 5.114512081637298353406e-02f;
    private const float P4 = 6.757896990527504603057e-03f;
    private const float P5 = 5.082762527590693718096e-04f;
    private const float Q0 = 1.000000000000000000000e+00f;
    private const float Q1 = -2.742335390411667452936e-01f;
    private const float Q2 = 2.159509375685829852307e-02f;
    private const float MinQuadrant = -125.0f;

    /// <summary>
    /// numpy's <c>NPY_NANF</c> is the <em>positive</em> quiet NaN (0x7FC00000), while .NET's
    /// <see cref="float.NaN"/> is the negative one. A NaN result only ever reaches a
    /// comparison that is false either way, but the port keeps the bit pattern exact.
    /// </summary>
    private static readonly float PositiveNaN = Bits.Int32BitsToSingle(unchecked((int)0x7FC00000));

    /// <summary>numpy's <c>np.exp(np.float32(x))</c>.</summary>
    public static float Exp(float x)
    {
        // NaN / overflow / underflow are handled by masks *after* the kernels's maths ran on
        // a zeroed copy of the input, exactly like the SIMD version.
        bool nanMask = float.IsNaN(x);
        bool xmaxMask = x >= XMax;
        bool xminMask = x <= XMin;

        float v = nanMask || xmaxMask || xminMask ? 0f : x;

        // Round x/ln2 to the nearest integer, ties to even. The vector code writes this as
        // `q = x * log2e; q = (q + cvt_magic) - cvt_magic`, but the compiler contracts the
        // multiply into the add (FMA), so the *unrounded* product is what the magic number
        // rounds: numpy rounds x*log2e + 2^23 once. That only differs from the two-step form
        // when the float32 product is an exact half-integer, and there it decides which
        // quadrant (and hence which mantissa) the result gets — verified against np.exp on
        // the exact-tie corpus (see docs/070-det-post.md).
        float quadrant = MathF.FusedMultiplyAdd(v, Log2E, CvtMagic) - CvtMagic;

        // Cody-Waite range reduction (the third term of the vector code is x*0 == 0).
        v = MathF.FusedMultiplyAdd(quadrant, C1, v);
        v = MathF.FusedMultiplyAdd(quadrant, C2, v);
        v = MathF.FusedMultiplyAdd(quadrant, 0f, v);

        float num = MathF.FusedMultiplyAdd(P5, v, P4);
        num = MathF.FusedMultiplyAdd(num, v, P3);
        num = MathF.FusedMultiplyAdd(num, v, P2);
        num = MathF.FusedMultiplyAdd(num, v, P1);
        num = MathF.FusedMultiplyAdd(num, v, P0);
        float den = MathF.FusedMultiplyAdd(Q2, v, Q1);
        den = MathF.FusedMultiplyAdd(den, v, Q0);
        float poly = num / den;

        poly = ScaleByPowerOfTwo(poly, quadrant);

        if (nanMask) return PositiveNaN;
        if (xmaxMask) return float.PositiveInfinity;
        if (xminMask) return 0f;
        return poly;
    }

    /// <summary>Element-wise <see cref="Exp(float)"/>.</summary>
    public static void Exp(ReadOnlySpan<float> x, Span<float> y)
    {
        if (y.Length < x.Length) throw new ArgumentException("destination is too small", nameof(y));
        for (int i = 0; i < x.Length; i++) y[i] = Exp(x[i]);
    }

    /// <summary>Element-wise <see cref="Exp(float)"/> into a new array.</summary>
    public static float[] Exp(ReadOnlySpan<float> x)
    {
        var y = new float[x.Length];
        Exp(x, y);
        return y;
    }

    /// <summary>1 / (1 + exp(-x)) computed the way the Python pipeline does it.</summary>
    public static float Sigmoid(float x) => 1f / (1f + Exp(-x));

    /// <summary>
    /// <c>fma_scalef_ps</c>: multiply by <c>2^quadrant</c> by adding the exponent to the raw
    /// bits; a quadrant of -125 or below would push the result into the denormals, where the
    /// bit add no longer works, so those lanes are rescaled by an exact power of two instead.
    /// </summary>
    private static float ScaleByPowerOfTwo(float poly, float quadrant)
    {
        if (quadrant > MinQuadrant)
        {
            unchecked { return Bits.Int32BitsToSingle(Bits.SingleToInt32Bits(poly) + (RoundToInt(quadrant) << 23)); }
        }

        float quadDiff = 0f - (quadrant - MinQuadrant);     // >= 0 on this path
        int shift = RoundToInt(quadDiff);
        int twoPowerDiff = shift < 32 ? unchecked(1 << shift) : 0;   // AVX2 shifts by >= 32 give 0
        float q = MathF.Max(quadrant, MinQuadrant);
        float scaled = unchecked(Bits.Int32BitsToSingle(
            Bits.SingleToInt32Bits(poly) + (RoundToInt(q) << 23)));
        return scaled / twoPowerDiff;
    }

    /// <summary>
    /// <c>_mm256_cvtps_epi32</c> for the integer-valued floats this kernel rounds (the magic
    /// trick leaves an exact integer, so a C# cast is enough; ties-to-even already happened).
    /// </summary>
    private static int RoundToInt(float v) => (int)v;
}
