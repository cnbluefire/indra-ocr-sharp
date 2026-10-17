using System.Numerics;

namespace Indra.Ocr.Imaging;

/// <summary>Interpolation flags; the values match OpenCV's <c>cv::InterpolationFlags</c>.</summary>
public enum Interpolation
{
    Nearest = 0,
    Linear = 1,
    Cubic = 2,
    Area = 3,
    Lanczos4 = 4,
}

/// <summary>Border extrapolation modes; the values match OpenCV's <c>cv::BorderTypes</c>.</summary>
public enum BorderMode
{
    Constant = 0,
    Replicate = 1,
    Reflect = 2,
    Wrap = 3,
    Reflect101 = 4,
    Transparent = 5,
}

/// <summary>
/// The numeric primitives OpenCV's resize/warp kernels are built on, reproduced exactly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Round"/> is <c>cvRound</c>: round-half-to-even, i.e. the x86 default rounding
/// mode, <b>not</b> <see cref="Math.Round(double)"/>'s default (away from zero).
/// </para>
/// <para>
/// <see cref="WarpLanes"/>/<see cref="WarpUsesFma"/> describe the SIMD dispatch OpenCV picks
/// on this machine. OpenCV's warp kernels compute the mapped coordinates and the bilinear
/// weights with <c>v_fma</c> for every lane inside the vectorized block and with plain float
/// arithmetic in the scalar tail, so the port must know where that boundary is: the block
/// holds <c>2 * vlanes_32</c> columns (<c>uf</c>) and starts at x = 0, so columns
/// <c>x &lt; WarpUnroll * floor(width / WarpUnroll)</c> go through the FMA path.
/// </para>
/// </remarks>
public static class CvMath
{
    /// <summary>OpenCV's <c>DBL_EPSILON</c> (<b>not</b> <see cref="double.Epsilon"/>, which is the smallest subnormal).</summary>
    internal const double DblEpsilon = 2.220446049250313e-16;

    /// <summary><c>INTER_RESIZE_COEF_BITS</c>: resize's fixed-point weights have this many bits.</summary>
    internal const int ResizeCoefBits = 11;

    /// <summary><c>INTER_RESIZE_COEF_SCALE</c> = 1 &lt;&lt; <see cref="ResizeCoefBits"/>.</summary>
    internal const int ResizeCoefScale = 1 << ResizeCoefBits;

    /// <summary>Number of <see cref="float"/> lanes in one SIMD register on this machine.</summary>
    public static readonly int WarpLanes = Vector<float>.Count;

    /// <summary>OpenCV's warp unrolling factor <c>uf = vlanes_32 * 2</c>.</summary>
    public static readonly int WarpUnroll = WarpLanes * 2;

    /// <summary>Whether the SIMD kernels OpenCV dispatches here use FMA (AVX2/AVX512 but not SSE).</summary>
    /// <remarks>
    /// <c>Fma.IsSupported</c> arrived with <c>System.Runtime.Intrinsics</c> in .NET Core 3.0, and
    /// netstandard2.0 has no such namespace to ask -- the .NET Framework JIT never emits those
    /// intrinsics -- so the answer there is "no" and the FFMA half of the warp kernels is simply
    /// unreachable.  Only the dispatch differs: the kernels are the same source, and the net10.0
    /// build still asks the JIT.
    /// </remarks>
#if NETCOREAPP3_0_OR_GREATER
    public static readonly bool WarpUsesFma = System.Runtime.Intrinsics.X86.Fma.IsSupported;
#else
    public static readonly bool WarpUsesFma = false;
#endif

    /// <summary><c>cvRound</c>: round to nearest, ties to even.</summary>
    public static int Round(float v) => (int)MathF.Round(v, MidpointRounding.ToEven);

    /// <summary><c>cvRound</c> for a double.</summary>
    public static int Round(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    /// <summary><c>cvFloor</c>.</summary>
    public static int Floor(float v) => (int)MathF.Floor(v);

    /// <summary><c>cvFloor</c> for a double.</summary>
    public static int Floor(double v) => (int)Math.Floor(v);

    /// <summary><c>cvCeil</c>.</summary>
    public static int Ceil(float v) => (int)MathF.Ceiling(v);

    /// <summary><c>cvCeil</c> for a double.</summary>
    public static int Ceil(double v) => (int)Math.Ceiling(v);

    /// <summary><c>saturate_cast&lt;uchar&gt;(int)</c>: cvRound then clamp.</summary>
    public static byte SaturateByte(float v) => SaturateByte(Round(v));

    /// <summary><c>saturate_cast&lt;uchar&gt;(int)</c>.</summary>
    public static byte SaturateByte(int v) => (byte)(v > 255 ? 255 : v < 0 ? 0 : v);

    /// <summary><c>saturate_cast&lt;short&gt;(float)</c>: cvRound then clamp.</summary>
    public static short SaturateShort(float v)
    {
        int iv = Round(v);
        return (short)(iv > 32767 ? 32767 : iv < -32768 ? -32768 : iv);
    }

    /// <summary>
    /// <c>v_fma</c>: a single-rounding <c>a*b + c</c>. Unlike the scalar tail, the vectorized
    /// warp/interp blocks contract, so the same expression rounds differently there.
    /// </summary>
    internal static float Fma(float a, float b, float c)
        => WarpUsesFma ? MathF.FusedMultiplyAdd(a, b, c) : a * b + c;
}
