using System;
using System.Collections.Generic;
using Indra.Ocr.Imaging;

namespace Indra.Ocr.Rec;

/// <summary>
/// The line crop rule shared by recognition training and inference — the port of
/// <c>indra_ocr/rec_preproc.py</c> (see <c>docs/080-rec-preproc.md</c>).
/// </summary>
/// <remarks>
/// A recognizer input is made from a line box in three steps: expand the box by
/// <see cref="PadY"/> × its height vertically and <see cref="PadX"/> × its height horizontally,
/// crop that region from the page (white outside the page), and resize the crop to
/// <c>height</c> pixels keeping the aspect ratio. Training and inference must both go through
/// <see cref="CropLine"/>, so text scale and margins match.
/// </remarks>
public static class RecPreproc
{
    /// <summary>Recognizer input height.</summary>
    public const int LineHeight = 32;

    /// <summary>Vertical padding, as a fraction of the line height, on each side.</summary>
    public const double PadY = 0.2;

    /// <summary>Horizontal padding, as a fraction of the line height, on each side.</summary>
    public const double PadX = 0.3;

    /// <summary>Line widths are padded to a multiple of this (the CTC timestep stride).</summary>
    public const int WidthMultiple = 8;

    /// <summary>
    /// Crops one line from a grayscale page and returns the float32 ink map, <c>height</c> rows
    /// of <paramref name="outWidth"/> values (0 = paper, 1 = ink).
    /// </summary>
    /// <param name="gray">The whole page.</param>
    /// <param name="box">The line box in page pixels: <c>x0, y0, x1, y1</c> (float32, as the
    /// detector or the PDF text layer produced them).</param>
    /// <param name="outWidth">The crop width the caller gets back; it is the reconciled width
    /// (aspect ratio, 8-multiple, <paramref name="maxWidth"/> clamp), not the box width.</param>
    public static float[] CropLine(GrayImage gray, ReadOnlySpan<float> box, out int outWidth,
                                   int height = LineHeight, double padY = PadY, double padX = PadX,
                                   int maxWidth = 4096)
    {
        // The reference converts the (float32) box to Python floats - i.e. to double.
        double x0 = box[0], y0 = box[1], x1 = box[2], y1 = box[3];
        double h = Math.Max(1.0, y1 - y0);
        double xLo = x0 - padX * h, xHi = x1 + padX * h;
        double yLo = y0 - padY * h, yHi = y1 + padY * h;

        int pageW = gray.Width, pageH = gray.Height;
        int ix0 = (int)Math.Floor(xLo), iy0 = (int)Math.Floor(yLo);
        int ix1 = (int)Math.Ceiling(xHi), iy1 = (int)Math.Ceiling(yHi);
        int cw = Math.Max(1, ix1 - ix0), ch = Math.Max(1, iy1 - iy0);

        var crop = new byte[ch * cw];
        Array.Fill(crop, (byte)255);                     // white outside the page
        int sx0 = Math.Max(0, ix0), sy0 = Math.Max(0, iy0);
        int sx1 = Math.Min(pageW, ix1), sy1 = Math.Min(pageH, iy1);
        if (sx1 > sx0 && sy1 > sy0)
        {
            int runLength = sx1 - sx0;
            for (int y = sy0; y < sy1; y++)
                gray.Row(y).Slice(sx0, runLength).CopyTo(crop.AsSpan((y - iy0) * cw + (sx0 - ix0)));
        }

        // round() is ties-to-even, like Python's.
        outWidth = Math.Max(WidthMultiple,
                            Math.Min(maxWidth,
                                     (int)Math.Round((double)(cw * height) / ch, MidpointRounding.ToEven)));
        var interpolation = ch > height ? Interpolation.Area : Interpolation.Linear;
        var resized = new byte[outWidth * height];
        CvResize.Resize8u(new GrayImage(cw, ch, crop), resized, outWidth, height, interpolation);

        var ink = new float[outWidth * height];
        for (int i = 0; i < ink.Length; i++) ink[i] = 1f - resized[i] / 255f;
        return ink;
    }

    /// <summary>
    /// Right-pads line ink maps to one width, so a batch is a single <c>[B, 1, height, width]</c>
    /// tensor. Every line must be <paramref name="height"/> rows of a common width
    /// <c>line.Length / height</c>; the per-line widths are written to <paramref name="widths"/>.
    /// </summary>
    /// <returns>The padded batch (zero = paper, so padding is invisible to the recognizer).</returns>
    public static float[] PadBatch(IReadOnlyList<float[]> lines, int height, long[] widths,
                                   out int width, int multiple = WidthMultiple)
    {
        if (lines.Count == 0) throw new ArgumentException("no lines to batch", nameof(lines));
        if (widths.Length < lines.Count)
            throw new ArgumentException($"widths needs {lines.Count} entries", nameof(widths));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        int widest = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i] ?? throw new ArgumentException($"line {i} is null", nameof(lines));
            if (line.Length % height != 0)
                throw new ArgumentException($"line {i} holds {line.Length} values, not a multiple of {height}",
                                            nameof(lines));
            widest = Math.Max(widest, line.Length / height);
        }

        width = ((widest + multiple - 1) / multiple) * multiple;
        var batch = new float[lines.Count * height * width];
        for (int i = 0; i < lines.Count; i++)
        {
            int lineWidth = lines[i].Length / height;
            for (int y = 0; y < height; y++)
                lines[i].AsSpan(y * lineWidth, lineWidth).CopyTo(batch.AsSpan((i * height + y) * width));
            widths[i] = lineWidth;
        }
        return batch;
    }

    /// <summary>
    /// Groups line indices into batches of similar width, so little compute goes to padding.
    /// Lines are taken narrowest first (a stable sort, like Python's <c>sorted</c>). A batch
    /// closes at <paramref name="maxBatch"/> lines, when the next line is wider than
    /// <paramref name="maxRatio"/> times the batch's first line (widths below
    /// <paramref name="minWidth"/> count as <paramref name="minWidth"/>, so short lines do not
    /// fragment into tiny batches), or when lines × width would pass <paramref name="maxArea"/>
    /// pixel columns: ONNX Runtime keeps its largest allocation, and 64 lines of 4096 px held
    /// 5 GB on large scanned pages.
    /// </summary>
    public static List<int[]> WidthBatches(ReadOnlySpan<int> widths, int maxBatch = 64,
                                           double maxRatio = 1.3, int minWidth = 64,
                                           int maxArea = 64 * 640)
    {
        // (a span cannot be captured by the comparison lambda, hence the copy)
        var w = widths.ToArray();
        var order = new int[w.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = w[a].CompareTo(w[b]);
            return c != 0 ? c : a.CompareTo(b);          // stable, like Python's sorted()
        });

        var batches = new List<List<int>>();
        foreach (int i in order)
        {
            int width = Math.Max(w[i], minWidth);
            if (batches.Count > 0)
            {
                var last = batches[^1];
                int firstWidth = Math.Max(w[last[0]], minWidth);
                if (last.Count < maxBatch
                    && width <= maxRatio * firstWidth
                    && (last.Count + 1) * width <= maxArea)
                {
                    last.Add(i);
                    continue;
                }
            }
            batches.Add(new List<int> { i });
        }

        var result = new List<int[]>(batches.Count);
        foreach (var b in batches) result.Add(b.ToArray());
        return result;
    }
}
