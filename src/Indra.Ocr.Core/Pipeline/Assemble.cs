using System;
using System.Collections.Generic;
using System.Diagnostics;
using Indra.Ocr.Layout;
using Indra.Ocr.Order;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

namespace Indra.Ocr.Pipeline;

/// <summary>One line of an assembled page: its box in image pixels, its text and its confidence.</summary>
/// <remarks>Ported from <c>pipeline.Line</c>. The box is already rounded to one decimal (Python's
/// <c>round</c>) and the confidence to four.</remarks>
public sealed class OcrLine
{
    public OcrLine(IReadOnlyList<double> box, string text, double confidence)
    {
        Box = box;
        Text = text;
        Confidence = confidence;
    }

    /// <summary><c>x0, y0, x1, y1</c> in image pixels, each rounded to one decimal.</summary>
    public IReadOnlyList<double> Box { get; }

    public string Text { get; }

    public double Confidence { get; }

    public void Deconstruct(out IReadOnlyList<double> box, out string text, out double confidence)
    {
        box = Box;
        text = Text;
        confidence = Confidence;
    }
}

/// <summary>
/// One page of a document, ported from <c>pipeline.Page</c>: the lines in reading order, the
/// timings of the steps that produced it, and — with an order model that has layout heads — the
/// Markdown and the layout blocks.
/// </summary>
public sealed class OcrPage
{
    public OcrPage(
        IReadOnlyList<OcrLine> lines,
        IReadOnlyDictionary<string, double>? timings = null,
        string? markdown = null,
        string source = "ocr",
        string note = "",
        IReadOnlyList<LayoutBlock>? blocks = null,
        int rotation = 0,
        double skew = 0.0,
        bool vertical = false)
    {
        Lines = lines;
        Timings = timings ?? new Dictionary<string, double>();
        Markdown = markdown;
        Source = source;
        Note = note;
        Blocks = blocks ?? Array.Empty<LayoutBlock>();
        Rotation = rotation;
        Skew = skew;
        Vertical = vertical;
    }

    /// <summary>The lines in reading order.</summary>
    public IReadOnlyList<OcrLine> Lines { get; }

    /// <summary>The per-step timings the pipeline filled in.</summary>
    public IReadOnlyDictionary<string, double> Timings { get; }

    /// <summary>The page's Markdown, when an order model with layout heads produced it.</summary>
    public string? Markdown { get; }

    /// <summary><c>ocr</c> | <c>text</c> (PDF text layer) | <c>hybrid</c> | <c>error</c>.</summary>
    public string Source { get; }

    /// <summary>Why the page took this path.</summary>
    public string Note { get; }

    /// <summary>The layout blocks in reading order.</summary>
    public IReadOnlyList<LayoutBlock> Blocks { get; }

    /// <summary>Degrees counter-clockwise the input was turned; the boxes are upright.</summary>
    public int Rotation { get; }

    /// <summary>Degrees of small tilt undone after the turn.</summary>
    public double Skew { get; }

    /// <summary>Whether the page is set in vertical columns.</summary>
    public bool Vertical { get; }

    /// <summary>The page's text: the lines' text newline-joined.</summary>
    public string Text
    {
        get
        {
            var parts = new string[Lines.Count];
            for (int i = 0; i < Lines.Count; i++)
                parts[i] = Lines[i].Text;
            return string.Join("\n", parts);
        }
    }
}

/// <summary>
/// One piece of a split line: its box (float32, as the reference keeps it before the next
/// <c>np.array(..., np.float32)</c>), its text and its tokens — the reference's
/// <c>(bx, tx, tk)</c>.
/// </summary>
public readonly record struct LineSegment(float[] Box, string Text, IReadOnlyList<RecToken> Tokens);

