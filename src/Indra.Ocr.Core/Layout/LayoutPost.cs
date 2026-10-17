using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Indra.Ocr.Order;
using Indra.Ocr.Text;

namespace Indra.Ocr.Layout;

/// <summary>
/// One page block: its label (a <see cref="OrderPost.BlockClasses"/> name), its pixel bounding
/// box (rounded like the reference) and its lines newline-joined.
/// </summary>
public sealed class LayoutBlock
{
    public LayoutBlock(string label, double[] bbox, string text)
    {
        Label = label;
        Bbox = bbox;
        Text = text;
    }

    public string Label { get; }

    /// <summary>x0, y0, x1, y1 — each already rounded to one decimal by Python's <c>round</c>.</summary>
    public double[] Bbox { get; }

    public string Text { get; }
}

/// <summary>One cell of a line that holds several: its box and its text (the <c>cells</c> map).</summary>
public readonly record struct TableCell(float X0, float Y0, float X1, float Y1, string Text)
{
    /// <summary>The box as the four floats the table code reads.</summary>
    public float[] Box => [X0, Y0, X1, Y1];
}

/// <summary>
/// Page text from the ordered lines and the layout heads, ported from
/// <c>indra_ocr/layout_post.py</c>: same-block splits, per-class rendering (section headers,
/// paragraphs with hyphen/CJK joins, list groups, HTML tables), display math, and the flat block
/// list behind it.
/// </summary>
/// <remarks>
/// <para>
/// The reference works on numpy float32 arrays, and numpy's NEP 50 keeps a float32 scalar op a
/// Python float in float32 — including comparisons. So the geometric thresholds here (the band
/// midpoints, the row-merge gap, the cell-span tests) are computed in <see cref="float"/>, and
/// only the values the reference explicitly widens with <c>float(...)</c> (medians handed to
/// Python comparisons) are <see cref="double"/>. See <c>docs/120-layout-post.md</c>.
/// </para>
/// <para>
/// The class ids come from <see cref="OrderPost.BlockClasses"/> exactly as the reference's
/// <c>BLOCK_CLASSES.index(...)</c> does, and the two sorts the reference makes with
/// <c>sorted(...)</c> (rows by x, cells by (y, x), clusters by an edge) are stable here: the
/// tiebreaker is the position in the input list, which is what Python's stable sort keeps.
/// </para>
/// </remarks>
public static class LayoutPost
{
    // BLOCK_CLASSES.index(...)
    private static readonly int HeaderClass = Array.IndexOf(OrderPost.BlockClasses, "Page-Header");
    private static readonly int FooterClass = Array.IndexOf(OrderPost.BlockClasses, "Page-Footer");
    private static readonly int SectionClass = Array.IndexOf(OrderPost.BlockClasses, "Section-Header");
    private static readonly int ListClass = Array.IndexOf(OrderPost.BlockClasses, "List-Group");
    private static readonly int TableClass = Array.IndexOf(OrderPost.BlockClasses, "Table");

    // ------------------------------------------------------------------ balance_math

    /// <summary>
    /// Closes the <c>$</c> math spans the recognizer left open: a <c>$</c> opens math only when
    /// LaTeX (<c>\ _ ^ {</c>) or a closing <c>$</c> follows within 15 characters, and inside math
    /// a following plain word of 3+ letters closes the span; an open span closes at the end.
    /// </summary>
    public static string BalanceMath(string text)
    {
        if (text.IndexOf('$') < 0)
            return text;
        int[] cps = ToCodePointArray(text);
        var sb = new StringBuilder(text.Length + 4);
        bool inMath = false;
        for (int i = 0; i < cps.Length; i++)
        {
            int c = cps[i];
            if (c == '$')
            {
                if (inMath)
                {
                    inMath = false;
                }
                else
                {
                    // ahead = text[i + 1 : i + 16]; re.search(r"[\\_^{]", ahead) or "$" in ahead
                    int end = Math.Min(cps.Length, i + 16);
                    for (int j = i + 1; j < end; j++)
                    {
                        if (cps[j] is '\\' or '_' or '^' or '{' or '$')
                        {
                            inMath = true;
                            break;
                        }
                    }
                }
                AppendCodePoint(sb, c);
                continue;
            }
            if (inMath && c == ' ' && PlainWordAt(cps, i))
            {
                sb.Append('$');
                inMath = false;
            }
            AppendCodePoint(sb, c);
        }
        if (inMath)
            sb.Append('$');
        return sb.ToString();
    }

