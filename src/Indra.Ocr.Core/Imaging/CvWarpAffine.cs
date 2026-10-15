using System;

namespace Indra.Ocr.Imaging;

/// <summary>
/// Pure-managed port of the <c>cv::warpAffine</c> paths the pipeline uses
/// (<c>INTER_LINEAR</c> on 8-bit pages and on float32 ink maps), bit-exact against OpenCV 5.x
/// (see <c>docs/040-opencv-primitives.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>warpAffine</c> takes the <i>forward</i> matrix and inverts it (in float64, exactly like
/// OpenCV) before mapping output pixels back to the source.
/// </para>
/// <para>
/// The kernel OpenCV dispatches to computes coordinates and bilinear weights with FMA inside
/// the vectorized block of <see cref="CvMath.WarpUnroll"/> columns and with plain float
/// arithmetic in the scalar tail — the two round differently, so the same boundary is
/// reproduced here.
/// </para>
/// </remarks>
public static class CvWarpAffine
{
    /// <summary>
    /// <c>cv::getRotationMatrix2D</c>: the 2x3 <b>forward</b> matrix that rotates
    /// (<paramref name="centerX"/>, <paramref name="centerY"/>) by
    /// <paramref name="angleDegrees"/> counter-clockwise and scales by
    /// <paramref name="scale"/>.
    /// </summary>
    /// <remarks>
    /// The centre is a <c>Point2f</c> in OpenCV, so it is rounded to float here too (the
    /// pipeline passes half-integers, which are exact).
    /// </remarks>
    public static void RotationMatrix2D(float centerX, float centerY, double angleDegrees, double scale,
                                        Span<double> m)
    {
        if (m.Length < 6) throw new ArgumentException("need 6 doubles", nameof(m));
        double angle = angleDegrees * (Math.PI / 180.0);
        double alpha = Math.Cos(angle) * scale;
        double beta = Math.Sin(angle) * scale;
        m[0] = alpha;
        m[1] = beta;
        m[2] = (1 - alpha) * centerX - beta * centerY;
        m[3] = -beta;
        m[4] = alpha;
        m[5] = beta * centerX + (1 - alpha) * centerY;
    }

    /// <summary>
    /// The affine inverse exactly as <c>cv::warpAffine</c> computes it (float64), which is
    /// what the kernels receive before being narrowed to float.
    /// </summary>
    public static void InvertAffine(ReadOnlySpan<double> m, Span<double> inverse)
    {
        if (m.Length < 6 || inverse.Length < 6) throw new ArgumentException("need 6 doubles");
        double d = m[0] * m[4] - m[1] * m[3];
        d = d != 0 ? 1.0 / d : 0.0;
        double m0 = m[4] * d, m1 = m[1] * -d, m3 = m[3] * -d, m4 = m[0] * d;
        inverse[0] = m0;
        inverse[1] = m1;
        inverse[2] = -m0 * m[2] - m1 * m[5];
        inverse[3] = m3;
        inverse[4] = m4;
        inverse[5] = -m3 * m[2] - m4 * m[5];
    }

    /// <summary>Rotates an 8-bit page about its centre, replicating the border
    /// (<c>pipeline.rotate_page</c>).</summary>
    public static void Rotate8u(GrayImage src, Span<byte> dst, int dstStride, int dstWidth, int dstHeight,
                                double degrees)
    {
        Span<double> m = stackalloc double[6];
        RotationMatrix2D((float)(dstWidth / 2.0), (float)(dstHeight / 2.0), degrees, 1.0, m);
        WarpAffine8u(src, dst, dstStride, dstWidth, dstHeight, m, BorderMode.Replicate);
    }

    // ------------------------------------------------------------------ 8-bit

    /// <summary>Warps an 8-bit image into a tightly packed buffer.</summary>
    public static void WarpAffine8u(GrayImage src, Span<byte> dst, int dstWidth, int dstHeight,
                                    ReadOnlySpan<double> m, BorderMode border, double borderValue = 0)
        => WarpAffine8u(src, dst, dstWidth, dstWidth, dstHeight, m, border, borderValue);