/// <summary>
/// Port of <c>pipeline.OrtOCR.assemble</c> (with <c>gutter_cuts</c>, <c>split_line</c>,
/// <c>_decode</c>, <c>_is_space</c>) and of the two text-layer order checks it uses from
/// <c>textlayer.py</c> (<c>order_backtracks</c>, <c>pdf_order_coherent</c>).
/// </summary>
/// <remarks>
/// The flow is the reference's: cut each detected line at wide gaps (<see cref="SplitGap"/>) and at
/// column gutters (<see cref="GutterCuts"/>), run the reading order, and — when the order model
/// carries table heads and any line is classified a table — cut the table lines at the smaller
/// <see cref="CellGap"/> and run the order model again. With <c>content_order</c> and text-layer
/// tokens on every line, a coherent PDF content order wins over the model's order.
/// <para>
/// The class only holds the model and the flags the reference reads from <c>self</c>; the driver
/// that owns the whole pipeline (<c>OrtOCR</c>) is a later step.
/// </para>
/// </remarks>
public sealed class Assembler
{
    /// <summary>SPLIT_GAP: cut a detected line at a wider gap (the label rule outside tables).</summary>
    public const double SplitGap = 3.0;

    /// <summary>CELL_GAP: cut a table line into cells at a wider gap (the label rule in tables).</summary>
    public const double CellGap = 1.8;

    /// <summary>GUTTER_GAP: cut at a smaller gap when other lines' edges line up with it.</summary>
    public const double GutterGap = 1.6;

    /// <summary>GUTTER_EDGES: how many other line edges must line up (within half a line height).</summary>
    public const int GutterEdges = 3;

    /// <summary>MIN_MODEL_AGREEMENT: the model must agree with this fraction of consecutive pairs.</summary>
    public const double MinModelAgreement = 0.85;

    private readonly IOrderModel? _order;
    private readonly bool _tableHeads;
    private readonly Vocab? _vocab;
    private readonly bool _furniture;
    private readonly int? _spaceId;
    private bool _pageRtl;

    /// <param name="order">The order model, or <c>null</c> when the model set has none.</param>
    /// <param name="tableHeads">The reference's <c>table_heads</c> flag.</param>
    /// <param name="vocab">The recognizer vocabulary (the space id for <see cref="IsSpace"/>).</param>
    /// <param name="furniture">The reference's <c>furniture</c> flag: keep headers and footers.</param>
    public Assembler(IOrderModel? order = null, bool tableHeads = false, Vocab? vocab = null,
                     bool furniture = false)
    {
        _order = order;
        _tableHeads = tableHeads;
        _vocab = vocab;
        _furniture = furniture;
        _spaceId = vocab is not null && vocab.Index.TryGetValue(" ", out int id) ? id : null;
    }

    // ------------------------------------------------------------------ assemble