    // _PLAIN_WORD = re.compile(r" [A-Za-z]{3,}\b(?![_^{(])"), matched at a code point index.
    // The position is always a space, so `\b` only looks at what follows: a word boundary needs
    // the next code point to be a non-word one, and the lookahead vetoes `_ { ( ^`.
    private static bool PlainWordAt(ReadOnlySpan<int> cps, int i)
    {
        if (cps[i] != ' ')
            return false;
        int j = i + 1;
        int letters = 0;
        while (j < cps.Length && IsAsciiLetter(cps[j]))
        {
            j++;
            letters++;
        }
        if (letters < 3)
            return false;
        if (j < cps.Length)
        {
            int next = cps[j];
            if (IsWordCodePoint(next) || next is '_' or '{' or '(' or '^')
                return false;
        }
        return true;
    }

    // Python's `\w` for str patterns: alphanumeric (str.isalnum) or underscore.
    private static bool IsWordCodePoint(int cp) =>
        UnicodeText.IsAlphaCodePoint(cp) || IsNumericCodePoint(cp) || cp == '_';

    // str.isnumeric(): any code point with a numeric value, i.e. Nd, Nl or No.
    private static bool IsNumericCodePoint(int cp)
    {
        if (cp < 0 || cp > 0x10FFFF)
            return false;
        return UnicodeCompat.Category(cp) is
            System.Globalization.UnicodeCategory.DecimalDigitNumber
            or System.Globalization.UnicodeCategory.LetterNumber
            or System.Globalization.UnicodeCategory.OtherNumber;
    }

