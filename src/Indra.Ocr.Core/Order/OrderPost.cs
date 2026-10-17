using System;
using System.Collections.Generic;
using Indra.Ocr.Text;

namespace Indra.Ocr.Order;

/// <summary>
/// The torch-free half of the reading-order stage, ported from
/// <c>indra_ocr/order_post.py</c>: line features, pairwise-order decoding, the two geometry
/// baselines (top-down, XY-cut), average-linkage block clustering and the layout-head decoder.
/// </summary>
/// <remarks>
/// <para>
/// The training/eval-only helpers of the reference module (<c>kendall_tau</c>,
/// <c>successor_acc</c>, <c>label_order</c>, <c>block_class</c>) are not part of the pipeline and
/// are not ported, like <c>det_post.encode_targets</c>; <see cref="BlockClasses"/> and
/// <see cref="GridClassIds"/> (which <c>decode_blocks_order</c> needs) are here.
/// </para>
/// <para>
/// Float handling follows the same rules as the rest of the port (see <c>docs/110-order-post.md</c>):
/// <see cref="NpyLog"/> reproduces numpy's vectorised float32 <c>log</c> exactly, the float32
/// sums use numpy's portable pairwise algorithm, and <c>np.logaddexp</c>'s float32 scalar loop —
/// which calls the CRT's <c>expf</c>/<c>log1pf</c>, <em>not</em> the vectorised
/// <c>np.exp</c>/<c>np.log</c> kernels — is reproduced with <c>MathF.Exp</c> (bit-identical to
/// the CRT's <c>expf</c> here) and a correctly rounded <c>log1pf</c>.
/// </para>
/// </remarks>
public static class OrderPost
{
    /// <summary>Rows of the geometry block of the feature matrix.</summary>
    public const int GeomCount = 10;

    /// <summary>Rows of the text block of the feature matrix.</summary>
    public const int TextCount = 6;

    /// <summary>Width of the feature matrix (<c>N_FEATS</c>).</summary>
    public const int FeatureCount = GeomCount + TextCount;

    /// <summary>Chandra block labels the layout heads predict (<c>BLOCK_CLASSES</c>).</summary>
    public static readonly string[] BlockClasses =
    [
        "Text", "Section-Header", "Page-Header", "Page-Footer", "List-Group",
        "Table", "Caption", "Footnote", "Table-Of-Contents", "Form",
    ];

    /// <summary>Block labels whose lines are read row by row (<c>GRID_LABELS</c> ids).</summary>
    public static readonly int[] GridClassIds = [5, 9, 8];   // Table, Form, Table-Of-Contents

    /// <summary>numpy's <c>NPY_LOGE2f</c>.</summary>
    private const float Ln2F = 0.6931471805599453f;

    /// <summary>The bullet/number markers a list item may start with.</summary>
    private static bool IsBulletMarker(int codePoint) => codePoint
        is 0x2022 /* • */ or 0x002D /* - */ or 0x2013 /* – */ or 0x25AA /* ▪ */
        or 0x25E6 /* ◦ */ or 0x002A /* * */ or 0x00B7 /* · */ or 0x2192 /* → */;

    // ---------------------------------------------------------------- line features

