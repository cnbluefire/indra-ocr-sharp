using Indra.Ocr.Imaging;

namespace Indra.Ocr.Det;

/// <summary>
/// LineNet, the line detector: a page's float32 ink map <c>[1, 1, H, W]</c> in, the raw
/// <c>[1, 3, H/2, W/2]</c> output (core logit, top distance, bottom distance) out.
/// </summary>
/// <remarks>
/// Mirrors the <c>self.det.run(None, {"x": x[None, None]})</c> of <c>pipeline.OrtOCR._detect_at</c>.
/// The seam lets the detection logic run on machines without <c>det.onnx</c>.
/// </remarks>
public interface IDetModel
{
    /// <summary>
    /// Runs the detector. <paramref name="x"/> is <c>[1, 1, height, width]</c> row-major;
    /// <paramref name="logits"/> receives <c>[3, height/2, width/2]</c>.
    /// </summary>
    void Run(ReadOnlySpan<float> x, int height, int width, Span<float> logits);
}

public interface IPictureDetModel : IDetModel
{
    float[]? TakePictureLogits(out int height, out int width);
}

public readonly record struct DetectionResult(float[] Boxes, float[] Pictures);

/// <summary>
/// Port of <c>pipeline.OrtOCR.detect</c>: one detection pass over the page, a second one at a
/// larger long side when the lines come back too short to read, and the boxes scaled back to page
/// pixels and clipped.
/// </summary>
/// <remarks>
/// The second pass exists because LineNet is trained at a fixed line scale: a page whose lines
/// land under <see cref="AdaptiveMinH"/> input pixels (a dense or large scan) is re-detected with
/// its long side grown until a line is about <see cref="AdaptiveTargetH"/> pixels tall, capped at
/// <see cref="AdaptiveMaxSide"/>. Only the *boxes in input pixels* are compared, so the second
/// pass is free to be a different size.
/// </remarks>
public static class Detect
{
    /// <summary>ADAPTIVE_MIN_H: median line height (input px) that triggers a second pass.</summary>
    public const double AdaptiveMinH = 9.0;

    /// <summary>ADAPTIVE_TARGET_H: the line height the second pass aims for.</summary>
    public const double AdaptiveTargetH = 12.0;

    /// <summary>ADAPTIVE_MAX_SIDE: the long side the second pass never exceeds.</summary>
    public const int AdaptiveMaxSide = 2048;

    /// <summary>
    /// <c>_detect_at</c>: one pass at <paramref name="longSide"/> (a page long side), returning
    /// the decoded boxes <c>[N, 5]</c> in <b>input</b> pixels plus the scale that was applied to
    /// the page.
    /// </summary>
    /// <remarks>
    /// The network works at half the input resolution (<c>OUT_STRIDE</c>), so the output grid is
    /// <c>height/2 × width/2</c>.
    /// </remarks>
    public static float[] DetectAt(IDetModel model, GrayImage gray, int longSide, out double scale)
    {
        Guard.NotNull(model);
        var x = DetPost.PageToInput(gray, longSide, out int height, out int width, out scale);
        int oh = height / DetPost.OutStride, ow = width / DetPost.OutStride;
        var logits = new float[3 * oh * ow];
        model.Run(x, height, width, logits);
        return DetPost.Decode(logits, oh, ow);
    }

    /// <summary>
    /// <c>detect</c>'s adaptive rule: the long side a second pass should use, or
    /// <paramref name="longSide"/> when the first pass stands. <paramref name="medianHeight"/> is
    /// the median box height of the first pass (NaN when there were fewer than eight boxes).
    /// </summary>
    /// <remarks>
    /// The rule needs at least eight boxes — a page with a handful of lines has nothing to gain
    /// from a second opinion — and, since <c>long_side * 12 / med_h</c> is what the target height
    /// asks for, a median at or above <see cref="AdaptiveMinH"/> already has it. The candidate is
    /// truncated to a multiple of 32 (the network's input stride) and must beat the first pass.
    /// </remarks>
    public static int AdaptiveLongSide(ReadOnlySpan<float> boxes, int longSide, out double medianHeight)
    {
        medianHeight = double.NaN;
        int count = boxes.Length / 5;
        if (count < 8) return longSide;

        var heights = new float[count];
        for (int i = 0; i < count; i++) heights[i] = boxes[i * 5 + 3] - boxes[i * 5 + 1];
        // The median stays in float32 (Numerics.Median); a NaN median never fires, like Python.
        medianHeight = Numerics.Median(heights);
        if (!(medianHeight < AdaptiveMinH)) return longSide;

        double wanted = Math.Min(AdaptiveMaxSide,
                                 longSide * AdaptiveTargetH / Math.Max(medianHeight, 1.0));
        int candidate = (int)wanted / DetPost.InputMultiple * DetPost.InputMultiple;
        return candidate > longSide ? candidate : longSide;
    }

