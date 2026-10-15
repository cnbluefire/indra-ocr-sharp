using System.Globalization;
using Indra.Ocr.Imaging;
using Indra.Ocr.Text;

namespace Indra.Ocr.PdfText;

/// <summary>One character of a text-layer run: the character and its box (l, b, r, t).</summary>
public readonly record struct TextChar(string Character, double[] Bbox);

/// <summary>One text-layer run: its box, its text, and its characters with boxes.</summary>
public sealed class TextLine
{
    public TextLine(double[] bbox, string text, IReadOnlyList<TextChar> chars)
    {
        Bbox = bbox;
        Text = text;
        Chars = chars;
    }

    /// <summary>The box (left, top, right, bottom) in the rendered frame, from the ink characters.</summary>
    public double[] Bbox { get; }

    public string Text { get; }

    /// <summary>The characters in PDF order (whitespace included).</summary>
    public IReadOnlyList<TextChar> Chars { get; }
}

/// <summary>
/// What <c>extract_runs</c> and <c>read_text_layer</c> counted: the reference's <c>stats</c>
/// dictionary. <see cref="OffPage"/> is only filled in by
/// <see cref="TextLayerReader.ExtractRuns"/>, <see cref="NoInk"/> only by
/// <see cref="TextLayerReader.ReadTextLayer"/>.
/// </summary>
public sealed record TextLayerStats
{
    /// <summary>Non-whitespace characters seen.</summary>
    public int Chars { get; init; }

    /// <summary>Characters dropped because they are not upright on the rendered page.</summary>
    public int Rotated { get; init; }

    /// <summary>Characters whose code is not text (replacement, private use, control, ...).</summary>
    public int Bad { get; init; }

    /// <summary>Characters whose centre falls outside the rendered frame.</summary>
    public int OffPage { get; init; }

    /// <summary>Share of runs with no ink under them, rounded to 4 decimals (Python's <c>round</c>).</summary>
    public double? NoInk { get; init; }
}

/// <summary>The text layer of one page and the verdict on it.</summary>
public sealed class TextLayer
{
    public TextLayer(
        IReadOnlyList<TextLine> lines,
        (double Width, double Height) size,
        bool trusted,
        string reason,
        TextLayerStats stats)
    {
        Lines = lines;
        Size = size;
        Trusted = trusted;
        Reason = reason;
        Stats = stats;
    }

    /// <summary>The runs in PDF content order.</summary>
    public IReadOnlyList<TextLine> Lines { get; }

    /// <summary>Page width and height in points, as rendered.</summary>
    public (double Width, double Height) Size { get; }

    public bool Trusted { get; }

    /// <summary>Why the layer is not trusted (empty when it is).</summary>
    public string Reason { get; }

    public TextLayerStats Stats { get; }
}

/// <summary>
/// The port of <c>indra_ocr/textlayer.py</c>: which text-layer runs a page has, and whether
/// they can be trusted in place of OCR.
/// </summary>
/// <remarks>
/// The PDFium calls themselves live behind <see cref="IPdfPage"/> (step 140); everything here
/// is the policy, so it runs against recorded page data in the tests. The two content-order
/// checks <c>textlayer.py</c> also holds (<c>order_backtracks</c>, <c>pdf_order_coherent</c>)
/// live in <c>Assembler</c>, their only runtime caller.
/// </remarks>
public static class TextLayerReader
{
    public const double MaxInvisibleFraction = 0.05;
    public const double MaxBadCharFraction = 0.005;
    public const double MaxNoInkLineFraction = 0.03;

    /// <summary>Grey level below which a pixel counts as ink.</summary>
    public const int Dark = 160;

    /// <summary><see cref="TextLayer.Reason"/> when the layer's codes are not text.</summary>
    public const string BrokenFontMap = "broken font map";