    /// <summary>
    /// Lines in, the assembled page out. <paramref name="boxes"/> is <c>[n, 4]</c> float32,
    /// <paramref name="rec"/> the recognizer's <c>(text, confidence, tokens)</c> per line.
    /// </summary>
    /// <param name="timings">
    /// The timings dictionary the pipeline already filled in; <c>order_ms</c> and <c>total_ms</c>
    /// are written to it (the value of <c>order_ms</c> is wall clock, as in the reference).
    /// </param>
    public OcrPage Run(double pageWidth, double pageHeight, ReadOnlySpan<float> boxes, int n,
                       IReadOnlyList<RecognizedLine> rec, IDictionary<string, double>? timings = null,
                       bool contentOrder = false, ReadOnlySpan<float> pictures = default,
                       bool vertical = false)
    {
        var clock = Stopwatch.StartNew();

        int rtl = 0, ltr = 0;
        foreach (RecognizedLine line in rec)
            foreach (int cp in UnicodeText.CodePointValues(line.Text))
            {
                if (!UnicodeText.IsAlphaCodePoint(cp)) continue;
                if (RtlText.IsRtl(cp)) rtl++; else ltr++;
            }
        _pageRtl = rtl > ltr;

        // Text-layer labels split a line at wide gaps; do the same (SPLIT_GAP).
        var bx = new List<float>(n * 4);
        var rc = new List<Rec>();
        int limit = Math.Min(n, rec.Count);
        if (limit > 0)
        {
            var cuts = GutterCuts(boxes, n, rec);
            for (int i = 0; i < limit; i++)
            {
                var line = rec[i];
                foreach (var seg in SplitLine(boxes.Slice(i * 4, 4), line.Tokens, SplitGap, cuts[i]))
                {
                    bx.AddRange(seg.Box);
                    rc.Add(new Rec(seg.Text, line.Confidence, seg.Tokens));
                }
            }
        }
        var curBoxes = bx.ToArray();
        int m = rc.Count;
        TidyLines(ref curBoxes, rc, _pageRtl);
        m = rc.Count;
        var texts = Texts(rc, m);
        float[] geometry = _pageRtl ? Mirror(curBoxes, m, (float)pageWidth) : curBoxes;
        var ro = ReadingOrder.Run(_order, geometry, m, texts, pageWidth, pageHeight, _tableHeads);
        if (vertical && ro.Layout is not null)
        {
            List<List<int>> grouped = OrderPost.XyCutBlocks(curBoxes, m);
            int[] verticalOrder = grouped.SelectMany(v => v).ToArray();
            var same = Enumerable.Repeat(-1f, m * m).ToArray();
            foreach (List<int> group in grouped)
                foreach (int a in group) foreach (int b in group) same[a * m + b] = 1f;
            float[] cls = ro.Layout.Cls.ToArray();
            foreach (int c in OrderPost.GridClassIds)
            {
                for (int i = 0; i < m; i++) cls[i * OrderPost.BlockClasses.Length + c] = -1e4f;
            }
            ro = new ReadingOrderResult(verticalOrder, new LayoutHeads(cls, same, null), ro.Logits);
        }

        if (ro.Layout is not null && ro.Layout.Table is not null)
        {
            // The table heads learned one line per cell, but the detector can join cells:
            // split the table lines into cells, then run the order model again.
            var isTable = new bool[m];
            bool anyTable = false;
            for (int i = 0; i < m; i++)
            {
                isTable[i] = ArgMaxRow(ro.Layout.Cls, i) == TableClassIndex;
                anyTable |= isTable[i];
            }
            if (anyTable)
            {
                var bx2 = new List<float>(curBoxes.Length);
                var rc2 = new List<Rec>(m);
                for (int i = 0; i < m; i++)
                {
                    if (isTable[i])
                    {
                        foreach (var seg in SplitLine(curBoxes.AsSpan(i * 4, 4), rc[i].Toks, CellGap))
                        {
                            bx2.AddRange(seg.Box);
                            rc2.Add(new Rec(seg.Text, rc[i].Conf, seg.Tokens));
                        }
                    }
                    else
                    {
                        bx2.AddRange(curBoxes.AsSpan(i * 4, 4));
                        rc2.Add(rc[i]);
                    }
                }
                if (rc2.Count != m)
                {
                    curBoxes = bx2.ToArray();
                    rc = rc2;
                    m = rc.Count;
                    texts = Texts(rc, m);
                    ro = ReadingOrder.Run(_order, curBoxes, m, texts, pageWidth, pageHeight,
                                          _tableHeads);
                }
            }
        }

        var order = ro.Order;
        string note = "order=model";
        if (contentOrder && m > 1)
        {
            var keys = new int?[m];
            bool all = true;
            for (int i = 0; i < m; i++)
            {
                int? best = null;
                foreach (var tk in rc[i].Toks)
                    if (tk.ContentIndex is int ci && (best is null || ci < best.Value))
                        best = ci;
                keys[i] = best;
                if (best is null)
                    all = false;
            }
            if (all)
            {
                int[] pdf = SortedByKey(keys, m);
                float[]? sub = null;
                if (ro.Logits is not null)
                {
                    sub = new float[m * m];
                    for (int a = 0; a < m; a++)
                        for (int b = 0; b < m; b++)
                            sub[a * m + b] = ro.Logits[pdf[a] * m + pdf[b]];
                }
                var subBoxes = new float[m * 4];
                for (int a = 0; a < m; a++)
                    Array.Copy(curBoxes, pdf[a] * 4, subBoxes, a * 4, 4);
                if (PdfOrderCoherent(subBoxes, m, sub))
                {
                    order = pdf;
                    note = "order=pdf";
                }
            }
        }

        var timingsDict = timings ?? new Dictionary<string, double>();
        timingsDict["order_ms"] = clock.Elapsed.TotalMilliseconds;
        double total = 0.0;
        foreach (var kv in timingsDict)
            if (kv.Key != "total_ms")
                total += kv.Value;
        timingsDict["total_ms"] = total;
        // The page keeps the pipeline's own dictionary when it is a Dictionary (the usual case).
        var timingsView = timingsDict as IReadOnlyDictionary<string, double>
                          ?? new Dictionary<string, double>(timingsDict);

        var lines = new List<OcrLine>(order.Length);
        foreach (int i in order)
        {
            lines.Add(new OcrLine(
                new[]
                {
                    Numerics.RoundHalfEven(curBoxes[i * 4 + 0], 1),
                    Numerics.RoundHalfEven(curBoxes[i * 4 + 1], 1),
                    Numerics.RoundHalfEven(curBoxes[i * 4 + 2], 1),
                    Numerics.RoundHalfEven(curBoxes[i * 4 + 3], 1),
                },
                rc[i].Text,
                Numerics.RoundHalfEven(rc[i].Conf, 4)));
        }

        string? md = null;
        var blocks = new List<LayoutBlock>();
        if (ro.Layout is not null)
        {
            blocks = LayoutPost.PageBlocks(curBoxes, m, texts, order, ro.Layout.Cls, ro.Layout.Same);
            AddPictures(blocks, pictures);
            // Inside tables, labels split cells at smaller gaps (CELL_GAP).
            var cells = new Dictionary<int, IReadOnlyList<TableCell>>(m);
            for (int i = 0; i < m; i++)
            {
                var list = new List<TableCell>();
                foreach (var seg in SplitLine(curBoxes.AsSpan(i * 4, 4), rc[i].Toks, CellGap))
                    list.Add(new TableCell(seg.Box[0], seg.Box[1], seg.Box[2], seg.Box[3],
                                           seg.Text));
                cells[i] = list;
            }
            md = LayoutPost.PageMarkdown(curBoxes, m, texts, order, ro.Layout.Cls, ro.Layout.Same,
                                          _furniture, cells,
                                          ro.Layout.Table is null ? default : ro.Layout.Table,
                                          pictures);
        }
        else if (m < 2)
        {
            md = string.Join("\n", texts);
        }

        return new OcrPage(lines, timingsView, md, "ocr", note, blocks);
    }

