using System;
using Indra.Ocr.Imaging;

namespace Indra.Ocr.Pipeline;

/// <summary>
/// <c>orient.onnx</c>: a page thumbnail as a float32 ink map (1 = ink) in, the four quarter-turn
/// probabilities out.
/// </summary>
/// <remarks>
/// One image per call, mirroring <c>sess.run(None, {"x": orient_input(gray)[None, None]})</c> in
/// <c>pipeline.OrtOCR.orientation</c>. The shipped model set does contain <c>orient.onnx</c>;
/// <c>OcrRunner</c> leaves the model out when the CLI asks for <c>--no-turn</c>.
/// </remarks>
public interface IOrientModel
{
    /// <summary>Runs the model. <paramref name="x"/> is <c>[1, 1, side, side]</c> row-major,
    /// <paramref name="p"/> receives the four probabilities.</summary>
    void Run(ReadOnlySpan<float> x, Span<float> p);
}

/// <summary>
/// Port of the page-uprighting half of <c>indra_ocr/pipeline.py</c>: the orientation model
/// (<see cref="Turns"/>), the deskew search (<see cref="EstimateSkew"/>) and the two rotations
/// that apply them (<see cref="Rotate90"/>, <see cref="RotatePage"/>), plus
/// <see cref="UnrotateBox"/>, which maps a straightened box back onto the turned page.
/// </summary>
public static class Orientation
{
    /// <summary>ORIENT_SIDE: <c>orient.onnx</c> input side, the page's long side padded into a square.</summary>
    public const int OrientSide = 768;

    /// <summary>
    /// ORIENT_MIN: a turn is applied only above this probability. Real pages are almost all
    /// upright and a wrong turn loses the page (0.95 turned 2 of 1,000 upright eval pages,
    /// 0.5 turned 13).
    /// </summary>
    /// <remarks>
    /// Compared against a float32 probability, so this is numpy's NEP 50 float32 comparison with
    /// <c>0.95</c> cast to float32 (<c>0.949999988079071</c>).
    /// </remarks>
    public const float OrientMin = 0.95f;

    /// <summary>DESKEW_MAX: degrees of tilt searched, in 0.5 degree steps and then 0.1 degree steps.</summary>
    public const double DeskewMax = 5.0;

    /// <summary>DESKEW_MIN: smaller tilts are left alone (the search step is 0.1).</summary>
    public const double DeskewMin = 0.3;

    /// <summary><c>estimate_skew</c>'s default thumbnail long side.</summary>
    public const int DeskewSide = 800;

    /// <summary>Mean ink under this and the page counts as blank: no rows to straighten.</summary>
    public const float BlankInk = 0.003f;

    // ------------------------------------------------------------------ orientation

    /// <summary>
    /// <c>orient_input</c>: the page's long side scaled to <paramref name="side"/> with
    /// <c>INTER_AREA</c>, then <c>1 - gray/255</c> into the top-left of a zeroed square.
    /// </summary>
    public static void OrientInput(GrayImage gray, Span<float> dst, int side = OrientSide)
    {
        if (side <= 0) throw new ArgumentOutOfRangeException(nameof(side));
        if (dst.Length < side * side)
            throw new ArgumentException($"need {side * side} floats", nameof(dst));
        double s = side / (double)Math.Max(gray.Height, gray.Width);
        int w = Math.Max(1, CvMath.Round(gray.Width * s));
        int h = Math.Max(1, CvMath.Round(gray.Height * s));
        var small = new byte[w * h];
        CvResize.Resize8u(gray, small, w, h, Interpolation.Area);

        var outv = dst[..(side * side)];
        outv.Clear();  // np.zeros: the padding stays 0 (paper in the ink map)
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                outv[y * side + x] = 1f - small[y * w + x] / 255f;
    }

    /// <summary>
    /// Quarter turns (counter-clockwise) the page content is turned by, from the orientation
    /// model: 0 when no turn is more likely than <see cref="OrientMin"/>. <c>argmax</c> takes the
    /// first maximum, as numpy's does.
    /// </summary>
    public static int Turns(IOrientModel model, GrayImage gray, int side = OrientSide)
    {
        ArgumentNullException.ThrowIfNull(model);
        var x = new float[side * side];
        OrientInput(gray, x, side);
        Span<float> p = stackalloc float[4];
        model.Run(x, p);
        int k = Numerics.ArgMax(p);
        return p[k] > OrientMin ? k : 0;
    }