    /// <summary>
    /// Characters of a string that are not text: the replacement character, private use,
    /// surrogates, unassigned code points, and control characters other than a tab.
    /// </summary>
    public static int BadCharCount(string text)
    {
        int n = 0;
        foreach (int codePoint in UnicodeText.CodePointValues(text))
        {
            if (codePoint == 0xFFFD)
            {
                n++;
                continue;
            }

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            if (category is UnicodeCategory.PrivateUse
                          or UnicodeCategory.Surrogate
                          or UnicodeCategory.OtherNotAssigned
                || (category == UnicodeCategory.Control && codePoint != '\t'))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// The upright text runs of a page, in content order, with the statistics
    /// (<c>textlayer.extract_runs</c>).
    /// </summary>
    public static (List<TextLine> Runs, TextLayerStats Stats) ExtractRuns(IPdfPage page)
    {
        IReadOnlyList<PdfChar> source = page.ReadChars();
        // The linear part of the page-to-frame map: a character is upright when its baseline
        // runs left to right once mapped through it (any /Rotate, any mirroring).
        double[,] affine = page.DeviceAffine();

        var upright = new Dictionary<double, bool>();
        var bad = new Dictionary<string, int>();
        var chars = new List<string>();
        var rawBoxes = new List<double[]>();
        var breaks = new List<int>(); // run ends after these positions
        int nChars = 0, nRotated = 0, nBad = 0;

        for (int i = 0; i < source.Count; i++)
        {
            PdfChar pc = source[i];
            string c = Chr(pc.Code);
            if (c is "\r" or "\n")
            {
                breaks.Add(chars.Count);
                continue;
            }

            if (c == "\x02")
            {
                c = "-"; // pdfium's mark for a hyphen at a line break
            }

            if (!UnicodeText.IsSpace(c))
            {
                nChars++;
                if (!bad.TryGetValue(c, out int k))
                {
                    k = BadCharCount(c);
                    bad[c] = k;
                }

                nBad += k;
                double a = pc.Angle;
                if (!upright.TryGetValue(a, out bool up))
                {
                    double cos = Math.Cos(a), sin = Math.Sin(a);
                    double dx = affine[0, 0] * cos + affine[0, 1] * sin;
                    double dy = affine[1, 0] * cos + affine[1, 1] * sin;
                    up = !(dx <= 0 || Math.Abs(dy) > 1e-3 * Math.Max(Math.Abs(dx), 1e-9));
                    upright[a] = up;
                }

                if (!up)
                {
                    nRotated++; // not upright on the rendered page
                    breaks.Add(chars.Count);
                    continue;
                }
            }

            chars.Add(c);
            if (!pc.HasBox)
            {
                throw new PdfiumError("Failed to get charbox.");
            }

            rawBoxes.Add([pc.Left, pc.Bottom, pc.Right, pc.Top]); // l, b, r, t
        }

        TextLayerStats stats = new() { Chars = nChars, Rotated = nRotated, Bad = nBad };
        if (chars.Count == 0)
        {
            return ([], stats);
        }

        // Page space -> rendered frame, all characters at once: the four corners of each box
        // (l,b), (r,b), (l,t), (r,t), then their bounding box.
        int m = chars.Count;
        var boxes = new double[m][];
        var visible = new bool[m];
        int offPage = 0;
        double pageWidth = page.Width, pageHeight = page.Height;
        for (int i = 0; i < m; i++)
        {
            double l = rawBoxes[i][0], b = rawBoxes[i][1], r = rawBoxes[i][2], t = rawBoxes[i][3];
            double x0 = affine[0, 0] * l + affine[0, 1] * b + affine[0, 2];
            double x1 = affine[0, 0] * r + affine[0, 1] * b + affine[0, 2];
            double x2 = affine[0, 0] * l + affine[0, 1] * t + affine[0, 2];
            double x3 = affine[0, 0] * r + affine[0, 1] * t + affine[0, 2];
            double y0 = affine[1, 0] * l + affine[1, 1] * b + affine[1, 2];
            double y1 = affine[1, 0] * r + affine[1, 1] * b + affine[1, 2];
            double y2 = affine[1, 0] * l + affine[1, 1] * t + affine[1, 2];
            double y3 = affine[1, 0] * r + affine[1, 1] * t + affine[1, 2];
            var box = new[]
            {
                Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
                Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
                Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)),
                Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)),
            };
            boxes[i] = box;

