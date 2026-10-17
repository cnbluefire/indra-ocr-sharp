namespace Indra.Ocr.Imaging;

/// <summary>
/// Pure-managed port of the <c>cv::resize</c> paths the pipeline uses, bit-exact against
/// OpenCV 5.x for 8-bit input (see <c>docs/040-opencv-primitives.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The shapes OpenCV actually runs are reproduced, including the fixed-point weights and the
/// integer rounding of the 8-bit kernels:
/// <list type="bullet">
///   <item><c>INTER_AREA</c> with an integer scale (downscale by 2, 3, 4, ...) uses the
///     "area fast" sum-and-scale path; a 2x2 area is summed and rounded with
///     <c>(sum + 2) &gt;&gt; 2</c>, other areas with <c>cvRound(sum * (1/area))</c>;</item>
///   <item><c>INTER_AREA</c> with a fractional scale uses the decimation tables
///     (<c>computeResizeAreaTab</c>, float accumulation);</item>
///   <item>everything else uses the generic bilinear path with <c>short</c> fixed-point
///     weights (<c>INTER_RESIZE_COEF_BITS</c> = 11) and
///     <c>((b0*(S0&gt;&gt;4))&gt;&gt;16 + (b1*(S1&gt;&gt;4))&gt;&gt;16 + 2) &gt;&gt; 2</c>.</item>
/// </list>
/// A <c>2x</c> downscale asked through <c>INTER_LINEAR</c> is promoted to <c>INTER_AREA</c>
/// (OpenCV does this because the two agree at that scale).
/// </para>
/// <para>
/// The output buffer belongs to the caller; <paramref name="dstStride"/> lets it be a padded
/// canvas (the detector pastes into a white-padded square).
/// </para>
/// </remarks>
public static class CvResize
{
    private const float CoefScale = CvMath.ResizeCoefScale;

    // ------------------------------------------------------------------ 8-bit

    /// <summary>Resizes an 8-bit image into a tightly packed buffer.</summary>
    public static void Resize8u(GrayImage src, Span<byte> dst, int dstWidth, int dstHeight,
                                Interpolation interpolation)
        => Resize8u(src, dst, dstWidth, dstWidth, dstHeight, interpolation);

    /// <summary>Resizes an 8-bit image into a buffer whose rows are <paramref name="dstStride"/> bytes apart.</summary>
    public static void Resize8u(GrayImage src, Span<byte> dst, int dstStride, int dstWidth, int dstHeight,
                                Interpolation interpolation)
    {
        CheckDst(dst, dstStride, dstWidth, dstHeight, src.Width, src.Height);
        int sw = src.Width, sh = src.Height;
        double invScaleX = (double)dstWidth / sw;
        double invScaleY = (double)dstHeight / sh;
        double scaleX = 1.0 / invScaleX;
        double scaleY = 1.0 / invScaleY;
        int iscaleX = CvMath.Round(scaleX);
        int iscaleY = CvMath.Round(scaleY);
        bool isAreaFast = Math.Abs(scaleX - iscaleX) < CvMath.DblEpsilon
                       && Math.Abs(scaleY - iscaleY) < CvMath.DblEpsilon;

        var interp = interpolation;
        // A 2x INTER_LINEAR *is* INTER_AREA, and OpenCV takes the cheaper area kernel.
        if (interp == Interpolation.Linear && isAreaFast && iscaleX == 2 && iscaleY == 2)
            interp = Interpolation.Area;

        if (interp == Interpolation.Area && scaleX >= 1 && scaleY >= 1)
        {
            if (isAreaFast) AreaFast8u(src, dst, dstStride, dstWidth, dstHeight, iscaleX, iscaleY);
            else AreaGeneral8u(src, dst, dstStride, dstWidth, dstHeight, scaleX, scaleY, invScaleX, invScaleY);
            return;
        }

        Linear8u(src, dst, dstStride, dstWidth, dstHeight, interp == Interpolation.Area,
                 scaleX, scaleY, invScaleX, invScaleY);
    }

