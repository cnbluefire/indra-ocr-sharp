using System;
using Indra.Ocr.Imaging;

namespace Indra.Ocr.Det;

/// <summary>
/// Torch-free pre- and post-processing for LineNet — the port of
/// <c>indra_ocr/det_post.py</c> (see <c>docs/070-det-post.md</c>).
/// </summary>
/// <remarks>
/// Only the inference half is ported: <c>encode_targets</c> builds training targets and is
/// never called by the CLI, the viewer or the pipeline, so it is intentionally omitted.
/// </remarks>
public static class DetPost
{
    /// <summary>Core band height as a fraction of the line ink height.</summary>
    public const double CoreFrac = 0.4;

    /// <summary>The core is shorter than the line by this fraction of its height, at each end.</summary>
    public const double CoreXShrink = 0.15;

    /// <summary>Output stride: the network works at 1/2 input resolution.</summary>
    public const int OutStride = 2;

    /// <summary>Input height and width must divide by this.</summary>
    public const int InputMultiple = 32;

    /// <summary>
    /// Resizes a grayscale page so its long side is <paramref name="longSide"/> and pads to a
    /// multiple of <see cref="InputMultiple"/> with white. Returns the float32 <c>[H, W]</c>
    /// ink map (0 = white) together with its padded size and the scale that was applied.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>page_to_input</c>. The resize only happens when the rounded size actually
    /// differs from the source (the Python control flow), and the ink map is
    /// <c>1 - gray / 255</c> evaluated in float32 — both matter for bit-parity.
    /// </remarks>
    public static float[] PageToInput(GrayImage gray, int longSide,
                                      out int height, out int width, out double scale)
    {
        int h = gray.Height, w = gray.Width;
        scale = (double)longSide / Math.Max(h, w);
        int nh = Math.Max(1, (int)Math.Round(h * scale, MidpointRounding.ToEven));
        int nw = Math.Max(1, (int)Math.Round(w * scale, MidpointRounding.ToEven));

        GrayImage cur = gray;
        if (nh != h || nw != w)
        {
            // np.round / Python round() round halves to even, like MidpointRounding.ToEven.
            var resized = new byte[nw * nh];
            CvResize.Resize8u(gray, resized, nw, nh,
                              scale < 1.0 ? Interpolation.Area : Interpolation.Linear);
            cur = new GrayImage(nw, nh, resized);
        }

        height = ((nh + InputMultiple - 1) / InputMultiple) * InputMultiple;
        width = ((nw + InputMultiple - 1) / InputMultiple) * InputMultiple;
        var outBuf = new float[height * width];      // padding stays 0 = white
        for (int y = 0; y < nh; y++)
        {
            var row = cur.Row(y);
            int dst = y * width;
            for (int x = 0; x < nw; x++) outBuf[dst + x] = 1f - row[x] / 255f;
        }
        return outBuf;
    }

    /// <summary>
    /// Decodes one page from the LineNet output (core logit, top distance, bottom distance),
    /// row-major <c>[3, height, width]</c> at 1/2 input resolution, into <c>[N, 5]</c> line
    /// boxes in input pixels: <c>x0, y0, x1, y1, score</c>.
    /// </summary>
    /// <param name="logits">The raw network output, three contiguous <c>height*width</c> planes.</param>
    /// <param name="thresh">Core probability above which a pixel counts as line ink.</param>
    /// <param name="minArea">Minimum connected-component area to keep.</param>
    /// <param name="growY">Widen every box by this fraction of its height, each side.</param>
    /// <param name="growX">Widen every box horizontally by this fraction of its height, each side.</param>
    /// <remarks>
    /// The means are accumulated in <c>double</c>, exactly like <c>np.bincount</c> with float32
    /// weights, and the boxes are built in <c>double</c> before the final cast to float32
    /// (<c>np.asarray(boxes, np.float32)</c>).
    /// </remarks>
    public static float[] Decode(ReadOnlySpan<float> logits, int height, int width,
                                 float thresh = 0.5f, int minArea = 2,
                                 double growY = 0.0, double growX = 0.0)
    {
        int hw = height * width;
        if (logits.Length < 3 * hw)
            throw new ArgumentException($"logits holds {logits.Length} values, need {3 * hw}", nameof(logits));

        int topOff = hw, botOff = 2 * hw;

        // prob = 1 / (1 + exp(-core)), then the ink mask.
        var prob = new float[hw];
        var mask = new byte[hw];
        for (int i = 0; i < hw; i++)
        {
            float p = NpyExp.Sigmoid(logits[i]);
            prob[i] = p;
            mask[i] = p > thresh ? (byte)1 : (byte)0;
        }

        var labels = new int[hw];
        int n = CvConnectedComponents.LabelComponents4(new GrayImage(width, height, mask), labels);
        if (n <= 1) return Array.Empty<float>();          // np.zeros((0, 5))

        var stats = new int[n * CvConnectedComponents.StatsPerLabel];
        CvConnectedComponents.ComputeStats(labels, width, height, n, stats);

        // np.bincount(idx, weights=...) accumulates float32 weights in double, in raster order.
        var sumTop = new double[n];
        var sumBot = new double[n];
        var sumSc = new double[n];
        var count = new int[n];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float ys = ((float)y + 0.5f) * OutStride;      // pixel centre in input pixels
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                int lab = labels[i];
                if (lab == 0) continue;
                sumTop[lab] += ys - logits[topOff + i];
                sumBot[lab] += ys + logits[botOff + i];
                sumSc[lab] += prob[i];
                count[lab]++;
            }
        }

        var y0 = new double[n];
        var y1 = new double[n];
        var score = new double[n];
        for (int i = 0; i < n; i++)
        {
            double c = Math.Max((float)count[i], 1f);      // np.maximum(cnt, 1), in float32
            y0[i] = sumTop[i] / c;
            y1[i] = sumBot[i] / c;
            score[i] = sumSc[i] / c;
        }

        int kept = 0;
        for (int i = 1; i < n; i++)
            if (stats[i * CvConnectedComponents.StatsPerLabel + CvConnectedComponents.StatArea] >= minArea)
                kept++;

        var boxes = new float[kept * 5];
        int b = 0;
        for (int i = 1; i < n; i++)
        {
            int s = i * CvConnectedComponents.StatsPerLabel;
            if (stats[s + CvConnectedComponents.StatArea] < minArea) continue;

            double lh = Math.Max(1.0, y1[i] - y0[i]);
            double ex = Math.Min(CoreXShrink * lh, 0.5 * lh);
            double x0 = (double)(stats[s + CvConnectedComponents.StatLeft] * OutStride) - ex;
            double x1 = (double)((stats[s + CvConnectedComponents.StatLeft]
                                  + stats[s + CvConnectedComponents.StatWidth]) * OutStride) + ex;

            boxes[b++] = (float)(x0 - growX * lh);
            boxes[b++] = (float)(y0[i] - growY * lh);
            boxes[b++] = (float)(x1 + growX * lh);
            boxes[b++] = (float)(y1[i] + growY * lh);
            boxes[b++] = (float)score[i];
        }
        return boxes;
    }
}