            // Characters outside the rendered frame (beyond a crop box) are not on the page:
            // they are dropped, not read.
            double cx = (box[0] + box[2]) / 2, cy = (box[1] + box[3]) / 2;
            visible[i] = cx >= 0 && cx <= pageWidth && cy >= 0 && cy <= pageHeight;
            if (!visible[i])
            {
                offPage++;
            }
        }

        stats = stats with { OffPage = offPage };

        // A run is pdfium's own segment: the breaks it emits, plus the end of the text.
        var cuts = new List<int>(breaks) { m };
        cuts.Sort();
        var runs = new List<TextLine>();
        int start = 0;
        foreach (int end in cuts)
        {
            var seg = new List<TextChar>();
            for (int i = start; i < end; i++)
            {
                if (visible[i])
                {
                    seg.Add(new TextChar(chars[i], boxes[i]));
                }
            }

            start = end;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            bool anyInk = false;
            foreach (TextChar sc in seg)
            {
                if (UnicodeText.IsSpace(sc.Character))
                {
                    continue;
                }

                anyInk = true;
                minX = Math.Min(minX, sc.Bbox[0]);
                minY = Math.Min(minY, sc.Bbox[1]);
                maxX = Math.Max(maxX, sc.Bbox[2]);
                maxY = Math.Max(maxY, sc.Bbox[3]);
            }

            if (!anyInk)
            {
                continue;
            }

            var text = new System.Text.StringBuilder();
            foreach (TextChar sc in seg)
            {
                text.Append(sc.Character);
            }

            runs.Add(new TextLine([minX, minY, maxX, maxY], UnicodeText.CollapseSpaces(text.ToString()), seg));
        }

        return (runs, stats);
    }

    /// <summary>
    /// Share of runs whose box holds no dark pixel in <paramref name="gray"/> (which is the
    /// page rendered at <paramref name="scale"/> pixels per point).
    /// </summary>
    public static double NoInkFraction(GrayImage gray, IReadOnlyList<TextLine> runs, double scale)
    {
        int height = gray.Height, width = gray.Width;
        int empty = 0;
        foreach (TextLine line in runs)
        {
            double x0 = line.Bbox[0], y0 = line.Bbox[1], x1 = line.Bbox[2], y1 = line.Bbox[3];
            // A run's box always contains the centre of one of its characters, and the
            // visibility filter kept those on the page, so both slices are well formed.
            int rowFrom = Math.Max(0, (int)(y0 * scale));
            int rowTo = Math.Min(height, (int)(y1 * scale) + 1);
            int colFrom = Math.Max(0, (int)(x0 * scale));
            int colTo = Math.Min(width, (int)(x1 * scale) + 1);
            bool ink = false;
            for (int y = rowFrom; y < rowTo && !ink; y++)
            {
                ReadOnlySpan<byte> row = gray.Row(y);
                for (int x = colFrom; x < colTo; x++)
                {
                    if (row[x] < Dark)
                    {
                        ink = true;
                        break;
                    }
                }
            }

            if (!ink)
            {
                empty++;
            }
        }

        return (double)empty / Math.Max(runs.Count, 1);
    }

    /// <summary>
    /// The text-layer runs of a page and the verdict on them
    /// (<c>textlayer.read_text_layer</c>). <paramref name="gray"/> is a render of the page at
    /// <paramref name="scale"/> pixels per point — a low resolution is enough for the ink check.
    /// </summary>
    public static TextLayer ReadTextLayer(IPdfPage page, GrayImage gray, double scale)
    {
        (double Width, double Height) size = (page.Width, page.Height);
        double invisible = page.InvisibleTextFraction();
        if (invisible > MaxInvisibleFraction)
        {
            return new TextLayer(
                [], size, false, $"invisible text {Percent0(invisible)} (OCR layer)", new TextLayerStats());
        }

        var (runs, stats) = ExtractRuns(page);
        if (runs.Count == 0)
        {
            return new TextLayer([], size, false, "no text layer", stats);
        }

        if (stats.Bad > MaxBadCharFraction * Math.Max(stats.Chars, 1))
        {
            return new TextLayer(runs, size, false, BrokenFontMap, stats);
        }

        double empty = NoInkFraction(gray, runs, scale);
        stats = stats with { NoInk = Numerics.RoundHalfEven(empty, 4) };
        if (empty > MaxNoInkLineFraction)
        {
            return new TextLayer(runs, size, false, $"{Percent0(empty)} of text runs have no ink", stats);
        }

        return new TextLayer(runs, size, true, "", stats);
    }

    /// <summary>
    /// Python's <c>chr</c>: one code unit, or a surrogate pair for an astral code point.
    /// <c>FPDFText_GetUnicode</c> hands out UTF-16 code units, so lone surrogates are normal.
    /// </summary>
    private static string Chr(uint code)
        => code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32((int)code);

    /// <summary>Python's <c>f"{x:.0%}"</c>: the value times 100, rounded half to even, then "%".</summary>
    private static string Percent0(double fraction)
        => ((long)Numerics.RoundHalfEven(fraction * 100.0, 0)).ToString(CultureInfo.InvariantCulture) + "%";
}