    /// <summary>
    /// <c>detect</c>: the page's line boxes <c>[N, 5]</c> in page pixels
    /// (<c>x0, y0, x1, y1, score</c>), scaled and clipped to the page.
    /// </summary>
    /// <param name="longSide">The detector's long side (<c>det_long_side</c>, 1024).</param>
    /// <param name="adaptive">Whether the second pass may run.</param>
    public static float[] Run(IDetModel model, GrayImage gray, int longSide, bool adaptive = true)
    {
        var boxes = DetectAt(model, gray, longSide, out double scale);
        if (adaptive)
        {
            int second = AdaptiveLongSide(boxes, longSide, out _);
            if (second != longSide) boxes = DetectAt(model, gray, second, out scale);
        }
        if (boxes.Length != 0) ScaleAndClip(boxes, scale, gray.Width, gray.Height);
        return boxes;
    }

    public static DetectionResult RunAll(IDetModel model, GrayImage gray, int longSide,
                                         bool adaptive = true)
    {
        float[] boxes = DetectAt(model, gray, longSide, out double scale);
        if (adaptive)
        {
            int second = AdaptiveLongSide(boxes, longSide, out _);
            if (second != longSide) boxes = DetectAt(model, gray, second, out scale);
        }
        if (boxes.Length != 0) ScaleAndClip(boxes, scale, gray.Width, gray.Height);
        if (model is not IPictureDetModel picture ||
            picture.TakePictureLogits(out int h, out int w) is not float[] logits)
            return new DetectionResult(boxes, Array.Empty<float>());
        return new DetectionResult(boxes, PictureBoxes(logits, w, h, gray.Width, gray.Height, scale));
    }

    private static float[] PictureBoxes(float[] logits, int width, int height, int pageW, int pageH,
                                        double scale)
    {
        var mask = new byte[width * height];
        for (int i = 0; i < mask.Length; i++) mask[i] = logits[i] > 0 ? (byte)1 : (byte)0;
        var labels = new int[mask.Length];
        int count = CvConnectedComponents.LabelComponents8(new GrayImage(width, height, mask), labels);
        var stats = new int[count * 5];
        CvConnectedComponents.ComputeStats(labels, width, height, count, stats);
        var result = new List<float>();
        double factor = DetPost.OutStride / scale;
        double minArea = 0.004 * pageW * pageH;
        for (int k = 1; k < count; k++)
        {
            int o = k * 5;
            double x0 = stats[o] * factor, y0 = stats[o + 1] * factor;
            double x1 = (stats[o] + stats[o + 2]) * factor;
            double y1 = (stats[o + 1] + stats[o + 3]) * factor;
            if ((x1 - x0) * (y1 - y0) < minArea) continue;
            result.Add((float)x0); result.Add((float)y0); result.Add((float)Math.Min(x1, pageW));
            result.Add((float)Math.Min(y1, pageH));
        }
        return result.ToArray();
    }

    /// <summary>
    /// <c>detect</c>'s tail: input pixels to page pixels (columns 0-3 only, and in float32 — the
    /// scalar is numpy-weak, so it is cast down rather than widening the boxes), then
    /// <c>np.clip</c> to the page.
    /// </summary>
    private static void ScaleAndClip(float[] boxes, double scale, int width, int height)
    {
        float s = (float)scale;
        for (int i = 0; i < boxes.Length; i += 5)
        {
            boxes[i] /= s;
            boxes[i + 1] /= s;
            boxes[i + 2] /= s;
            boxes[i + 3] /= s;
            boxes[i] = MathCompat.Clamp(boxes[i], 0f, width);
            boxes[i + 1] = MathCompat.Clamp(boxes[i + 1], 0f, height);
            boxes[i + 2] = MathCompat.Clamp(boxes[i + 2], 0f, width);
            boxes[i + 3] = MathCompat.Clamp(boxes[i + 3], 0f, height);
        }
    }
}
