using System;
using System.Collections.Generic;

namespace Indra.Ocr.Imaging;

/// <summary>
/// 4-connectivity connected components, matching <c>cv::connectedComponentsWithStats</c> with
/// <c>connectivity=4</c> (see <c>docs/040-opencv-primitives.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two details of the OpenCV contract matter to the caller:
/// <list type="bullet">
///   <item>the returned count <b>includes the background</b>, which is label 0, so foreground
///     labels run 1..count-1;</item>
///   <item>labels are assigned in <b>raster order of first appearance</b>. The detector's box
///     decoder iterates <c>for i in range(1, n)</c> over the stats rows, so the order is part
///     of the observable output (it decides which lines come out in which order).</item>
/// </list>
/// </para>
/// <para>
/// Any non-zero pixel is foreground, exactly like OpenCV. The first pass is the classic
/// union-find scan; a second pass canonicalises the roots into the raster-order labels above.
/// </para>
/// </remarks>
public static class CvConnectedComponents
{
    /// <summary>Index of the <c>left</c> stat in a stats row (OpenCV's <c>CC_STAT_LEFT</c>).</summary>
    public const int StatLeft = 0;

    /// <summary>Index of the <c>top</c> stat (OpenCV's <c>CC_STAT_TOP</c>).</summary>
    public const int StatTop = 1;

    /// <summary>Index of the <c>width</c> stat (OpenCV's <c>CC_STAT_WIDTH</c>).</summary>
    public const int StatWidth = 2;

    /// <summary>Index of the <c>height</c> stat (OpenCV's <c>CC_STAT_HEIGHT</c>).</summary>
    public const int StatHeight = 3;

    /// <summary>Index of the pixel <c>area</c> stat (OpenCV's <c>CC_STAT_AREA</c>).</summary>
    public const int StatArea = 4;

    /// <summary>Number of stats per label.</summary>
    public const int StatsPerLabel = 5;

    /// <summary>Labels the components; returns the label count <b>including</b> the background.</summary>
    public static int LabelComponents4(GrayImage mask, Span<int> labels)
    {
        int width = mask.Width, height = mask.Height;
        if (labels.Length < width * height)
            throw new ArgumentException($"labels needs {width * height} entries", nameof(labels));

        // parent[0] is the background sentinel; parent[k] is the union-find root of label k.
        var parent = new List<int>(64) { 0 };
        int next = 0;

        for (int y = 0; y < height; y++)
        {
            var row = mask.Row(y);
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                if (row[x] == 0)
                {
                    labels[rowStart + x] = 0;
                    continue;
                }
                int left = x > 0 ? labels[rowStart + x - 1] : 0;
                int up = y > 0 ? labels[rowStart - width + x] : 0;
                if (left == 0 && up == 0)
                {
                    parent.Add(++next);
                    labels[rowStart + x] = next;
                }
                else if (left == 0 || up == 0)
                {
                    labels[rowStart + x] = left == 0 ? up : left;
                }
                else
                {
                    int m = Math.Min(left, up);
                    labels[rowStart + x] = m;
                    int ra = Find(parent, left), rb = Find(parent, up);
                    if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                }
            }
        }

        // Canonicalise: number the roots by the raster position where they first appear.
        var seen = new int[next + 1];
        int count = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            int v = labels[i];
            if (v == 0) continue;
            int root = Find(parent, v);
            int label = seen[root];
            if (label == 0) seen[root] = label = ++count;
            labels[i] = label;
        }
        return count + 1; // the background is label 0
    }

    /// <summary>Eight-connectivity variant used by the detector's picture channel.</summary>
    public static int LabelComponents8(GrayImage mask, Span<int> labels)
    {
        int width = mask.Width, height = mask.Height;
        if (labels.Length < width * height)
            throw new ArgumentException($"labels needs {width * height} entries", nameof(labels));
        var parent = new List<int>(64) { 0 };
        int next = 0;
        var near = new int[4];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = mask.Row(y);
            int start = y * width;
            for (int x = 0; x < width; x++)
            {
                if (row[x] == 0) { labels[start + x] = 0; continue; }
                int n = 0;
                if (x > 0 && labels[start + x - 1] != 0) near[n++] = labels[start + x - 1];
                if (y > 0)
                {
                    if (x > 0 && labels[start - width + x - 1] != 0) near[n++] = labels[start - width + x - 1];
                    if (labels[start - width + x] != 0) near[n++] = labels[start - width + x];
                    if (x + 1 < width && labels[start - width + x + 1] != 0) near[n++] = labels[start - width + x + 1];
                }
                if (n == 0) { parent.Add(++next); labels[start + x] = next; continue; }
                int first = near[0]; labels[start + x] = first;
                for (int i = 1; i < n; i++)
                {
                    int a = Find(parent, first), b = Find(parent, near[i]);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            }
        }
        var seen = new int[next + 1]; int count = 0;
        for (int i = 0; i < width * height; i++)
        {
            if (labels[i] == 0) continue;
            int root = Find(parent, labels[i]);
            if (seen[root] == 0) seen[root] = ++count;
            labels[i] = seen[root];
        }
        return count + 1;
    }

    /// <summary>
    /// Fills <paramref name="stats"/> with <see cref="StatsPerLabel"/> ints per label
    /// (<c>left, top, width, height, area</c>), row 0 being the background.
    /// </summary>
    /// <remarks>
    /// The background row is left zeroed. OpenCV happens to leave scratch values there (the
    /// first stats row doubles as the algorithm's working set, so it holds e.g.
    /// <c>[0, 0, width, height, background_area]</c> or
    /// <c>[-1, int.MaxValue, 0, 0, 0]</c> depending on the mask). Nothing reads it: the box
    /// decoder iterates labels <c>1 .. count-1</c>.
    /// </remarks>
    public static void ComputeStats(ReadOnlySpan<int> labels, int width, int height, int count,
                                    Span<int> stats)
    {
        int needed = count * StatsPerLabel;
        if (stats.Length < needed) throw new ArgumentException($"stats needs {needed} entries", nameof(stats));
        if (labels.Length < width * height) throw new ArgumentException("labels is too small", nameof(labels));
        stats.Slice(0, needed).Clear();

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                int label = labels[rowStart + x];
                if (label == 0) continue;
                int o = label * StatsPerLabel;
                if (stats[o + StatArea] == 0)
                {
                    stats[o + StatLeft] = x;
                    stats[o + StatTop] = y;
                    stats[o + StatWidth] = x;
                    stats[o + StatHeight] = y;
                }
                else
                {
                    if (x < stats[o + StatLeft]) stats[o + StatLeft] = x;
                    if (y < stats[o + StatTop]) stats[o + StatTop] = y;
                    if (x > stats[o + StatWidth]) stats[o + StatWidth] = x;
                    if (y > stats[o + StatHeight]) stats[o + StatHeight] = y;
                }
                stats[o + StatArea]++;
            }
        }

        // width/height are stored as the inclusive max while scanning; make them extents.
        for (int label = 1; label < count; label++)
        {
            int o = label * StatsPerLabel;
            if (stats[o + StatArea] == 0) continue;
            stats[o + StatWidth] = stats[o + StatWidth] - stats[o + StatLeft] + 1;
            stats[o + StatHeight] = stats[o + StatHeight] - stats[o + StatTop] + 1;
        }
    }

    private static int Find(List<int> parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]]; // path halving
            x = parent[x];
        }
        return x;
    }
}