    /// <summary>
    /// <c>line_features</c>: <c>[N, 4]</c> boxes (x0, y0, x1, y1) to <c>[N, 16]</c> float32
    /// features, row-major.
    /// </summary>
    public static float[] LineFeatures(
        ReadOnlySpan<float> boxes, int count, double pageWidth, double pageHeight,
        IReadOnlyList<string>? texts = null)
    {
        var f = new float[count * FeatureCount];
        if (count == 0)
            return f;

        float pageW = (float)pageWidth;
        float pageH = (float)pageHeight;

        var w = new float[count];
        var h = new float[count];
        for (int i = 0; i < count; i++)
        {
            w[i] = MathF.Max(boxes[i * 4 + 2] - boxes[i * 4 + 0], 1e-3f);
            h[i] = MathF.Max(boxes[i * 4 + 3] - boxes[i * 4 + 1], 1e-3f);
        }

        // np.median keeps the float32 reduction, `float(...)` then widens it for the two
        // features that are computed in Python doubles.
        double medH = Numerics.Median(h);
        float heightRatio20 = (float)(medH / pageHeight * 20.0);

        for (int i = 0; i < count; i++)
        {
            float x0 = boxes[i * 4 + 0];
            float y0 = boxes[i * 4 + 1];
            float x1 = boxes[i * 4 + 2];
            float y1 = boxes[i * 4 + 3];
            int o = i * FeatureCount;
            f[o + 0] = x0 / pageW;
            f[o + 1] = y0 / pageH;
            f[o + 2] = x1 / pageW;
            f[o + 3] = y1 / pageH;
            f[o + 4] = (x0 + x1) / (2f * pageW);
            f[o + 5] = (y0 + y1) / (2f * pageH);
            f[o + 6] = w[i] / pageW;
            f[o + 7] = NpyLog.Log(h[i] / (float)medH);
            f[o + 8] = NpyLog.Log(w[i] / h[i]) / 4f;
            f[o + 9] = heightRatio20;
        }

        if (texts is not null)
        {
            int m = Math.Min(count, texts.Count);
            for (int i = 0; i < m; i++)
            {
                string t = UnicodeText.Strip(texts[i]);
                if (t.Length == 0)
                    continue;
                int len = UnicodeText.CodePointCount(t);
                int digits = 0;
                foreach (int cp in UnicodeText.CodePointValues(t))
                    if (UnicodeText.IsDigitCodePoint(cp))
                        digits++;
                int o = i * FeatureCount + GeomCount;
                f[o + 0] = (float)(Numerics.Log1p(len) / 5.0);
                f[o + 1] = (float)(digits / (double)len);
                f[o + 2] = IsListItem(t, len) ? 1f : 0f;
                f[o + 3] = t.EndsWith('-') ? 1f : 0f;
                f[o + 4] = ".!?:;".Contains(t[^1]) ? 1f : 0f;
                f[o + 5] = FirstCodePointIsUpper(t) ? 1f : 0f;
            }
        }
        return f;
    }

    /// <summary>
    /// <c>t[0] in "•-–▪◦*·→" or (t[:3].rstrip(".)").isdigit() and len(t) &gt; 3)</c>.
    /// </summary>
    private static bool IsListItem(string t, int len)
    {
        int first = FirstCodePoint(t);
        if (IsBulletMarker(first))
            return true;
        return len > 3 && UnicodeText.IsDigit(FirstCodePoints(t, 3).TrimEnd('.', ')'));
    }

    private static bool FirstCodePointIsUpper(string t)
    {
        foreach (int cp in UnicodeText.CodePointValues(t))
            return UnicodeText.IsUpperCodePoint(cp);
        return false;
    }

    private static int FirstCodePoint(string t)
    {
        foreach (int cp in UnicodeText.CodePointValues(t))
            return cp;
        return -1;
    }