    private static float[] Mirror(float[] boxes, int n, float width)
    {
        var output = boxes.ToArray();
        for (int i = 0; i < n; i++)
        {
            output[i * 4] = width - boxes[i * 4 + 2];
            output[i * 4 + 2] = width - boxes[i * 4];
        }
        return output;
    }

    private void TidyLines(ref float[] boxes, List<Rec> rec, bool rtl)
    {
        int n = rec.Count;
        var keep = Enumerable.Repeat(true, n).ToArray();
        for (int i = 0; i < n; i++)
        {
            string core = rec[i].Text.Replace(" ", "");
            if (core.Length == 0 || core.Any(c => !".·…‥_-–—=".Contains(c))) continue;
            double h = boxes[i * 4 + 3] - boxes[i * 4 + 1];
            double w = boxes[i * 4 + 2] - boxes[i * 4];
            if (UnicodeText.CodePointCount(core) >= 3 || w >= 3 * h || !core.Any(c => "-–—".Contains(c)))
                keep[i] = false;
        }
        for (int i = 0; i < n; i++)
        {
            string marker = UnicodeText.Strip(rec[i].Text);
            if (!keep[i] || !IsMarker(marker)) continue;
            double hi = boxes[i * 4 + 3] - boxes[i * 4 + 1];
            int best = -1; double bestGap = double.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (i == j || !keep[j] || UnicodeText.Strip(rec[j].Text).Length == 0) continue;
                double hj = boxes[j * 4 + 3] - boxes[j * 4 + 1];
                double overlap = Math.Min(boxes[i * 4 + 3], boxes[j * 4 + 3]) -
                                 Math.Max(boxes[i * 4 + 1], boxes[j * 4 + 1]);
                if (overlap < 0.5 * Math.Min(hi, hj)) continue;
                double gap = rtl ? boxes[i * 4] - boxes[j * 4 + 2]
                                 : boxes[j * 4] - boxes[i * 4 + 2];
                if (gap >= -0.3 * hi && gap <= 2.5 * Math.Max(hi, hj) && gap < bestGap)
                { best = j; bestGap = gap; }
            }
            if (best < 0) continue;
            rec[best] = new Rec(marker + " " + UnicodeText.Strip(rec[best].Text), rec[best].Conf,
                                rec[best].Toks);
            boxes[best * 4] = Math.Min(boxes[i * 4], boxes[best * 4]);
            boxes[best * 4 + 1] = Math.Min(boxes[i * 4 + 1], boxes[best * 4 + 1]);
            boxes[best * 4 + 2] = Math.Max(boxes[i * 4 + 2], boxes[best * 4 + 2]);
            boxes[best * 4 + 3] = Math.Max(boxes[i * 4 + 3], boxes[best * 4 + 3]);
            keep[i] = false;
        }
        var outputBoxes = new List<float>(); var outputRec = new List<Rec>();
        for (int i = 0; i < n; i++) if (keep[i])
        { outputBoxes.AddRange(boxes.AsSpan(i * 4, 4)); outputRec.Add(rec[i]); }
        boxes = outputBoxes.ToArray(); rec.Clear(); rec.AddRange(outputRec);
    }

    private static bool IsMarker(string text)
    {
        if (UnicodeText.CodePointCount(text) == 1 && "•◦▪▫●○■□‣⁃∙*".Contains(text)) return true;
        string t = text;
        if (t.StartsWith('(') && t.EndsWith(')')) t = t[1..^1];
        else if (t.EndsWith('.') || t.EndsWith(')')) t = t[..^1]; else return false;
        if (t.Length is < 1 or > 5) return false;
        return t.All(char.IsDigit) || (t.Length == 1 &&
               (t[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) ||
               t.All(c => "ivxlcIVXLC".Contains(c));
    }

    private static void AddPictures(List<LayoutBlock> blocks, ReadOnlySpan<float> pictures)
    {
        for (int k = 0; k < pictures.Length / 4; k++)
        {
            int o = k * 4;
            double cy = (pictures[o + 1] + pictures[o + 3]) / 2;
            int at = blocks.Count;
            for (int i = 0; i < blocks.Count; i++)
                if ((blocks[i].Bbox[1] + blocks[i].Bbox[3]) / 2 > cy &&
                    Math.Min(pictures[o + 2], blocks[i].Bbox[2]) >
                    Math.Max(pictures[o], blocks[i].Bbox[0])) { at = i; break; }
            blocks.Insert(at, new LayoutBlock("Picture",
                new double[] { pictures[o], pictures[o + 1], pictures[o + 2], pictures[o + 3] }, ""));
        }
    }

    // ------------------------------------------------------------------ gutter cuts

    /// <summary>
    /// Per line, the x positions of gaps that sit on a column gutter: a gap over
    /// <see cref="GutterGap"/> line heights where at least <see cref="GutterEdges"/> other lines
    /// end at its left side or start at its right side.
    /// </summary>
    public List<double[]> GutterCuts(ReadOnlySpan<float> boxes, int n,
                                     IReadOnlyList<RecognizedLine> rec)
    {
        var cuts = new List<double[]>(n);
        int limit = Math.Min(n, rec.Count);
        var xs = new List<double>();
        for (int i = 0; i < limit; i++)
        {
            double h = Math.Max(1.0, (double)(boxes[i * 4 + 3] - boxes[i * 4 + 1]));
            xs.Clear();
            foreach (var tk in rec[i].Tokens)
                if (!IsSpace(tk))
                    xs.Add(tk.X);
            var mine = new List<double>();
            for (int k = 0; k + 1 < xs.Count; k++)
            {
                double a = xs[k];
                double c = xs[k + 1];
                if (c - a <= GutterGap * h)
                    continue;
                double gl = a + 0.35 * h;   // gap edges
                double gr = c - 0.35 * h;
                // numpy: float32 array minus a (weak) Python float stays float32, and so does
                // the comparison against 0.5*h.
                float band = (float)(0.5 * h);
                int near = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i)
                        continue;
                    bool left = MathF.Abs(boxes[j * 4 + 2] - (float)gl) < band;
                    bool right = MathF.Abs(boxes[j * 4 + 0] - (float)gr) < band;
                    if (left || right)
                        near++;
                }
                if (near >= GutterEdges)
                    mine.Add((a + c) / 2);
            }
            cuts.Add(mine.ToArray());
        }
        return cuts;
    }

    // ------------------------------------------------------------------ splitting

    /// <summary>
    /// Cuts a line where two neighbouring characters are more than <paramref name="gap"/> line
    /// heights apart (the detector merged two cells or columns). Returns
    /// <c>[(box, text, tokens)]</c>; one item when nothing is cut.
    /// </summary>
    /// <param name="at">
    /// Gutter cut positions (<see cref="GutterCuts"/>): a cut is also made where one falls between
    /// two neighbouring characters.
    /// </param>
    public IReadOnlyList<LineSegment> SplitLine(ReadOnlySpan<float> box,
                                                IReadOnlyList<RecToken> toks, double gap,
                                                IReadOnlyList<double>? at = null)
    {
        double h = Math.Max(1.0, (double)(box[3] - box[1]));
        double[] cuts = at is null || at.Count == 0 ? Array.Empty<double>() : Sorted(at);
        var segs = new List<List<RecToken>> { new() };
        double? prev = null;
        foreach (var tk in toks)
        {
            double x = tk.X;
            if (!IsSpace(tk))
            {
                bool cut = prev is double p &&
                           (x - p > gap * h || AnyBetween(cuts, p, x));
                if (cut)
                    segs.Add(new List<RecToken>());
                prev = x;
            }
            segs[^1].Add(tk);
        }
        if (segs.Count == 1)
            return new[] { new LineSegment(Copy4(box), Decode(toks), toks) };

        var outList = new List<LineSegment>(segs.Count);
        foreach (var sg in segs)
        {
            string text = UnicodeText.Strip(Decode(sg));
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            bool any = false;
            foreach (var tk in sg)
            {
                if (IsSpace(tk))
                    continue;
                minX = Math.Min(minX, tk.X);
                maxX = Math.Max(maxX, tk.X);
                any = true;
            }
            if (text.Length > 0 && any)
            {
                // max/min of a float32 box edge and double bounds: compared as float32 (NEP 50),
                // and the caller converts back through np.array(..., np.float32).
                outList.Add(new LineSegment(
                    new[]
                    {
                        MathF.Max(box[0], (float)(minX - 0.35 * h)),
                        box[1],
                        MathF.Min(box[2], (float)(maxX + 0.35 * h)),
                        box[3],
                    },
                    text, sg));
            }
        }
        return outList;
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>The reference's <c>_is_space</c>: the space character or the vocab's space id.</summary>
    public bool IsSpace(RecToken tok) => tok.Character is not null
        ? tok.Character == " "
        : _spaceId is int id && tok.Id == id;

    /// <summary>
    /// Text of tokens: <c>(vocab id, x)</c> from the recognizer, or <c>(char, x, content index)</c>
    /// from a PDF text layer. The first token decides (the reference's <c>isinstance(toks[0][0], str)</c>).
    /// </summary>
    public string Decode(IReadOnlyList<RecToken> toks)
    {
        if (toks.Count > 0 && toks[0].Character is not null)
        {
            var sb = new System.Text.StringBuilder(toks.Count);
            foreach (var tk in toks)
                sb.Append(tk.Character);
            string text = UnicodeText.CollapseSpaces(sb.ToString());
            // Two-element character tokens are group-recognizer output in visual order; the
            // three-element text-layer form carries ContentIndex and is already logical text.
            if (toks[0].ContentIndex is null)
            {
                text = Nfc.Normalize(text);
                if (RtlText.HasRtl(text)) text = RtlText.Flip(text, baseRtl: _pageRtl ? true : null);
            }
            return text;
        }
        if (_vocab is null)
            throw new InvalidOperationException("an id-token line needs a vocabulary to decode");
        var ids = new int[toks.Count];
        for (int i = 0; i < toks.Count; i++)
            ids[i] = (int)toks[i].Id;
        return _vocab.Decode(ids);
    }

    // ------------------------------------------------------------------ text-layer order

    /// <summary>
    /// Moves upward in content order that do not go to a column on the right — the
    /// <c>order_backtracks</c> of <c>textlayer.py</c>.
    /// </summary>
    public static int OrderBacktracks(ReadOnlySpan<float> boxes, int n)
    {
        if (n < 3)
            return 0;
        var heights = new float[n];
        for (int i = 0; i < n; i++)
            heights[i] = boxes[i * 4 + 3] - boxes[i * 4 + 1];
        double h = Numerics.Median(heights);
        float halfH = (float)(0.5 * h);
        float fullH = (float)h;
        int bt = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            // float32 comparisons: a float32 box edge against a weak Python float (NEP 50).
            if (boxes[(i + 1) * 4 + 1] < boxes[i * 4 + 1] - halfH &&
                boxes[(i + 1) * 4 + 0] < boxes[i * 4 + 2] - fullH)
                bt++;
        }
        return bt;
    }

    /// <summary>
    /// Is the content order a reading order? Few upward moves that are not column changes, and
    /// (with <paramref name="pairLogits"/>, <c>a[i, j] &gt; 0</c>: i before j) the model agrees with
    /// most consecutive pairs — the <c>pdf_order_coherent</c> of <c>textlayer.py</c>.
    /// </summary>
    public static bool PdfOrderCoherent(ReadOnlySpan<float> boxes, int n,
                                        ReadOnlySpan<float> pairLogits = default)
    {
        if (OrderBacktracks(boxes, n) > Math.Max(1, (int)(0.02 * n)))
            return false;
        if (pairLogits.Length < n * n || n < 3)
            return true;
        int positive = 0;
        for (int i = 0; i + 1 < n; i++)
            if (pairLogits[i * n + i + 1] > 0)
                positive++;
        return positive / (double)(n - 1) >= MinModelAgreement;
    }

    // ------------------------------------------------------------------ helpers

    private readonly record struct Rec(string Text, double Conf, IReadOnlyList<RecToken> Toks);

    private static string[] Texts(List<Rec> rec, int n)
    {
        var texts = new string[n];
        for (int i = 0; i < n; i++)
            texts[i] = rec[i].Text;
        return texts;
    }

    /// <summary>BLOCK_CLASSES.index("Table").</summary>
    private static readonly int TableClassIndex = Array.IndexOf(OrderPost.BlockClasses, "Table");

    /// <summary>numpy's <c>argmax(-1)</c> over one row of the class logits: the first maximum.</summary>
    private static int ArgMaxRow(float[] cls, int row)
    {
        int cols = OrderPost.BlockClasses.Length;
        int best = 0;
        float bestV = cls[row * cols];
        for (int c = 1; c < cols; c++)
            if (cls[row * cols + c] > bestV)
            {
                bestV = cls[row * cols + c];
                best = c;
            }
        return best;
    }

    /// <summary><c>sorted(range(n), key=keys[i])</c>: a stable sort, so ties keep index order.</summary>
    private static int[] SortedByKey(int?[] keys, int n)
    {
        var idx = new int[n];
        for (int i = 0; i < n; i++)
            idx[i] = i;
        Array.Sort(idx, (a, b) =>
        {
            int c = keys[a]!.Value.CompareTo(keys[b]!.Value);
            return c != 0 ? c : a.CompareTo(b);
        });
        return idx;
    }

    private static double[] Sorted(IReadOnlyList<double> values)
    {
        var copy = new double[values.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = values[i];
        Array.Sort(copy);
        return copy;
    }

    /// <summary><c>any(lo &lt; c &lt; hi for c in cuts)</c>.</summary>
    private static bool AnyBetween(double[] cuts, double lo, double hi)
    {
        foreach (double c in cuts)
            if (lo < c && c < hi)
                return true;
        return false;
    }

    private static float[] Copy4(ReadOnlySpan<float> box) => new[] { box[0], box[1], box[2], box[3] };
}