    private static bool IsAsciiLetter(int cp) =>
        (cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z');

    // ------------------------------------------------------------------ join_lines

    /// <summary>
    /// Joins the lines of one paragraph: <c>"tumori-"</c> + <c>"genesis"</c> becomes
    /// <c>"tumorigenesis"</c>, and a break between two CJK characters takes no space.
    /// </summary>
    public static string JoinLines(IReadOnlyList<string> texts)
    {
        string result = "";
        foreach (string raw in texts)
        {
            string t = UnicodeText.Strip(raw);
            if (t.Length == 0)
                continue;
            if (UnicodeText.CodePointCount(result) > 1 && result.EndsWith('-')
                && UnicodeText.IsAlphaCodePoint(SecondToLastCodePoint(result))
                && UnicodeText.IsAlphaCodePoint(FirstCodePoint(t))
                && UnicodeText.IsLowerCodePoint(FirstCodePoint(t)))
            {
                result = result[..^1] + t;
            }
            else if (result.Length > 0 && IsCjk(LastCodePoint(result))
                     && IsCjk(FirstCodePoint(t)))
            {
                result += t;
            }
            else
            {
                result = result.Length > 0 ? result + " " + t : t;
            }
        }
        return result;
    }

    /// <summary>Han, kana, CJK punctuation and fullwidth forms (Korean keeps its spaces).</summary>
    private static bool IsCjk(int codePoint) =>
        0x3000 <= codePoint && codePoint <= 0x30FF
        || 0x3400 <= codePoint && codePoint <= 0x9FFF
        || 0xF900 <= codePoint && codePoint <= 0xFAFF
        || 0xFF00 <= codePoint && codePoint <= 0xFFEF
        || 0x20000 <= codePoint && codePoint <= 0x2FA1F;

    // ------------------------------------------------------------------ split_blocks

    /// <summary>Runs of the reading order with a positive same-block logit between neighbours.</summary>
    public static List<List<int>> SplitBlocks(ReadOnlySpan<int> order, ReadOnlySpan<float> same, int n)
    {
        var blocks = new List<List<int>>();
        foreach (int i in order)
        {
            if (blocks.Count > 0 && same[blocks[^1][^1] * n + i] > 0)
                blocks[^1].Add(i);
            else
                blocks.Add([i]);
        }
        return blocks;
    }

    // ------------------------------------------------------------------ table rows

    /// <summary>Groups cell lines into rows by vertical centre, top to bottom, left to right.</summary>
    public static List<List<int>> Rows(ReadOnlySpan<float> boxes, int n)
    {
        var cy = new float[n];
        var height = new float[n];
        for (int i = 0; i < n; i++)
        {
            cy[i] = (boxes[i * 4 + 1] + boxes[i * 4 + 3]) / 2f;
            height[i] = boxes[i * 4 + 3] - boxes[i * 4 + 1];
        }
        // h = float(np.median(...)) is a Python float, and the comparison `abs(...) < 0.5 * h`
        // narrows it back to float32 (NEP 50: the Python float is the weak operand).
        float halfH = (float)(0.5 * Numerics.Median(height));

        var rows = new List<List<int>>();
        var scratch = new List<float>();
        foreach (int i in Numerics.ArgSort(cy))
        {
            if (rows.Count > 0)
            {
                var last = rows[^1];
                scratch.Clear();
                foreach (int k in last)
                    scratch.Add(cy[k]);
                float mean = Numerics.Mean32(ListCompat.AsSpan(scratch));
                if (MathF.Abs(cy[i] - mean) < halfH)
                {
                    last.Add(i);
                    continue;
                }
            }
            rows.Add([i]);
        }
        foreach (var r in rows)
            SortByX(r, boxes);
        return rows;
    }

    /// <summary>Joins the line rows of wrapped cells into one table row.</summary>
    public static List<List<int>> MergeWrapped(List<List<int>> rows, ReadOnlySpan<float> boxes, int n)
    {
        if (rows.Count < 3)
            return rows;
        var height = new float[n];
        for (int i = 0; i < n; i++)
            height[i] = boxes[i * 4 + 3] - boxes[i * 4 + 1];
        double h = Numerics.Median(height);

        var gaps = new double[rows.Count - 1];
        for (int i = 0; i + 1 < rows.Count; i++)
        {
            // float32 difference, then float(): boxes[nxt, 1].min() - boxes[cur, 3].max()
            float gap = MinY(boxes, rows[i + 1]) - MaxY(boxes, rows[i]);
            gaps[i] = gap;
        }
        double med = Numerics.Median(gaps);

        var result = new List<List<int>> { new(rows[0]) };
        List<int> prev = rows[0];
        for (int i = 0; i < gaps.Length; i++)
        {
            List<int> r = rows[i + 1];
            if (gaps[i] < 0.5 * med && gaps[i] < 0.6 * h && r.Count < prev.Count)
                result[^1].AddRange(r);
            else
                result.Add(new List<int>(r));
            prev = r;
        }
        return result;
    }

    // ------------------------------------------------------------------ tables

    /// <summary>
    /// An HTML table from cell lines (one text line per cell). A first row that is one line
    /// across most of the table is a title: it goes before the table as text, and the first
    /// remaining row becomes the header.
    /// </summary>
    public static string TableHtml(ReadOnlySpan<float> boxes, int count, IReadOnlyList<string> texts)
    {
        var ids = new List<int>(count);
        for (int i = 0; i < count; i++)
            ids.Add(i);
        return TableHtml(ids, boxes, texts);
    }

    private static string TableHtml(List<int> ids, ReadOnlySpan<float> boxes, IReadOnlyList<string> texts)
    {
        var rows = RowsIn(ids, boxes);
        float spanX = MaxX(boxes, ids) - MinX(boxes, ids);
        if (rows.Count > 2 && rows[0].Count == 1 && MaxLength(rows) > 1
            && boxes[ids[rows[0][0]] * 4 + 2] - boxes[ids[rows[0][0]] * 4 + 0] > 0.6f * spanX)
        {
            int title = ids[rows[0][0]];
            var keep = new List<int>(ids.Count - 1);
            foreach (int k in ids)
                if (k != title)
                    keep.Add(k);
            return texts[title] + "\n\n" + TableHtml(keep, boxes, texts);
        }

        int width = MaxLength(rows);
        var full = new List<List<int>>();
        foreach (var r in rows)
            if (r.Count == width)
                full.Add(r);
        rows = MergeWrappedIn(rows, ids, boxes);

        // Column bands from the rows with the most cells: bands is float32 (np.median of a list
        // of np.float32 scalars stays float32), and so are the midpoints and the span test.
        var bands = new float[width * 2];
        var lo = new float[full.Count];
        var hi = new float[full.Count];
        for (int c = 0; c < width; c++)
        {
            for (int r = 0; r < full.Count; r++)
            {
                int line = ids[full[r][c]];
                lo[r] = boxes[line * 4 + 0];
                hi[r] = boxes[line * 4 + 2];
            }
            bands[c * 2 + 0] = (float)Numerics.Median(lo);
            bands[c * 2 + 1] = (float)Numerics.Median(hi);
        }
        // Widen each band to the midpoints between neighbours: the midpoints are float32, and the
        // list that searchsorted turns into an array is float64.
        var edges = new double[width + 1];
        edges[0] = double.NegativeInfinity;
        edges[width] = double.PositiveInfinity;
        for (int c = 0; c + 1 < width; c++)
            edges[c + 1] = (bands[c * 2 + 1] + bands[(c + 1) * 2 + 0]) / 2f;

        var outLines = new List<string> { "<table>" };
        for (int ri = 0; ri < rows.Count; ri++)
        {
            var r = rows[ri];
            string tag = ri == 0 && rows.Count > 1 ? "th" : "td";
            SortByYX(r, ids, boxes);
            var cells = new Dictionary<int, (int Span, string Text)>();
            foreach (int k in r)
            {
                int line = ids[k];
                float x0 = boxes[line * 4 + 0];
                float x1 = boxes[line * 4 + 2];
                int first = Numerics.SearchSorted(edges, x0, true) - 1;
                int last = Numerics.SearchSorted(edges, x1, true) - 1;
                // A cell spans every band it covers by more than half of the band.
                var span = new List<int>();
                for (int c = Math.Max(first, 0); c <= Math.Min(last, width - 1); c++)
                {
                    float covered = MathF.Min(x1, bands[c * 2 + 1]) - MathF.Max(x0, bands[c * 2 + 0]);
                    float half = 0.5f * (bands[c * 2 + 1] - bands[c * 2 + 0]);
                    if (covered > half)
                        span.Add(c);
                }
                if (span.Count == 0)
                    span.Add(Math.Min(Math.Max(first, 0), width - 1));
                int c0 = span[0];
                bool had = cells.TryGetValue(c0, out var prev);
                cells[c0] = (
                    had ? Math.Max(prev.Span, span.Count) : span.Count,
                    had ? prev.Text + " " + texts[line] : texts[line]);
            }
            var tds = new List<string>();
            for (int c = 0; c < width;)
            {
                if (cells.TryGetValue(c, out var cell))
                {
                    string attr = cell.Span > 1 ? $" colspan=\"{cell.Span}\"" : "";
                    tds.Add($"<{tag}{attr}>{HtmlEscape(cell.Text)}</{tag}>");
                    c += cell.Span;
                }
                else
                {
                    tds.Add($"<{tag}></{tag}>");
                    c += 1;
                }
            }
            outLines.Add("<tr>" + string.Concat(tds) + "</tr>");
        }
        outLines.Add("</table>");
        return string.Join("\n", outLines);
    }

    /// <summary>
    /// Whether the table heads are usable: the lines of one cell are stacked (they overlap
    /// horizontally). When they join side-by-side lines in more than 10% of the cells the heads
    /// failed on this table and the geometric assembly is used instead. <paramref name="tab"/> is
    /// the <c>[3, n, n]</c> head slice; only the cell head (<c>tab[2]</c>) is read.
    /// </summary>
    public static bool HeadsPlausible(ReadOnlySpan<float> boxes, int n, ReadOnlySpan<float> tab)
    {
        var cells = OrderPost.Cluster(Slice(tab, 2, n), n);
        int bad = 0;
        foreach (var cell in cells)
        {
            if (cell.Count < 2)
                continue;
            bool overlaps = false;
            foreach (int i in cell)
            {
                foreach (int j in cell)
                {
                    float overlap = MathF.Min(boxes[i * 4 + 2], boxes[j * 4 + 2])
                                    - MathF.Max(boxes[i * 4 + 0], boxes[j * 4 + 0]);
                    if (overlap < 0)
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (overlaps)
                    break;
            }
            if (overlaps)
                bad++;
        }
        return bad <= 0.1 * cells.Count;
    }

    /// <summary>
    /// An HTML table from the OrderNet table heads. <paramref name="tab"/> is
    /// <c>[3, n, n]</c> — same row / same column / same cell logits for the lines of one table
    /// block.
    /// </summary>
    public static string TableHtmlHeads(ReadOnlySpan<float> boxes, int n, IReadOnlyList<string> texts,
                                        ReadOnlySpan<float> tab)
    {
        var cells = OrderPost.Cluster(Slice(tab, 2, n), n);
        int m = cells.Count;

        var cb = new float[m * 4];
        for (int i = 0; i < m; i++)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue;
            float x1 = float.MinValue, y1 = float.MinValue;
            foreach (int k in cells[i])
            {
                x0 = MathF.Min(x0, boxes[k * 4 + 0]);
                y0 = MathF.Min(y0, boxes[k * 4 + 1]);
                x1 = MathF.Max(x1, boxes[k * 4 + 2]);
                y1 = MathF.Max(y1, boxes[k * 4 + 3]);
            }
            cb[i * 4 + 0] = x0;
            cb[i * 4 + 1] = y0;
            cb[i * 4 + 2] = x1;
            cb[i * 4 + 3] = y1;
        }
        var ctext = new string[m];
        for (int i = 0; i < m; i++)
        {
            var order = new List<int>(cells[i]);
            var keyY = new float[order.Count];
            var keyX = new float[order.Count];
            for (int k = 0; k < order.Count; k++)
            {
                keyY[k] = boxes[order[k] * 4 + 1];
                keyX[k] = boxes[order[k] * 4 + 0];
            }
            StableSortByYX(order, keyY, keyX);
            var parts = new List<string>(order.Count);
            foreach (int k in order)
                parts.Add(texts[k]);
            ctext[i] = string.Join(" ", parts);
        }

        var rows = OrderPost.Cluster(CellLogits(tab, 0, n, cells, m), m);
        rows = SortedGroups(rows, MinKeys(cb, rows, 1));
        var cols = OrderPost.Cluster(CellLogits(tab, 1, n, cells, m), m);
        cols = SortedGroups(cols, MinKeys(cb, cols, 0));

        var rowOf = new int[m];
        var colOf = new int[m];
        for (int r = 0; r < rows.Count; r++)
            foreach (int c in rows[r])
                rowOf[c] = r;
        for (int k = 0; k < cols.Count; k++)
            foreach (int c in cols[k])
                colOf[c] = k;

        var centers = new double[cols.Count];
        for (int i = 0; i < cols.Count; i++)
        {
            var g = cols[i];
            var vals = new float[g.Count];
            for (int t = 0; t < g.Count; t++)
                vals[t] = (cb[g[t] * 4 + 0] + cb[g[t] * 4 + 2]) / 2f;
            centers[i] = Numerics.Median(vals);
        }
        var heights = new float[n];
        for (int i = 0; i < n; i++)
            heights[i] = boxes[i * 4 + 3] - boxes[i * 4 + 1];
        float h = (float)Numerics.Median(heights);

        var grid = new Dictionary<(int Row, int Col), (int Span, string Text)>();
        for (int c = 0; c < m; c++)
        {
            int c0 = colOf[c];
            int span = 1;
            // cb[c, 0] - h <= centers[c0 + span] <= cb[c, 2] + h, all narrowed to float32.
            while (c0 + span < cols.Count)
            {
                float centre = (float)centers[c0 + span];
                if (cb[c * 4 + 0] - h <= centre && centre <= cb[c * 4 + 2] + h)
                    span++;
                else
                    break;
            }
            var key = (rowOf[c], c0);
            if (grid.TryGetValue(key, out var prev))
                grid[key] = (Math.Max(prev.Span, span), prev.Text + " " + ctext[c]);
            else
                grid[key] = (span, ctext[c]);
        }

        var outLines = new List<string> { "<table>" };
        for (int r = 0; r < rows.Count; r++)
        {
            string tag = r == 0 && rows.Count > 1 ? "th" : "td";
            var tds = new List<string>();
            for (int c = 0; c < cols.Count;)
            {
                if (grid.TryGetValue((r, c), out var cell))
                {
                    string attr = cell.Span > 1 ? $" colspan=\"{cell.Span}\"" : "";
                    tds.Add($"<{tag}{attr}>{HtmlEscape(cell.Text)}</{tag}>");
                    c += cell.Span;
                }
                else
                {
                    tds.Add($"<{tag}></{tag}>");
                    c += 1;
                }
            }
            outLines.Add("<tr>" + string.Concat(tds) + "</tr>");
        }
        outLines.Add("</table>");
        return string.Join("\n", outLines);
    }

    /// <summary>
    /// <c>out[a, b] = out[b, a] = tab[head][np.ix_(cells[a], cells[b])].mean()</c> — the mean
    /// logit between two cells, in float32 and in numpy's pairwise order (rows outer).
    /// </summary>
    private static float[] CellLogits(ReadOnlySpan<float> tab, int head, int n,
                                      List<List<int>> cells, int m)
    {
        var outv = new float[m * m];
        var buffer = new List<float>();
        for (int a = 0; a < m; a++)
        {
            for (int b = a + 1; b < m; b++)
            {
                buffer.Clear();
                foreach (int i in cells[a])
                    foreach (int j in cells[b])
                        buffer.Add(tab[head * n * n + i * n + j]);
                float mean = Numerics.Mean32(ListCompat.AsSpan(buffer));
                outv[a * m + b] = mean;
                outv[b * m + a] = mean;
            }
        }
        return outv;
    }

    /// <summary>One <c>[n, n]</c> head of a <c>[3, n, n]</c> table-logits buffer.</summary>
    private static float[] Slice(ReadOnlySpan<float> tab, int head, int n)
    {
        var outv = new float[n * n];
        tab.Slice(head * n * n, n * n).CopyTo(outv);
        return outv;
    }

    // ------------------------------------------------------------------ page blocks / markdown

    /// <summary>
    /// The layout blocks behind <see cref="PageMarkdown"/>, in reading order.
    /// </summary>
    public static List<LayoutBlock> PageBlocks(
        ReadOnlySpan<float> boxes, int n, IReadOnlyList<string> texts, ReadOnlySpan<int> order,
        ReadOnlySpan<float> clsLogits, ReadOnlySpan<float> same)
    {
        var blocks = new List<LayoutBlock>();
        foreach (var blk in SplitBlocks(order, same, n))
        {
            string label = OrderPost.BlockClasses[ArgMaxColumnSum(clsLogits, blk)];
            var bbox = new double[4];
            bbox[0] = Numerics.RoundHalfEven(MinX(boxes, blk), 1);
            bbox[1] = Numerics.RoundHalfEven(MinY(boxes, blk), 1);
            bbox[2] = Numerics.RoundHalfEven(MaxX(boxes, blk), 1);
            bbox[3] = Numerics.RoundHalfEven(MaxY(boxes, blk), 1);
            var parts = new List<string>(blk.Count);
            foreach (int i in blk)
                parts.Add(texts[i]);
            blocks.Add(new LayoutBlock(label, bbox, string.Join("\n", parts)));
        }
        return blocks;
    }

    /// <summary>
    /// Markdown for one page. <paramref name="boxes"/> is <c>[n, 4]</c>,
    /// <paramref name="clsLogits"/> <c>[n, C]</c> and <paramref name="same"/> <c>[n, n]</c>;
    /// <paramref name="cells"/> maps a line to its table cells when the line holds several, and
    /// <paramref name="tab"/> is the <c>[3, n, n]</c> table-head logits (empty when absent).
    /// </summary>
    public static string PageMarkdown(
        ReadOnlySpan<float> boxes, int n, IReadOnlyList<string> texts, ReadOnlySpan<int> order,
        ReadOnlySpan<float> clsLogits, ReadOnlySpan<float> same,
        bool keepFurniture = false,
        IReadOnlyDictionary<int, IReadOnlyList<TableCell>>? cells = null,
        ReadOnlySpan<float> tab = default)
    {
        var parts = new List<string>();
        foreach (var blk in SplitBlocks(order, same, n))
        {
            int cls = ArgMaxColumnSum(clsLogits, blk);
            var textsB = new List<string>(blk.Count);
            foreach (int i in blk)
                textsB.Add(texts[i]);

            if ((cls == HeaderClass || cls == FooterClass) && !keepFurniture)
                continue;
            if (AllWholeMath(textsB))
            {
                var equations = new List<string>(textsB.Count);
                foreach (string t in textsB)
                {
                    string s = UnicodeText.Strip(t);
                    equations.Add("$$" + s[1..^1] + "$$");
                }
                parts.Add(string.Join("\n", equations));
                continue;
            }
            if (cls == SectionClass)
            {
                parts.Add("## " + JoinLines(textsB));
            }
            else if (cls == TableClass && blk.Count > 1 && tab.Length > 0)
            {
                var blockBoxes = SliceBoxes(boxes, blk);
                var blockTab = SliceTab(tab, blk, n);
                if (HeadsPlausible(blockBoxes, blk.Count, blockTab))
                    parts.Add(TableHtmlHeads(blockBoxes, blk.Count, textsB, blockTab));
                else
                    parts.Add(GeometricTable(boxes, texts, blk, cells));
            }
            else if (cls == TableClass && blk.Count > 1)
            {
                parts.Add(GeometricTable(boxes, texts, blk, cells));
            }
            else if (cls == ListClass)
            {
                var items = new List<List<string>>();
                foreach (string t in textsB)
                {
                    if (items.Count == 0 || IsItem(t))
                        items.Add([t]);
                    else
                        items[^1].Add(t);
                }
                var joined = new List<string>(items.Count);
                foreach (var item in items)
                    joined.Add(JoinLines(item));
                parts.Add(string.Join("\n", joined));
            }
            else
            {
                parts.Add(JoinLines(textsB));
            }
        }
        var kept = new List<string>();
        foreach (string p in parts)
            if (p.Length > 0)
                kept.Add(p);
        return string.Join("\n\n", kept);
    }

    // The geometric table path: each line's own box, or the cells the line holds when it has
    // several (the reference's `cells` map).
    private static string GeometricTable(
        ReadOnlySpan<float> boxes, IReadOnlyList<string> texts, List<int> blk,
        IReadOnlyDictionary<int, IReadOnlyList<TableCell>>? cells)
    {
        var cellBoxes = new List<float>();
        var cellTexts = new List<string>();
        foreach (int i in blk)
        {
            IReadOnlyList<TableCell>? lineCells = null;
            if (cells is not null && cells.TryGetValue(i, out var found) && found.Count > 0)
                lineCells = found;
            if (lineCells is null)
            {
                cellBoxes.Add(boxes[i * 4 + 0]);
                cellBoxes.Add(boxes[i * 4 + 1]);
                cellBoxes.Add(boxes[i * 4 + 2]);
                cellBoxes.Add(boxes[i * 4 + 3]);
                cellTexts.Add(texts[i]);
            }
            else
            {
                foreach (var cell in lineCells)
                {
                    cellBoxes.AddRange(cell.Box);
                    cellTexts.Add(cell.Text);
                }
            }
        }
        return TableHtml(ListCompat.AsSpan(cellBoxes), cellTexts.Count, cellTexts);
    }

    // _WHOLE_MATH = re.compile(r"^\$[^$]+\$$") on t.strip()
    private static bool AllWholeMath(List<string> texts)
    {
        foreach (string t in texts)
        {
            string s = UnicodeText.Strip(t);
            if (s.Length < 3 || s[0] != '$' || s[^1] != '$' || s.IndexOf('$', 1, s.Length - 2) >= 0)
                return false;
        }
        return true;
    }

    // _ITEM = re.compile(r"^\s*([•\-–▪◦*·→]|\(?\d{1,3}[.)]|\(?[a-zA-Z][.)])(\s|$)")
    // Internal so the tests can check the predicate on its own (tests/test_textlayer.py does).
    internal static bool IsItem(string text)
    {
        int[] cps = ToCodePointArray(text);
        int i = 0;
        while (i < cps.Length && UnicodeText.IsSpaceCodePoint(cps[i]))
            i++;

        int marker;
        if (i < cps.Length
            && cps[i] is 0x2022 or 0x2D or 0x2013 or 0x25AA or 0x25E6 or 0x2A or 0xB7 or 0x2192)
        {
            marker = i + 1;
        }
        else if (TryNumberedMarker(cps, i, out marker))
        {
        }
        else if (TryLetteredMarker(cps, i, out marker))
        {
        }
        else
        {
            return false;
        }
        return marker >= cps.Length || UnicodeText.IsSpaceCodePoint(cps[marker]);
    }

    // \(?\d{1,3}[.)] : an optional "(", one to three decimal digits, then "." or ")". Greedy up
    // to three digits, exactly like the regex (which backtracks but cannot succeed past three).
    private static bool TryNumberedMarker(int[] cps, int i, out int marker)
    {
        int j = i;
        if (j < cps.Length && cps[j] == '(')
            j++;
        int digits = 0;
        while (j + digits < cps.Length && digits < 3 && UnicodeText.IsDigitCodePoint(cps[j + digits]))
            digits++;
        if (digits >= 1 && j + digits < cps.Length && cps[j + digits] is '.' or ')')
        {
            marker = j + digits + 1;
            return true;
        }
        marker = 0;
        return false;
    }

    // \(?[a-zA-Z][.)]
    private static bool TryLetteredMarker(int[] cps, int i, out int marker)
    {
        int j = i;
        if (j < cps.Length && cps[j] == '(')
            j++;
        if (j + 1 < cps.Length && IsAsciiLetter(cps[j]) && cps[j + 1] is '.' or ')')
        {
            marker = j + 2;
            return true;
        }
        marker = 0;
        return false;
    }

    /// <summary><c>cls_logits[blk].sum(0).argmax()</c>: the strided float32 column sum.</summary>
    private static int ArgMaxColumnSum(ReadOnlySpan<float> clsLogits, List<int> blk)
    {
        int classes = OrderPost.BlockClasses.Length;
        int best = 0;
        float bestValue = float.NegativeInfinity;
        for (int c = 0; c < classes; c++)
        {
            float sum = 0f;
            foreach (int i in blk)
                sum += clsLogits[i * classes + c];
            if (sum > bestValue)
            {
                bestValue = sum;
                best = c;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ small helpers

    /// <summary>The <c>[k, 4]</c> boxes of a block, as a fresh array (the reference slices).</summary>
    private static float[] SliceBoxes(ReadOnlySpan<float> boxes, List<int> blk)
    {
        var outv = new float[blk.Count * 4];
        for (int i = 0; i < blk.Count; i++)
            boxes.Slice(blk[i] * 4, 4).CopyTo(outv.AsSpan(i * 4));
        return outv;
    }

    /// <summary>The <c>[3, k, k]</c> table logits of a block.</summary>
    private static float[] SliceTab(ReadOnlySpan<float> tab, List<int> blk, int n)
    {
        int k = blk.Count;
        var outv = new float[3 * k * k];
        for (int head = 0; head < 3; head++)
            for (int i = 0; i < k; i++)
                for (int j = 0; j < k; j++)
                    outv[head * k * k + i * k + j] = tab[head * n * n + blk[i] * n + blk[j]];
        return outv;
    }

    private static List<List<int>> RowsIn(List<int> ids, ReadOnlySpan<float> boxes) =>
        Rows(SliceBoxes(boxes, ids), ids.Count);

    private static List<List<int>> MergeWrappedIn(List<List<int>> rows, List<int> ids,
                                                 ReadOnlySpan<float> boxes) =>
        MergeWrapped(rows, SliceBoxes(boxes, ids), ids.Count);

    private static int MaxLength(List<List<int>> rows)
    {
        int m = 0;
        foreach (var r in rows)
            m = Math.Max(m, r.Count);
        return m;
    }

    private static float MinX(ReadOnlySpan<float> boxes, List<int> ids)
    {
        float m = float.PositiveInfinity;
        foreach (int i in ids)
            m = MathF.Min(m, boxes[i * 4 + 0]);
        return m;
    }

    private static float MaxX(ReadOnlySpan<float> boxes, List<int> ids)
    {
        float m = float.NegativeInfinity;
        foreach (int i in ids)
            m = MathF.Max(m, boxes[i * 4 + 2]);
        return m;
    }

    private static float MinY(ReadOnlySpan<float> boxes, List<int> ids)
    {
        float m = float.PositiveInfinity;
        foreach (int i in ids)
            m = MathF.Min(m, boxes[i * 4 + 1]);
        return m;
    }

    private static float MaxY(ReadOnlySpan<float> boxes, List<int> ids)
    {
        float m = float.NegativeInfinity;
        foreach (int i in ids)
            m = MathF.Max(m, boxes[i * 4 + 3]);
        return m;
    }

    /// <summary>The per-cluster minimum of one box coordinate (the head-grid sort keys).</summary>
    private static float[] MinKeys(float[] cb, List<List<int>> groups, int coord)
    {
        var keys = new float[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            float m = float.PositiveInfinity;
            foreach (int c in groups[g])
                m = MathF.Min(m, cb[c * 4 + coord]);
            keys[g] = m;
        }
        return keys;
    }

    /// <summary>Stable sort by x0 (ties keep the position in the list).</summary>
    private static void SortByX(List<int> items, ReadOnlySpan<float> boxes)
    {
        var keys = new float[items.Count];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = boxes[items[i] * 4 + 0];
        ApplyOrder(items, SortedPositions(keys));
    }

    /// <summary>Stable sort by (y0, x0) (ties keep the position in the list).</summary>
    private static void StableSortByYX(List<int> items, float[] keyY, float[] keyX)
    {
        var idx = new int[items.Count];
        for (int i = 0; i < idx.Length; i++)
            idx[i] = i;
        Array.Sort(idx, (a, b) =>
        {
            int c = keyY[a].CompareTo(keyY[b]);
            if (c != 0)
                return c;
            c = keyX[a].CompareTo(keyX[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        ApplyOrder(items, idx);
    }

    /// <summary>Stable sort by (y0, x0) of the boxes, in place (ties keep the position).</summary>
    private static void SortByYX(List<int> items, List<int> ids, ReadOnlySpan<float> boxes)
    {
        var keyY = new float[items.Count];
        var keyX = new float[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            keyY[i] = boxes[ids[items[i]] * 4 + 1];
            keyX[i] = boxes[ids[items[i]] * 4 + 0];
        }
        StableSortByYX(items, keyY, keyX);
    }

    /// <summary>
    /// Python's <c>sorted(seq, key=...)</c> translated to positions: equal keys keep the position
    /// they had in the input (numpy/Array.Sort are both unstable, so the tiebreak is needed).
    /// </summary>
    private static int[] SortedPositions(float[] keys)
    {
        var idx = new int[keys.Length];
        for (int i = 0; i < idx.Length; i++)
            idx[i] = i;
        Array.Sort(idx, (a, b) =>
        {
            int c = keys[a].CompareTo(keys[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return idx;
    }

    /// <summary>Reorders a list according to a permutation of its positions.</summary>
    private static void ApplyOrder(List<int> items, int[] order)
    {
        var copy = items.ToArray();
        for (int i = 0; i < items.Count; i++)
            items[i] = copy[order[i]];
    }

    /// <summary>A stable sort of clusters by a per-cluster key.</summary>
    private static List<List<int>> SortedGroups(List<List<int>> groups, float[] keys)
    {
        var idx = SortedPositions(keys);
        var outv = new List<List<int>>(groups.Count);
        foreach (int i in idx)
            outv.Add(groups[i]);
        return outv;
    }

    // Python's html.escape(text, quote=False)
    private static string HtmlEscape(string s)
    {
        if (s.IndexOf('&') < 0 && s.IndexOf('<') < 0 && s.IndexOf('>') < 0)
            return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static int[] ToCodePointArray(string s)
    {
        var list = new List<int>(s.Length);
        foreach (int cp in UnicodeText.CodePointValues(s))
            list.Add(cp);
        return list.ToArray();
    }

    private static int FirstCodePoint(string s)
    {
        foreach (int cp in UnicodeText.CodePointValues(s))
            return cp;
        return -1;
    }

    private static int LastCodePoint(string s)
    {
        int last = -1;
        foreach (int cp in UnicodeText.CodePointValues(s))
            last = cp;
        return last;
    }

    private static int SecondToLastCodePoint(string s)
    {
        int prev = -1, last = -1;
        foreach (int cp in UnicodeText.CodePointValues(s))
        {
            prev = last;
            last = cp;
        }
        return prev;
    }

    private static void AppendCodePoint(StringBuilder sb, int codePoint)
    {
        if (codePoint is >= 0xD800 and <= 0xDFFF)
            sb.Append((char)codePoint);       // a lone surrogate has no ConvertFromUtf32
        else
            sb.Append(char.ConvertFromUtf32(codePoint));
    }
}