    /// <summary>Integer-factor downscale: every output pixel is the mean of an
    /// <c>iscaleX * iscaleY</c> source block.</summary>
    private static void AreaFast8u(GrayImage src, Span<byte> dst, int dstStride, int dw, int dh,
                                   int isx, int isy)
    {
        int sw = src.Width, sh = src.Height;
        float scale = 1f / (isx * isy);
        int dwidth1 = sw / isx;
        bool fastMode = isx == 2 && isy == 2;

        for (int dy = 0; dy < dh; dy++)
        {
            int sy0 = dy * isy;
            var dstRow = dst.Slice(dy * dstStride, dw);

            if (sy0 >= sh)
            {
                dstRow.Clear(); // no source rows: OpenCV writes zeros
                continue;
            }

            int w = sy0 + isy <= sh ? dwidth1 : 0;
            if (fastMode)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    int sx = isx * dx;
                    int s0 = src[sy0, sx] + src[sy0, sx + 1];
                    int s1 = src[sy0 + 1, sx] + src[sy0 + 1, sx + 1];
                    dstRow[dx] = (byte)((s0 + s1 + 2) >> 2);
                }
            }
            else
            {
                for (int dx = 0; dx < w; dx++)
                {
                    int sx = isx * dx;
                    int s = 0;
                    for (int k = 0; k < isy; k++)
                        for (int j = 0; j < isx; j++)
                            s += src[sy0 + k, sx + j];
                    dstRow[dx] = CvMath.SaturateByte(s * scale);
                }
            }

