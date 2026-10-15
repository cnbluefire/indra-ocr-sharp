using System;
using System.Collections.Generic;
using Indra.Ocr.Imaging;

namespace Indra.Ocr.Rec;

/// <summary>
/// CenterMask (<c>mask.onnx</c>): a batch of ink maps <c>[B, 1, 96, W]</c> in, centre-line
/// probabilities <c>[B, 1, 96, W]</c> out. The recognizer must only ever see crops that went
/// through the same exported mask model as training did.
/// </summary>
public interface IMaskModel
{
    /// <summary>Runs the model. <paramref name="x"/> and <paramref name="p"/> are both
    /// <c>[batch, 1, 96, width]</c>; the port never calls it with an empty batch.</summary>
    void Run(ReadOnlySpan<float> x, int batch, int width, Span<float> p);
}

/// <summary>One line window, with foreign ink whitened, plus the line box inside it.</summary>
/// <remarks>
/// <see cref="Pixels"/> is <see cref="Width"/>×<see cref="Height"/> bytes (a copy of the page
/// window); <see cref="Box"/> holds <c>x0, y0, x1, y1</c> in <b>double</b>, exactly like the
/// reference's list of Python floats, because <see cref="RecPreproc.CropLine"/> must not lose
/// the subtraction's precision.
/// </remarks>
public sealed class MaskedWindow
{
    public MaskedWindow(int x, int y, int width, int height, byte[] pixels, double[] box)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Pixels = pixels;
        Box = box;
    }

    /// <summary>Window origin on the page.</summary>
    public int X { get; }

    /// <summary>Window origin on the page.</summary>
    public int Y { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The window pixels, foreign ink replaced by the paper level.</summary>
    public byte[] Pixels { get; }

    /// <summary>The line box inside the window: <c>x0, y0, x1, y1</c> in doubles.</summary>
    public double[] Box { get; }
}

/// <summary>
/// Masks a line window with CenterMask before the recognizer crops the line — the port of
/// <c>indra_ocr/line_mask.py</c> (see <c>docs/085-line-mask.md</c>).
/// </summary>
public static class LineMask
{
    /// <summary>Window height in pixels; the centre line is about a third of it.</summary>
    public const int WindowH = 96;

    /// <summary>Box heights above and below the line box.</summary>
    public const double WindowAbove = 1.0;

    /// <summary>Box heights left and right.</summary>
    public const double WindowSide = 0.5;

    /// <summary>Pixels with a lower centre-line probability become paper.</summary>
    public const double Keep = 0.5;

    private static readonly byte[] WhitePixel = { 255 };

    /// <summary>The window around a line box (page pixels), clipped to the page.</summary>
    /// <param name="windowBox">Receives <c>x0, y0, x1, y1</c>.</param>
    public static void WindowBox(ReadOnlySpan<float> box, double pageWidth, double pageHeight,
                                 Span<double> windowBox)
    {
        double x0 = box[0], y0 = box[1], x1 = box[2], y1 = box[3];
        double h = Math.Max(1.0, y1 - y0);
        windowBox[0] = Math.Max(0.0, x0 - WindowSide * h);
        windowBox[1] = Math.Max(0.0, y0 - WindowAbove * h);
        windowBox[2] = Math.Min(pageWidth, x1 + WindowSide * h);
        windowBox[3] = Math.Min(pageHeight, y1 + WindowAbove * h);
    }

    /// <summary>
    /// The integer window rectangle: <c>window_box</c> rounded to integers (Python's
    /// ties-to-even <c>round</c>), i.e. the reference's <c>_window</c> slice bounds. Either
    /// extent can come out zero or negative for a box at (or outside) the page edge.
    /// </summary>
    public static void WindowRect(GrayImage page, ReadOnlySpan<float> box,
                                  out int x0, out int y0, out int x1, out int y1)
    {
        Span<double> b = stackalloc double[4];
        WindowBox(box, page.Width, page.Height, b);
        x0 = CvMath.Round(b[0]);
        y0 = CvMath.Round(b[1]);
        x1 = CvMath.Round(b[2]);
        y1 = CvMath.Round(b[3]);
    }

    /// <summary>
    /// Centre-line keep masks at window resolution, one per box. The probabilities are scaled
    /// back to the window bilinearly before the threshold: a threshold at mask resolution with
    /// a nearest-neighbour upscale breaks the stroke edges (surya OCR: 2 CER on M804 lines
    /// 0.30 → 0.36).
    /// </summary>
    public static bool[][] MaskWindows(IMaskModel model, GrayImage page, ReadOnlySpan<float> boxes)
    {
        int n = boxes.Length / 4;
        var keep = new bool[n][];
        if (n == 0) return keep;

        var inks = new float[n][];
        var sizes = new (int Width, int Height)[n];
        var live = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            WindowRect(page, boxes.Slice(i * 4, 4), out int x0, out int y0, out int x1, out int y1);
            int wd = Math.Max(0, x1 - x0), h = Math.Max(0, y1 - y0);
            inks[i] = WindowInk(page, x0, y0, wd, h);
            sizes[i] = (wd, h);
            if (inks[i].Length == 0) keep[i] = new bool[wd * h];   // degenerate: nothing is kept
            else live.Add(i);
        }

        // np.argsort of the ink widths, but stable (see AGENTS.md: numpy's tie order is
        // machine-specific). Similar widths go into one call of at most 16 windows, so little
        // compute goes to padding.
        var order = live.ToArray();
        Array.Sort(order, (a, b) =>
        {
            int c = InkWidth(inks[a]).CompareTo(InkWidth(inks[b]));
            return c != 0 ? c : a.CompareTo(b);
        });