    /// <summary>Warps an 8-bit image with the forward matrix <paramref name="m"/>.</summary>
    public static void WarpAffine8u(GrayImage src, Span<byte> dst, int dstStride, int dstWidth, int dstHeight,
                                    ReadOnlySpan<double> m, BorderMode border, double borderValue = 0)
    {
        CheckDst(dst, dstStride, dstWidth, dstHeight);
        if (border == BorderMode.Transparent)
            throw new NotSupportedException("BORDER_TRANSPARENT needs a pre-initialised destination");
        Span<double> inv = stackalloc double[6];
        InvertAffine(m, inv);
        // The kernel narrows the matrix to float; every coordinate is then computed in float.
        Span<float> f = stackalloc float[6];
        for (int i = 0; i < 6; i++) f[i] = (float)inv[i];

        int sw = src.Width, sh = src.Height;
        byte bval = CvMath.SaturateByte((float)borderValue);
        int borderX = Border(border, sw), borderY = Border(border, sh);
        int simdCols = (dstWidth / CvMath.WarpUnroll) * CvMath.WarpUnroll;
        float m0 = f[0], m1 = f[1], m2 = f[2], m3 = f[3], m4 = f[4], m5 = f[5];

        for (int y = 0; y < dstHeight; y++)
        {
            float mx = y * m1 + m2;
            float my = y * m4 + m5;
            var dstRow = dst.Slice(y * dstStride, dstWidth);
            for (int x = 0; x < dstWidth; x++)
            {
                float sx, sy;
                if (x < simdCols)
                {
                    sx = CvMath.Fma(m0, x, mx);
                    sy = CvMath.Fma(m3, x, my);
                }
                else
                {
                    sx = x * m0 + y * m1 + m2;
                    sy = x * m3 + y * m4 + m5;
                }

                int ix = CvMath.Floor(sx), iy = CvMath.Floor(sy);
                float fx = sx - ix, fy = sy - iy;

                int p00, p01, p10, p11;
                if ((uint)ix < (uint)(sw - 1) && (uint)iy < (uint)(sh - 1))
                {
                    var r0 = src.Row(iy);
                    var r1 = src.Row(iy + 1);
                    p00 = r0[ix];
                    p01 = r0[ix + 1];
                    p10 = r1[ix];
                    p11 = r1[ix + 1];
                }
                else
                {
                    if (border == BorderMode.Constant
                        && ((uint)(ix + 1) >= (uint)(sw + 1) || (uint)(iy + 1) >= (uint)(sh + 1)))
                    {
                        dstRow[x] = bval;
                        continue;
                    }
                    p00 = Fetch8u(src, ix, iy, sw, sh, borderX, borderY, border, bval);
                    p01 = Fetch8u(src, ix + 1, iy, sw, sh, borderX, borderY, border, bval);
                    p10 = Fetch8u(src, ix, iy + 1, sw, sh, borderX, borderY, border, bval);
                    p11 = Fetch8u(src, ix + 1, iy + 1, sw, sh, borderX, borderY, border, bval);
                }

                float v0, v1, res;
                if (x < simdCols)
                {
                    v0 = CvMath.Fma(fx, p01 - p00, p00);
                    v1 = CvMath.Fma(fx, p11 - p10, p10);
                    res = CvMath.Fma(fy, v1 - v0, v0);
                }
                else
                {
                    v0 = p00 + fx * (p01 - p00);
                    v1 = p10 + fx * (p11 - p10);
                    res = v0 + fy * (v1 - v0);
                }
                dstRow[x] = CvMath.SaturateByte(res);
            }
        }
    }

    // ------------------------------------------------------------------ 32-bit float