    /// <summary>The first <paramref name="limit"/> code points of a string (Python's <c>t[:n]</c>).</summary>
    private static string FirstCodePoints(string t, int limit)
    {
        int n = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (n == limit)
                return t[..i];
            if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
                i++;
            n++;
        }
        return t;
    }

    // ---------------------------------------------------------------- decode_order

    /// <summary>
    /// <c>decode_order</c>: order from a pairwise logit matrix <c>[N, N]</c>
    /// (<c>a[i, j] &gt; 0</c>: i before j). Starts from the row-sum ranking and does insertion
    /// moves that most increase the summed log-probability of the pairwise relations.
    /// </summary>
    public static int[] DecodeOrder(ReadOnlySpan<float> a, int n, int sweeps = 4)
    {
        var order = new int[n];
        if (n <= 1)
        {
            for (int i = 0; i < n; i++)
                order[i] = i;
            return order;
        }

        // lp = -np.logaddexp(0.0, -a): log P(i before j).
        var lp = new float[n * n];
        for (int i = 0; i < lp.Length; i++)
            lp[i] = LogSigmoid(a[i]);

        // order = np.argsort(-lp.sum(1) + lp.sum(0)). The row sums are contiguous (numpy's
        // pairwise tree); the column sums are a strided axis-0 reduce, which numpy accumulates
        // sequentially per column.
        var column = new float[n];
        var keys = new float[n];
        for (int i = 0; i < n; i++)
        {
            float rows = Numerics.Sum32(lp.AsSpan(i * n, n));
            for (int j = 0; j < n; j++)
                column[j] = lp[j * n + i];
            float cols = 0f;
            for (int j = 0; j < n; j++)
                cols += column[j];
            keys[i] = -rows + cols;
        }
        order = Numerics.ArgSort(keys);

        var rest = new int[n - 1];
        var gains = new double[n];
        for (int sweep = 0; sweep < sweeps; sweep++)
        {
            bool improved = false;
            foreach (int x in (int[])order.Clone())
            {
                // rest = order[order != x]; cur = the position x currently sits at.
                int m = 0;
                int cur = 0;
                for (int i = 0; i < n; i++)
                {
                    if (order[i] == x)
                    {
                        cur = i;
                        continue;
                    }
                    rest[m++] = order[i];
                }

                // gains = concat([0.0], cumsum(before)) + concat(cumsum(after[::-1])[::-1], [0.0]);
                // the cumsums stay float32, the outer concatenation is float64 (the 0.0 literal).
                for (int i = 0; i < n; i++)
                    gains[i] = 0.0;
                float run = 0f;
                for (int k = 0; k < m; k++)
                {
                    run += lp[rest[k] * n + x];            // cumsum(before)
                    gains[k + 1] += run;
                }
                run = 0f;
                for (int k = m - 1; k >= 0; k--)
                {
                    run += lp[x * n + rest[k]];            // cumsum(after[::-1])[::-1]
                    gains[k] += run;
                }

                int best = 0;
                for (int k = 1; k < n; k++)
                    if (gains[k] > gains[best])
                        best = k;
                if (gains[best] > gains[cur] + 1e-6)
                {
                    var moved = new int[n];
                    for (int i = 0; i < best; i++)
                        moved[i] = rest[i];
                    moved[best] = x;
                    for (int i = best; i < m; i++)
                        moved[i + 1] = rest[i];
                    order = moved;
                    improved = true;
                }
            }
            if (!improved)
                break;
        }
        return order;
    }

    /// <summary>
    /// numpy's <b>float32</b> <c>logaddexp(0.0, -a)</c> — the float32 scalar loop, which uses the
    /// CRT's <c>expf</c>/<c>log1pf</c> rather than the vectorised kernels <c>np.exp</c>/
    /// <c>np.log</c> use.  <c>MathF.Exp</c> is bit-identical to this machine's <c>expf</c>
    /// (probe: 0/60000 inputs differ); the CRT's <c>log1pf</c> is *not* correctly rounded
    /// (~11% of arguments differ by 1 ulp), so <c>lp</c> can differ from the reference in the
    /// last ulp — see <c>docs/110-order-post.md</c> for the measured effect on the decoded order.
    /// </summary>
    private static float LogSigmoid(float a)
    {
        if (a == 0f)
            return -(0f + Ln2F);                        // np.logaddexp: x == y -> x + ln 2
        if (a > 0f)
            return -(0f + Log1PF(MathF.Exp(-a)));       // x = 0.0 > y = -a
        return -(-a + Log1PF(MathF.Exp(a)));            // x = 0.0 < y = -a
    }

    /// <summary>The correctly rounded float32 <c>log1p</c> (C's <c>log1pf</c> to 1 ulp).</summary>
    private static float Log1PF(float x) => (float)Numerics.Log1p(x);

    // ---------------------------------------------------------------- geometry baselines

    /// <summary>
    /// <c>order_top_down</c>: rows top to bottom (centres within half a line height share a row),
    /// left to right.
    /// </summary>
    public static int[] OrderTopDown(ReadOnlySpan<float> boxes, int n)
    {
        var order = new int[n];
        if (n == 0)
            return order;

        var centreY = new float[n];
        var height = new float[n];
        for (int i = 0; i < n; i++)
        {
            centreY[i] = (boxes[i * 4 + 1] + boxes[i * 4 + 3]) / 2f;
            height[i] = boxes[i * 4 + 3] - boxes[i * 4 + 1];
        }
        double half = Math.Max(Numerics.Median(height) * 0.5, 1e-3);

        // sorted(range(n), key=lambda i: (round(cy[i] / max(h * 0.5, 1e-3)), boxes[i, 0]))
        var keys = new (long Row, float X)[n];
        for (int i = 0; i < n; i++)
        {
            // The quotient is float32 (h * 0.5 is a numpy float32, 1e-3 a weak Python float);
            // Python's round() is half-to-even on the exact value.
            float q = centreY[i] / (float)half;
            keys[i] = ((long)Math.Round((double)q, MidpointRounding.ToEven), boxes[i * 4 + 0]);
        }
        var idx = new int[n];
        for (int i = 0; i < n; i++)
            idx[i] = i;
        // Python's sorted() is stable; comparing the index as a tiebreaker gives the same order.
        Array.Sort(idx, (x, y) =>
        {
            int c = keys[x].Row.CompareTo(keys[y].Row);
            if (c != 0)
                return c;
            c = keys[x].X.CompareTo(keys[y].X);
            return c != 0 ? c : x.CompareTo(y);
        });
        return idx;
    }

    /// <summary>
    /// <c>order_xycut</c>: recursive XY-cut at the widest whitespace gap on either axis. A
    /// horizontal cut (between rows) puts the upper part first, a vertical one the left part;
    /// without a gap it falls back to <see cref="OrderTopDown"/>.
    /// </summary>
    public static int[] OrderXyCut(ReadOnlySpan<float> boxes, int n)
    {
        var ids = new List<int>(n);
        for (int i = 0; i < n; i++)
            ids.Add(i);
        return Rec(boxes, ids).ToArray();
    }

    private static List<int> Rec(ReadOnlySpan<float> boxes, List<int> ids)
    {
        if (ids.Count <= 1)
            return new List<int>(ids);

        var b = new float[ids.Count * 4];
        for (int i = 0; i < ids.Count; i++)
            for (int c = 0; c < 4; c++)
                b[i * 4 + c] = boxes[ids[i] * 4 + c];

        var (gy, ky, oy) = BestCut(b, ids.Count, 1);
        var (gx, kx, ox) = BestCut(b, ids.Count, 0);
        if (ky < 0 && kx < 0)
        {
            var topDown = OrderTopDown(b, ids.Count);
            var fallback = new List<int>(ids.Count);
            foreach (int i in topDown)
                fallback.Add(ids[i]);
            return fallback;
        }

        (float Gap, int K, int[] Order) cut = gy >= gx ? (gy, ky, oy) : (gx, kx, ox);
        int k = cut.K;
        int[] o = cut.Order;
        var left = new List<int>(k);
        for (int i = 0; i < k; i++)
            left.Add(ids[o[i]]);
        var right = new List<int>(ids.Count - k);
        for (int i = k; i < o.Length; i++)
            right.Add(ids[o[i]]);
        var result = Rec(boxes, left);
        result.AddRange(Rec(boxes, right));
        return result;
    }

    /// <summary>
    /// <c>best_cut</c>: the widest gap between the sorted starts and the running end, on the y
    /// axis (<paramref name="axis"/> 1) or the x axis (0).
    /// </summary>
    private static (float Gap, int K, int[] Order) BestCut(ReadOnlySpan<float> b, int n, int axis)
    {
        int lo = axis == 1 ? 1 : 0;
        int hi = axis == 1 ? 3 : 2;
        var starts = new float[n];
        for (int i = 0; i < n; i++)
            starts[i] = b[i * 4 + lo];
        int[] order = Numerics.ArgSort(starts);

        float end = b[order[0] * 4 + hi];
        float bestGap = 0f;
        int bestK = -1;
        for (int k = 1; k < n; k++)
        {
            float gap = starts[order[k]] - end;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestK = k;
            }
            end = MathF.Max(end, b[order[k] * 4 + hi]);
        }
        return (bestGap, bestK, order);
    }

    // ---------------------------------------------------------------- block clustering

    /// <summary>
    /// <c>_cluster</c>: average-linkage clustering of the pairwise "same group" logits. Merges
    /// the two groups with the highest mean logit while that mean is positive, so one wrong pair
    /// cannot join two groups that the other pairs keep apart. Member lists are sorted.
    /// </summary>
    public static List<List<int>> Cluster(ReadOnlySpan<float> logits, int n)
    {
        var blocks = new List<List<int>>();
        if (n == 0)
            return blocks;

        var s = new double[n * n];            // S = logits.astype(float64).copy()
        for (int i = 0; i < s.Length; i++)
            s[i] = logits[i];
        for (int i = 0; i < n; i++)
            s[i * n + i] = 0.0;
        var m = new double[n * n];            // mean logit between groups, -inf off the diagonal
        Array.Copy(s, m, s.Length);
        for (int i = 0; i < n; i++)
            m[i * n + i] = double.NegativeInfinity;
        var size = new double[n];
        size.AsSpan().Fill(1.0);
        var alive = new bool[n];
        alive.AsSpan().Fill(true);
        var members = new List<int>[n];
        for (int i = 0; i < n; i++)
            members[i] = [i];
        var best = new int[n];
        var bestValue = new double[n];
        for (int i = 0; i < n; i++)
        {
            best[i] = ArgMaxRow(m, n, i);
            bestValue[i] = m[i * n + best[i]];
        }

        var row = new double[n];
        for (int iteration = 0; iteration < n - 1; iteration++)
        {
            int a = Numerics.ArgMax(bestValue);
            int b = best[a];
            if (bestValue[a] <= 0.0)
                break;
            if (a > b)
                (a, b) = (b, a);

            for (int j = 0; j < n; j++)
                s[a * n + j] += s[b * n + j];
            for (int i = 0; i < n; i++)
                s[i * n + a] += s[i * n + b];
            s[a * n + a] = 0.0;
            size[a] += size[b];
            alive[b] = false;
            members[a].AddRange(members[b]);

            for (int j = 0; j < n; j++)
                row[j] = s[a * n + j] / (size[a] * size[j]);
            for (int j = 0; j < n; j++)
                if (!alive[j])
                    row[j] = double.NegativeInfinity;
            row[a] = double.NegativeInfinity;

            for (int j = 0; j < n; j++)
            {
                m[a * n + j] = row[j];
                m[j * n + a] = row[j];
            }
            for (int j = 0; j < n; j++)
            {
                m[b * n + j] = double.NegativeInfinity;
                m[j * n + b] = double.NegativeInfinity;
            }
            bestValue[b] = double.NegativeInfinity;
            best[a] = Numerics.ArgMax(row);
            bestValue[a] = row[best[a]];

            // Other rows whose best partner was a or b: (j, a) changed and (j, b) is gone.
            for (int j = 0; j < n; j++)
            {
                if (j == a || !alive[j] || (best[j] != a && best[j] != b))
                    continue;
                best[j] = ArgMaxRow(m, n, j);
                bestValue[j] = m[j * n + best[j]];
            }
            for (int j = 0; j < n; j++)
            {
                if (j == a || !alive[j] || !(row[j] > bestValue[j]))
                    continue;
                best[j] = a;
                bestValue[j] = row[j];
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (!alive[i])
                continue;
            members[i].Sort();
            blocks.Add(members[i]);
        }
        return blocks;
    }

    /// <summary>numpy's <c>argmax</c> over row <paramref name="i"/> of <paramref name="m"/> (first maximum).</summary>
    private static int ArgMaxRow(double[] m, int n, int i)
    {
        int best = 0;
        for (int j = 1; j < n; j++)
            if (m[i * n + j] > m[i * n + best])
                best = j;
        return best;
    }

    // ---------------------------------------------------------------- decode_blocks_order

    /// <summary>
    /// <c>decode_blocks_order</c>: blocks first (from <see cref="Cluster"/>), then lines inside
    /// them by XY-cut — or row by row when the block's layout class is a grid
    /// (<see cref="GridClassIds"/>) — and the blocks by the mean pairwise order logit between
    /// them. <paramref name="cls"/> is the layout head's <c>[n, 10]</c> class logit block.
    /// </summary>
    public static int[] DecodeBlocksOrder(
        ReadOnlySpan<float> a, ReadOnlySpan<float> same, ReadOnlySpan<float> boxes, int n,
        ReadOnlySpan<float> cls)
    {
        if (cls.Length < n * BlockClasses.Length)
            throw new ArgumentException("cls must be [n, 10]", nameof(cls));
        var order = new int[n];
        if (n <= 1)
        {
            for (int i = 0; i < n; i++)
                order[i] = i;
            return order;
        }

        var blocks = Cluster(same, n);
        var inner = new List<int[]>(blocks.Count);
        foreach (var block in blocks)
        {
            var bx = new float[block.Count * 4];
            for (int k = 0; k < block.Count; k++)
                for (int c = 0; c < 4; c++)
                    bx[k * 4 + c] = boxes[block[k] * 4 + c];
            int[] local = IsGridBlock(cls, block)
                ? OrderTopDown(bx, block.Count)
                : OrderXyCut(bx, block.Count);
            var mapped = new int[block.Count];
            for (int k = 0; k < block.Count; k++)
                mapped[k] = block[local[k]];
            inner.Add(mapped);
        }

        // Mean order logit between blocks: G a G^T with G the membership matrix scaled by
        // 1 / block size; np.triu(..., 1) then keeps only the strictly upper triangle, and
        // up - up.T makes that antisymmetric: A[k, l] = M[k, l] for k < l, -M[l, k] for k > l.
        int blockCount = blocks.Count;
        var mean = new double[blockCount * blockCount];
        var stage = new double[blockCount * n];      // G @ a64
        for (int k = 0; k < blockCount; k++)
        {
            double scale = 1.0 / blocks[k].Count;
            foreach (int i in blocks[k])
                for (int j = 0; j < n; j++)
                    stage[k * n + j] += a[i * n + j];
            for (int j = 0; j < n; j++)
                stage[k * n + j] *= scale;
        }
        for (int k = 0; k < blockCount; k++)
        {
            for (int l = 0; l < blockCount; l++)
            {
                double sum = 0.0;
                foreach (int j in blocks[l])
                    sum += stage[k * n + j];
                mean[k * blockCount + l] = sum / blocks[l].Count;
            }
        }

        var antisymmetric = new float[blockCount * blockCount];
        for (int k = 0; k < blockCount; k++)
        {
            for (int l = 0; l < blockCount; l++)
            {
                float v = k < l ? (float)mean[k * blockCount + l]
                    : k > l ? (float)-mean[l * blockCount + k]
                    : 0f;
                antisymmetric[k * blockCount + l] = v;
            }
        }

        var blockOrder = DecodeOrder(antisymmetric, blockCount);
        var result = new int[n];
        int at = 0;
        foreach (int k in blockOrder)
            foreach (int i in inner[k])
                result[at++] = i;
        return result;
    }

    /// <summary><c>int(cls[b].sum(0).argmax()) in grid</c>.</summary>
    private static bool IsGridBlock(ReadOnlySpan<float> cls, List<int> block)
    {
        Span<float> sums = stackalloc float[BlockClasses.Length];
        foreach (int i in block)
            for (int c = 0; c < BlockClasses.Length; c++)
                sums[c] += cls[i * BlockClasses.Length + c];
        int best = 0;
        for (int c = 1; c < sums.Length; c++)
            if (sums[c] > sums[best])
                best = c;
        foreach (int g in GridClassIds)
            if (g == best)
                return true;
        return false;
    }
}