        for (int k = 0; k < order.Length; k += 16)
        {
            int count = Math.Min(16, order.Length - k);
            int batchWidth = 0;
            for (int j = 0; j < count; j++)
                batchWidth = Math.Max(batchWidth, InkWidth(inks[order[k + j]]));

            var batch = new float[count * WindowH * batchWidth];
            for (int j = 0; j < count; j++)
            {
                var ink = inks[order[k + j]];
                int lineWidth = InkWidth(ink);
                for (int y = 0; y < WindowH; y++)
                    ink.AsSpan(y * lineWidth, lineWidth)
                       .CopyTo(batch.AsSpan((j * WindowH + y) * batchWidth));
            }

            var probabilities = new float[batch.Length];
            model.Run(batch, count, batchWidth, probabilities);

            for (int j = 0; j < count; j++)
            {
                int i = order[k + j];
                var (wd, h) = sizes[i];
                int lineWidth = InkWidth(inks[i]);
                var windowed = new float[lineWidth * WindowH];
                for (int y = 0; y < WindowH; y++)
                    probabilities.AsSpan((j * WindowH + y) * batchWidth, lineWidth)
                                 .CopyTo(windowed.AsSpan(y * lineWidth));

                var resized = new float[wd * h];
                CvResize.Resize32f(windowed, lineWidth, WindowH, resized, wd, h, Interpolation.Linear);
                var mask = new bool[resized.Length];
                for (int q = 0; q < resized.Length; q++) mask[q] = resized[q] >= Keep;
                keep[i] = mask;
            }
        }
        return keep;
    }

    /// <summary>One masked window per box: the window with foreign ink whitened, and the box
    /// inside it (page offset subtracted).</summary>
    public static MaskedWindow[] MaskedWindows(IMaskModel model, GrayImage page, ReadOnlySpan<float> boxes)
    {
        int n = boxes.Length / 4;
        var keep = MaskWindows(model, page, boxes);
        var windows = new MaskedWindow[n];
        for (int i = 0; i < n; i++)
        {
            var box = boxes.Slice(i * 4, 4);
            WindowRect(page, box, out int x0, out int y0, out int x1, out int y1);
            int wd = Math.Max(0, x1 - x0), h = Math.Max(0, y1 - y0);

            var pixels = new byte[wd * h];
            if (wd > 0 && h > 0)
                for (int y = 0; y < h; y++)
                    page.Row(y0 + y).Slice(x0, wd).CopyTo(pixels.AsSpan(y * wd));

            if (pixels.Length > 0)
            {
                int paper = PaperLevel(pixels, wd, h);
                var k = keep[i];
                for (int q = 0; q < pixels.Length; q++)
                    if (!k[q]) pixels[q] = (byte)paper;
            }

            windows[i] = new MaskedWindow(x0, y0, wd, h, pixels,
                                          new[] { (double)box[0] - x0, (double)box[1] - y0,
                                                  (double)box[2] - x0, (double)box[3] - y0 });
        }
        return windows;
    }

    /// <summary>
    /// Recognizer inputs for the line boxes of one page: <see cref="RecPreproc.CropLine"/> of
    /// each line, after CenterMask when <paramref name="model"/> is given (one batched call per
    /// page). Widths are <c>crop.Length / height</c>.
    /// </summary>
    public static List<float[]> CropLines(GrayImage page, ReadOnlySpan<float> boxes, int height,
                                          IMaskModel? model = null)
    {
        var crops = new List<float[]>(boxes.Length / 4);
        if (model is null)
        {
            for (int i = 0; i + 3 < boxes.Length; i += 4)
                crops.Add(RecPreproc.CropLine(page, boxes.Slice(i, 4), out _, height));
            return crops;
        }

        foreach (var window in MaskedWindows(model, page, boxes))
        {
            // A window that the page clip collapsed is empty; the reference would raise in
            // cv2.resize here, so the port only has to stay sane (white paper).
            var source = window.Width > 0 && window.Height > 0
                ? new GrayImage(window.Width, window.Height, window.Pixels)
                : new GrayImage(1, 1, WhitePixel);
            crops.Add(RecPreproc.CropLine(source, window.Box, out _, height));
        }
        return crops;
    }

    private static int InkWidth(float[] ink) => ink.Length / WindowH;

    /// <summary>Window ink map at mask resolution: the window scaled to <c>96</c> rows.</summary>
    private static float[] WindowInk(GrayImage page, int x0, int y0, int wd, int h)
    {
        if (wd <= 0 || h <= 0) return Array.Empty<float>();
        int width = Math.Max(32, CvMath.Round((double)(wd * WindowH) / Math.Max(h, 1) / 8.0) * 8);
        var resized = new byte[width * WindowH];
        double shrink = (double)WindowH / Math.Max(1, h);
        CvResize.Resize8u(page.Slice(x0, y0, wd, h), resized, width, WindowH,
                          shrink >= 0.5 ? Interpolation.Linear : Interpolation.Area);

        var ink = new float[resized.Length];
        for (int i = 0; i < ink.Length; i++) ink[i] = 1f - resized[i] / 255f;
        return ink;
    }

    /// <summary>
    /// Paper level: the 90th percentile grey of the window on a 1/16 subsample, truncated to a
    /// byte (<c>np.uint8</c>). <c>255</c> for an empty window, like the reference.
    /// </summary>
    private static int PaperLevel(ReadOnlySpan<byte> pixels, int width, int height)
    {
        int subW = (width + 3) / 4, subH = (height + 3) / 4;
        var sub = new double[subW * subH];
        for (int y = 0, sy = 0; y < height; y += 4, sy++)
            for (int x = 0, sx = 0; x < width; x += 4, sx++)
                sub[sy * subW + sx] = pixels[y * width + x];
        return (int)Numerics.Percentile(sub, 90);
    }
}