    /// <summary>Warps a float32 image with the forward matrix <paramref name="m"/>.</summary>
    /// <remarks>
    /// The float32 kernel is the same code path with float pixels and no rounding on store.
    /// </remarks>
    public static void WarpAffine32f(ReadOnlySpan<float> src, int srcWidth, int srcHeight,
                                     Span<float> dst, int dstStride, int dstWidth, int dstHeight,
                                     ReadOnlySpan<double> m, BorderMode border, double borderValue = 0)
    {
        if (dstStride < dstWidth) throw new ArgumentOutOfRangeException(nameof(dstStride));
        if (dst.Length < dstStride * (dstHeight - 1) + dstWidth)
            throw new ArgumentException("destination buffer is too small", nameof(dst));
        if (border == BorderMode.Transparent)
            throw new NotSupportedException("BORDER_TRANSPARENT needs a pre-initialised destination");

        Span<double> inv = stackalloc double[6];
        InvertAffine(m, inv);
        Span<float> f = stackalloc float[6];
        for (int i = 0; i < 6; i++) f[i] = (float)inv[i];

        int sw = srcWidth, sh = srcHeight;
        float bval = (float)borderValue;
        int borderX = Border(border, sw), borderY = Border(border, sh);
        int simdCols = (dstWidth / CvMath.WarpUnroll) * CvMath.WarpUnroll;
        float m0 = f[0], m1 = f[1], m2 = f[2], m3 = f[3], m4 = f[4], m5 = f[5];

        for (int y = 0; y < dstHeight; y++)
        {
            float mx = y * m1 + m2;
            float my = y * m4 + m5;
            int dstBase = y * dstStride;
            for (int x = 0; x < dstWidth; x++)
            {
                float sx, sy;
                if (x < simdCols)
                {
                    sx = CvMath.Fma(m0, x, mx);
                    sy = CvMath.Fma(m3, x, my);
                }
                else
                {
                    sx = x * m0 + y * m1 + m2;
                    sy = x * m3 + y * m4 + m5;
                }

                int ix = CvMath.Floor(sx), iy = CvMath.Floor(sy);
                float fx = sx - ix, fy = sy - iy;

                float p00, p01, p10, p11;
                if ((uint)ix < (uint)(sw - 1) && (uint)iy < (uint)(sh - 1))
                {
                    int o = iy * sw + ix;
                    p00 = src[o];
                    p01 = src[o + 1];
                    p10 = src[o + sw];
                    p11 = src[o + sw + 1];
                }
                else
                {
                    if (border == BorderMode.Constant
                        && ((uint)(ix + 1) >= (uint)(sw + 1) || (uint)(iy + 1) >= (uint)(sh + 1)))
                    {
                        dst[dstBase + x] = bval;
                        continue;
                    }
                    p00 = Fetch32f(src, sw, sh, ix, iy, borderX, borderY, border, bval);
                    p01 = Fetch32f(src, sw, sh, ix + 1, iy, borderX, borderY, border, bval);
                    p10 = Fetch32f(src, sw, sh, ix, iy + 1, borderX, borderY, border, bval);
                    p11 = Fetch32f(src, sw, sh, ix + 1, iy + 1, borderX, borderY, border, bval);
                }

                float v0, v1, res;
                if (x < simdCols)
                {
                    v0 = CvMath.Fma(fx, p01 - p00, p00);
                    v1 = CvMath.Fma(fx, p11 - p10, p10);
                    res = CvMath.Fma(fy, v1 - v0, v0);
                }
                else
                {
                    v0 = p00 + fx * (p01 - p00);
                    v1 = p10 + fx * (p11 - p10);
                    res = v0 + fy * (v1 - v0);
                }
                dst[dstBase + x] = res;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>OpenCV downgrades the border to replicate for a degenerate (1-pixel) axis.</summary>
    private static int Border(BorderMode border, int len)
        => border != BorderMode.Constant && border != BorderMode.Transparent && len <= 1
            ? (int)BorderMode.Replicate
            : (int)border;

    private static byte Fetch8u(GrayImage src, int x, int y, int sw, int sh,
                                int borderX, int borderY, BorderMode border, byte bval)
    {
        if ((uint)x < (uint)sw && (uint)y < (uint)sh) return src[y, x];
        if (border == BorderMode.Constant) return bval;
        int cx = CvBorder.Interpolate(x, sw, borderX);
        int cy = CvBorder.Interpolate(y, sh, borderY);
        return src[cy, cx];
    }

    private static float Fetch32f(ReadOnlySpan<float> src, int sw, int sh, int x, int y,
                                  int borderX, int borderY, BorderMode border, float bval)
    {
        if ((uint)x < (uint)sw && (uint)y < (uint)sh) return src[y * sw + x];
        if (border == BorderMode.Constant) return bval;
        int cx = CvBorder.Interpolate(x, sw, borderX);
        int cy = CvBorder.Interpolate(y, sh, borderY);
        return src[cy * sw + cx];
    }

    private static void CheckDst(Span<byte> dst, int dstStride, int dstWidth, int dstHeight)
    {
        if (dstWidth <= 0 || dstHeight <= 0) throw new ArgumentOutOfRangeException(nameof(dstWidth));
        if (dstStride < dstWidth) throw new ArgumentOutOfRangeException(nameof(dstStride));
        if (dst.Length < dstStride * (dstHeight - 1) + dstWidth)
            throw new ArgumentException("destination buffer is too small", nameof(dst));
    }
}

/// <summary><c>borderInterpolate</c> for the border modes the kernels can ask for.</summary>
internal static class CvBorder
{
    /// <summary>
    /// Maps the out-of-range coordinate <paramref name="p"/> of an axis of length
    /// <paramref name="len"/> to a real index. Returns -1 for <c>BORDER_CONSTANT</c> (the
    /// caller must use the border value) — exactly OpenCV's convention.
    /// </summary>
    internal static int Interpolate(int p, int len, int borderType)
    {
        if ((uint)p < (uint)len) return p;
        switch ((BorderMode)borderType)
        {
            case BorderMode.Replicate:
                return p < 0 ? 0 : len - 1;
            case BorderMode.Reflect:
            case BorderMode.Reflect101:
            {
                if (len == 1) return 0;
                int delta = (BorderMode)borderType == BorderMode.Reflect101 ? 1 : 0;
                do
                {
                    p = p < 0 ? -p - 1 + delta : len - 1 - (p - len) - delta;
                }
                while ((uint)p >= (uint)len);
                return p;
            }
            case BorderMode.Wrap:
            {
                if (len == 1) return 0;
                if (p < 0) p -= (p - len + 1) / len * len;
                if (p >= len) p %= len;
                return p;
            }
            default:
                return -1;
        }
    }
}