    // ------------------------------------------------------------------ deskew

    /// <summary>
    /// <c>estimate_skew</c>: degrees (counter-clockwise) that straighten the text rows of a
    /// page — the angle whose horizontal ink profile changes most sharply row to row, on a page
    /// thumbnail, in 0.5 degree steps and then 0.1 degree steps.
    /// </summary>
    /// <param name="threads">How many angles to score at once, i.e. the reference's
    /// <c>cv2.setNumThreads(threads)</c>: OpenCV splits one <c>warpAffine</c> across threads
    /// where this scores independent angles in parallel. <c>1</c> is the worker-pool case.</param>
    public static double EstimateSkew(GrayImage gray, int side = DeskewSide, int threads = 1)
    {
        float[] ink = SkewThumbnail(gray, side, out int w, out int h);
        if (Numerics.Mean32(ink) < BlankInk) return 0.0;  // a blank page has no rows to straighten
        var coarse = NpyArange.Range(-DeskewMax, DeskewMax + 1e-6, 0.5);
        int best = BestAngle(ink, w, h, coarse, threads);
        var fine = NpyArange.Range(coarse[best] - 0.5, coarse[best] + 0.5 + 1e-6, 0.1);
        return fine[BestAngle(ink, w, h, fine, threads)];
    }

    /// <summary>Python's <c>max(angles, key=sharpness)</c>: the first angle with the largest
    /// sharpness.</summary>
    /// <remarks>
    /// Every angle is an independent computation — its own warp into its own buffer, reading the
    /// thumbnail — so scoring them in parallel gives the values the serial order gives, bit for
    /// bit; only then is the first maximum taken, in index order, as <c>max</c> does. What the
    /// angles cost is why this is worth splitting: 32 warps of an 800-long-side thumbnail are the
    /// pipeline's slowest step by far, and the reference has OpenCV's own threads on it.
    /// </remarks>
    private static int BestAngle(float[] ink, int width, int height, double[] angles, int threads)
    {
        var values = new float[angles.Length];
        if (threads > 1 && angles.Length > 1)
        {
            Parallel.For(0, angles.Length,
                         new ParallelOptions { MaxDegreeOfParallelism = threads },
                         i => values[i] = Sharpness(ink, width, height, angles[i]));
        }
        else
        {
            for (int i = 0; i < angles.Length; i++)
                values[i] = Sharpness(ink, width, height, angles[i]);
        }

        int best = 0;
        float bestValue = float.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            if (i == 0 || values[i] > bestValue)
            {
                bestValue = values[i];
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// <c>estimate_skew</c>'s score for one angle: the page thumbnail's ink map rotated by the
    /// angle, summed row by row, with the squared differences between neighbouring row sums added
    /// up. Larger means the rows line up better.
    /// </summary>
    /// <param name="ink">A tightly packed <c>[height, width]</c> float32 ink map (1 = ink).</param>
    public static float Sharpness(ReadOnlySpan<float> ink, int width, int height, double angle)
    {
        if (ink.Length < width * height)
            throw new ArgumentException($"need {width * height} floats", nameof(ink));
        var rotated = new float[width * height];
        Span<double> m = stackalloc double[6];
        CvWarpAffine.RotationMatrix2D((float)(width / 2.0), (float)(height / 2.0), angle, 1.0, m);
        CvWarpAffine.WarpAffine32f(ink, width, height, rotated, width, width, height, m,
                                  BorderMode.Constant);

        var profile = new float[height];
        for (int y = 0; y < height; y++)
            profile[y] = Numerics.Sum32(rotated.AsSpan(y * width, width));

        var diffs = new float[height - 1];
        for (int y = 0; y + 1 < height; y++)
        {
            diffs[y] = profile[y + 1] - profile[y];
            diffs[y] *= diffs[y];
        }
        return Numerics.Sum32(diffs);
    }

    /// <summary>
    /// <c>estimate_skew</c>'s thumbnail: the page's long side scaled to <paramref name="side"/>
    /// with <c>INTER_AREA</c>, turned into a float32 ink map (1 = ink), tightly packed
    /// <c>[height, width]</c>.
    /// </summary>
    public static float[] SkewThumbnail(GrayImage gray, int side, out int width, out int height)
    {
        double s = side / (double)Math.Max(gray.Height, gray.Width);
        width = Math.Max(1, CvMath.Round(gray.Width * s));
        height = Math.Max(1, CvMath.Round(gray.Height * s));
        var small = new byte[width * height];
        CvResize.Resize8u(gray, small, width, height, Interpolation.Area);

        var ink = new float[width * height];
        for (int i = 0; i < ink.Length; i++)
            ink[i] = (255f - small[i]) / 255f;
        return ink;
    }

    // ------------------------------------------------------------------ the two rotations

    /// <summary>
    /// <c>np.rot90(src, quarterTurns)</c>: rotates counter-clockwise (negative = clockwise) and
    /// swaps the dimensions for every odd turn. <c>np.ascontiguousarray</c> is implied — the
    /// result is written out tightly.
    /// </summary>
    public static void Rotate90(GrayImage src, Span<byte> dst, int quarterTurns)
    {
        int k = ((quarterTurns % 4) + 4) % 4;
        int w = src.Width, h = src.Height;
        int dw = k % 2 == 0 ? w : h, dh = k % 2 == 0 ? h : w;
        if (dst.Length < dw * dh)
            throw new ArgumentException($"need {dw * dh} bytes", nameof(dst));
        for (int i = 0; i < dh; i++)
            for (int j = 0; j < dw; j++)
                dst[i * dw + j] = k switch
                {
                    0 => src[i, j],
                    1 => src[j, w - 1 - i],
                    2 => src[h - 1 - i, w - 1 - j],
                    _ => src[h - 1 - j, i],
                };
    }

    /// <summary>
    /// <c>rotate_page</c>: the page rotated by <paramref name="degrees"/> counter-clockwise
    /// about its centre (same size), replicating the border.
    /// </summary>
    public static void RotatePage(GrayImage gray, double degrees, Span<byte> dst, int dstStride)
        => CvWarpAffine.Rotate8u(gray, dst, dstStride, gray.Width, gray.Height, degrees);

    /// <summary>
    /// <c>unrotate_box</c>: the bounding box of the four corners of <paramref name="box"/> mapped
    /// back through a <c>rotate_page(.., degrees)</c>, clipped to the page and rounded to one
    /// decimal (Python's <c>round</c>, i.e. ties to even on the decimal).
    /// </summary>
    /// <remarks>The corners are transformed in float64 and the results stay float64, so callers
    /// should keep them in <c>double</c> (<c>RecPreproc.CropLine</c> has a double overload).</remarks>
    public static void UnrotateBox(ReadOnlySpan<float> box, double degrees, int width, int height,
                                   Span<double> dst)
    {
        if (box.Length < 4) throw new ArgumentException("need 4 box values", nameof(box));
        UnrotateBox(box[0], box[1], box[2], box[3], degrees, width, height, dst);
    }

    /// <summary>
    /// <c>unrotate_box</c> for a float64 box — the reference's <c>ln.box</c> / <c>b["bbox"]</c>,
    /// which are Python floats (the line boxes were rounded to one decimal by <c>assemble</c>,
    /// the block boxes by <c>layout_post</c>), so narrowing them to float32 would move the
    /// corners.
    /// </summary>
    public static void UnrotateBox(ReadOnlySpan<double> box, double degrees, int width, int height,
                                   Span<double> dst)
    {
        if (box.Length < 4) throw new ArgumentException("need 4 box values", nameof(box));
        UnrotateBox(box[0], box[1], box[2], box[3], degrees, width, height, dst);
    }

    private static void UnrotateBox(double x0, double y0, double x1, double y1, double degrees,
                                    int width, int height, Span<double> dst)
    {
        if (dst.Length < 4) throw new ArgumentException("need 4 doubles", nameof(dst));
        Span<double> m = stackalloc double[6];
        CvWarpAffine.RotationMatrix2D((float)(width / 2.0), (float)(height / 2.0), -degrees, 1.0, m);
        Span<double> xs = stackalloc double[4] { x0, x1, x0, x1 };
        Span<double> ys = stackalloc double[4] { y0, y0, y1, y1 };

        double cxMin = double.PositiveInfinity, cxMax = double.NegativeInfinity;
        double cyMin = double.PositiveInfinity, cyMax = double.NegativeInfinity;
        for (int k = 0; k < 4; k++)
        {
            double cx = m[0] * xs[k] + m[1] * ys[k] + m[2];
            double cy = m[3] * xs[k] + m[4] * ys[k] + m[5];
            if (cx < cxMin) cxMin = cx;
            if (cx > cxMax) cxMax = cx;
            if (cy < cyMin) cyMin = cy;
            if (cy > cyMax) cyMax = cy;
        }
        dst[0] = RoundTenth(Math.Clamp(cxMin, 0, width));
        dst[1] = RoundTenth(Math.Clamp(cyMin, 0, height));
        dst[2] = RoundTenth(Math.Clamp(cxMax, 0, width));
        dst[3] = RoundTenth(Math.Clamp(cyMax, 0, height));
    }

    /// <summary>
    /// Python's <c>round(x, 1)</c>: correctly rounded to one decimal, ties to even.
    /// </summary>
    /// <remarks>
    /// The reference rounds <c>unrotate_box</c>'s corners this way. .NET's <c>Math.Round</c> is
    /// correctly rounded for the digit overloads (since .NET Core 3.0), so it agrees with
    /// CPython's `round` on every double — including values that are not exactly representable,
    /// where the *stored* double decides (`2.675` is above the tie and rounds up, `1.005` below
    /// it and rounds down). The fixture's <c>round</c> cases pin both signs.
    /// </remarks>
    public static double RoundTenth(double x) => Math.Round(x, 1, MidpointRounding.ToEven);

    // ------------------------------------------------------------------ the whole step

    /// <summary>
    /// <c>upright</c>: the page turned upright (orientation model) and straightened
    /// (<see cref="EstimateSkew"/>), returning the pixels of the upright page plus the quarter
    /// turns undone and the degrees of skew undone.
    /// </summary>
    /// <param name="model">The orientation model, or <c>null</c> for <c>turn=False</c> (the page
    /// is known to be upright).</param>
    /// <param name="threads">Threads for the deskew search (<see cref="EstimateSkew"/>); the
    /// reference gives OpenCV the same number (<c>cv2.setNumThreads</c>).</param>
    /// <returns>The upright page's pixels — or <c>null</c> when the page did not change, in which
    /// case the caller's buffer behind <paramref name="gray"/> is still the page (the reference
    /// returns the same array object in that case).</returns>
    public static byte[]? Upright(IOrientModel? model, GrayImage gray,
                                  out int width, out int height, out int turns, out double skew,
                                  int threads = 1)
        => Upright(model, gray, OrientSide, DeskewSide, out width, out height, out turns, out skew,
                   threads);

    /// <inheritdoc cref="Upright(IOrientModel?, GrayImage, out int, out int, out int, out double, int)"/>
    public static byte[]? Upright(IOrientModel? model, GrayImage gray, int orientSide, int deskewSide,
                                  out int width, out int height, out int turns, out double skew,
                                  int threads = 1)
    {
        byte[]? page = null;
        turns = model is null ? 0 : Turns(model, gray, orientSide);
        width = gray.Width;
        height = gray.Height;
        if (turns != 0)
        {
            var turned = new byte[gray.PixelCount];
            Rotate90(gray, turned, -turns);
            page = turned;
            if (turns % 2 == 1) (width, height) = (height, width);   // only odd turns swap
        }

        var view = page is null ? gray : new GrayImage(width, height, page);
        skew = EstimateSkew(view, deskewSide, threads);
        if (Math.Abs(skew) >= DeskewMin)
        {
            var straightened = new byte[view.PixelCount];
            RotatePage(view, skew, straightened, view.Width);
            page = straightened;
        }
        else
        {
            skew = 0.0;
        }
        return page;
    }
}