            // Partially covered blocks on the right edge: the mean of the samples that fit.
            for (int dx = w; dx < dw; dx++)
            {
                int sx0 = isx * dx;
                int s = 0, count = 0;
                for (int sy = 0; sy < isy; sy++)
                {
                    if (sy0 + sy >= sh) break;
                    for (int sx = 0; sx < isx; sx++)
                    {
                        if (sx0 + sx >= sw) break;
                        s += src[sy0 + sy, sx0 + sx];
                        count++;
                    }
                }
                dstRow[dx] = count == 0 ? (byte)0 : CvMath.SaturateByte(s / (float)count);
            }
        }
    }

    /// <summary>One <c>DecimateAlpha</c> term: output column <see cref="Di"/> takes
    /// <see cref="Alpha"/> of source column <see cref="Si"/>.</summary>
    private readonly record struct DecimateAlpha(int Di, int Si, float Alpha);

    /// <summary><c>computeResizeAreaTab</c>: the fractional-overlap weights of one axis.</summary>
    private static (DecimateAlpha[] Tab, int Size) ResizeAreaTab(int ssize, int dsize, double scale)
    {
        var tab = new List<DecimateAlpha>(dsize * 2);
        for (int dx = 0; dx < dsize; dx++)
        {
            double fsx1 = dx * scale;
            double fsx2 = fsx1 + scale;
            double cellWidth = Math.Min(scale, ssize - fsx1);
            int sx1 = CvMath.Ceil(fsx1), sx2 = CvMath.Floor(fsx2);
            sx2 = Math.Min(sx2, ssize - 1);
            sx1 = Math.Min(sx1, sx2);

            if (sx1 - fsx1 > 1e-3)
                tab.Add(new DecimateAlpha(dx, sx1 - 1, (float)((sx1 - fsx1) / cellWidth)));
            for (int sx = sx1; sx < sx2; sx++)
                tab.Add(new DecimateAlpha(dx, sx, (float)(1.0 / cellWidth)));
            if (fsx2 - sx2 > 1e-3)
                tab.Add(new DecimateAlpha(dx, sx2, (float)(Math.Min(Math.Min(fsx2 - sx2, 1.0), cellWidth) / cellWidth)));
        }
        return (tab.ToArray(), tab.Count);
    }

    /// <summary>Fractional-scale downscale: source pixels are accumulated with the
    /// decimation weights in float, exactly in the table's order.</summary>
    private static void AreaGeneral8u(GrayImage src, Span<byte> dst, int dstStride, int dw, int dh,
                                      double scaleX, double scaleY, double invScaleX, double invScaleY)
    {
        int sw = src.Width, sh = src.Height;
        var (xtab, xtabSize) = ResizeAreaTab(sw, dw, scaleX);
        var (ytab, ytabSize) = ResizeAreaTab(sh, dh, scaleY);

        // First ytab entry of every output row (OpenCV asserts the rows are contiguous).
        var tabofs = new int[dh + 1];
        int dy = 0;
        for (int k = 0; k < ytabSize; k++)
            if (k == 0 || ytab[k].Di != ytab[k - 1].Di)
                tabofs[dy++] = k;
        tabofs[dy] = ytabSize;
        if (dy != dh)
            throw new InvalidOperationException($"area resize: {dy} of {dh} rows have weights");

        var buf = new float[dw];
        var sum = new float[dw];
        for (dy = 0; dy < dh; dy++)
        {
            var dstRow = dst.Slice(dy * dstStride, dw);
            // Rows share no source rows between groups, and the first contribution of a row is
            // `sum = beta * buf` on a zeroed accumulator, so a plain accumulating add matches.
            Array.Clear(sum, 0, sum.Length);
            for (int j = tabofs[dy]; j < tabofs[dy + 1]; j++)
            {
                float beta = ytab[j].Alpha;
                var srcRow = src.Row(ytab[j].Si);
                Array.Clear(buf, 0, buf.Length);
                for (int k = 0; k < xtabSize; k++)
                    buf[xtab[k].Di] += srcRow[xtab[k].Si] * xtab[k].Alpha;
                for (int dx = 0; dx < dw; dx++) sum[dx] += beta * buf[dx];
            }
            for (int dx = 0; dx < dw; dx++) dstRow[dx] = CvMath.SaturateByte(sum[dx]);
        }
    }

    /// <summary>Generic bilinear resize (also used by <c>INTER_AREA</c> when it scales up).</summary>
    private static void Linear8u(GrayImage src, Span<byte> dst, int dstStride, int dw, int dh,
                                 bool areaMode, double scaleX, double scaleY,
                                 double invScaleX, double invScaleY)
    {
        int sw = src.Width, sh = src.Height;
        var xofs = new int[dw];
        var ia0 = new short[dw];
        var ia1 = new short[dw];
        for (int dx = 0; dx < dw; dx++)
        {
            float fx;
            int sx;
            if (!areaMode)
            {
                fx = (float)((dx + 0.5) * scaleX - 0.5);
                sx = CvMath.Floor(fx);
                fx -= sx;
            }
            else
            {
                sx = CvMath.Floor(dx * scaleX);
                fx = (float)((dx + 1) - (sx + 1) * invScaleX);
                fx = fx <= 0 ? 0f : fx - CvMath.Floor(fx);
            }
            if (sx < 0) { fx = 0f; sx = 0; }
            if (sx + 1 >= sw) { if (sx >= sw - 1) fx = 0f; sx = sw - 1; }
            xofs[dx] = sx;
            ia0[dx] = CvMath.SaturateShort((1f - fx) * CoefScale);
            ia1[dx] = CvMath.SaturateShort(fx * CoefScale);
        }

        for (int dy = 0; dy < dh; dy++)
        {
            float fy;
            int sy;
            if (!areaMode)
            {
                fy = (float)((dy + 0.5) * scaleY - 0.5);
                sy = CvMath.Floor(fy);
                fy -= sy;
            }
            else
            {
                sy = CvMath.Floor(dy * scaleY);
                fy = (float)((dy + 1) - (sy + 1) * invScaleY);
                fy = fy <= 0 ? 0f : fy - CvMath.Floor(fy);
            }
            int ry0 = MathCompat.Clamp(sy, 0, sh - 1);
            int ry1 = MathCompat.Clamp(sy + 1, 0, sh - 1);
            short b0 = CvMath.SaturateShort((1f - fy) * CoefScale);
            short b1 = CvMath.SaturateShort(fy * CoefScale);

            var r0 = src.Row(ry0);
            var r1 = src.Row(ry1);
            var dstRow = dst.Slice(dy * dstStride, dw);
            for (int dx = 0; dx < dw; dx++)
            {
                int sx = xofs[dx];
                int i1 = Math.Min(sx + 1, sw - 1);
                int t0 = r0[sx] * ia0[dx] + r0[i1] * ia1[dx];
                int t1 = r1[sx] * ia0[dx] + r1[i1] * ia1[dx];
                dstRow[dx] = (byte)((((b0 * (t0 >> 4)) >> 16) + ((b1 * (t1 >> 4)) >> 16) + 2) >> 2);
            }
        }
    }

    // ------------------------------------------------------------------ 32-bit float

    /// <summary>
    /// Resizes a float32 image. Only the bilinear path is implemented — it is what
    /// <c>line_mask.mask_windows</c> asks for when scaling the mask probabilities back up.
    /// </summary>
    /// <remarks>
    /// <b>Known deviation:</b> OpenCV's float32 vertical pass contracts to an FMA chain
    /// (<c>v_muladd</c>) for the vectorized columns, so results can differ in the last bit.
    /// This path is only reached with <c>mask.onnx</c>, which the shipped model set does not
    /// include, and is noted in <c>docs/040</c>.
    /// </remarks>
    public static void Resize32f(ReadOnlySpan<float> src, int srcWidth, int srcHeight,
                                 Span<float> dst, int dstWidth, int dstHeight,
                                 Interpolation interpolation = Interpolation.Linear)
    {
        if (interpolation != Interpolation.Linear)
            throw new NotSupportedException($"{interpolation} resize of float32 images is not ported");
        Linear32f(src, srcWidth, srcHeight, dst, dstWidth, dstHeight);
    }

    private static void Linear32f(ReadOnlySpan<float> src, int sw, int sh,
                                  Span<float> dst, int dw, int dh)
    {
        double scaleX = 1.0 / ((double)dw / sw);
        double scaleY = 1.0 / ((double)dh / sh);

        var xofs = new int[dw];
        var ax0 = new float[dw];
        var ax1 = new float[dw];
        for (int dx = 0; dx < dw; dx++)
        {
            float fx = (float)((dx + 0.5) * scaleX - 0.5);
            int sx = CvMath.Floor(fx);
            fx -= sx;
            if (sx < 0) { fx = 0f; sx = 0; }
            if (sx + 1 >= sw) { if (sx >= sw - 1) fx = 0f; sx = sw - 1; }
            xofs[dx] = sx;
            ax0[dx] = 1f - fx;
            ax1[dx] = fx;
        }

        for (int dy = 0; dy < dh; dy++)
        {
            float fy = (float)((dy + 0.5) * scaleY - 0.5);
            int sy = CvMath.Floor(fy);
            fy -= sy;
            int ry0 = MathCompat.Clamp(sy, 0, sh - 1);
            int ry1 = MathCompat.Clamp(sy + 1, 0, sh - 1);
            float b0 = 1f - fy, b1 = fy;

            for (int dx = 0; dx < dw; dx++)
            {
                int sx = xofs[dx];
                int i1 = Math.Min(sx + 1, sw - 1);
                float t0 = src[ry0 * sw + sx] * ax0[dx] + src[ry0 * sw + i1] * ax1[dx];
                float t1 = src[ry1 * sw + sx] * ax0[dx] + src[ry1 * sw + i1] * ax1[dx];
                dst[dy * dw + dx] = t0 * b0 + t1 * b1;
            }
        }
    }

    private static void CheckDst(Span<byte> dst, int dstStride, int dstWidth, int dstHeight, int sw, int sh)
    {
        if (dstWidth <= 0 || dstHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "destination size must be positive");
        if (dstStride < dstWidth)
            throw new ArgumentOutOfRangeException(nameof(dstStride), "stride must be >= width");
        if (dst.Length < dstStride * (dstHeight - 1) + dstWidth)
            throw new ArgumentException("destination buffer is too small", nameof(dst));
        if (sw <= 0 || sh <= 0)
            throw new ArgumentException("source image is empty", nameof(sw));
    }
}
