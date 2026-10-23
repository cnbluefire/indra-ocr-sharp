using System.Globalization;
using Indra.Ocr.Imaging;

namespace Indra.Ocr.Text;

/// <summary>
/// The verdict half of the port of <c>indra_ocr/textlayer.py</c>: whether a page's text-layer runs
/// can be trusted in place of OCR.
/// </summary>
/// <remarks>
/// Reading the runs off a page is the PDF backend's business — <c>PdfPage.ExtractRuns</c> makes
/// the PDFium calls (<c>get_unicode</c>/<c>get_angle</c>/<c>get_box</c>, the device affine, the
/// page size) and hands back <see cref="TextLine"/>s and a <see cref="TextLayerStats"/>. Everything
/// here works on those plus a render, so it is plain data in and plain data out: the
/// recognition-result side of the module, with no PDFium in sight.
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
    public const string VisualRtl = "right-to-left text in visual order";
    public const int MinRtlPairs = 20;
    public const double MaxVisualRtlFraction = 0.25;

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

            UnicodeCategory category = UnicodeCompat.Category(codePoint);
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
    /// The verdict on a page's text-layer runs (<c>textlayer.read_text_layer</c>).
    /// <paramref name="gray"/> is a render of the page at <paramref name="scale"/> pixels per
    /// point — a low resolution is enough for the ink check.
    /// </summary>
    /// <remarks>
    /// The runs are read through <paramref name="extractRuns"/> rather than passed in, because the
    /// reference checks the invisible-text fraction <b>before</b> it touches a single character
    /// box: a page whose text is drawn invisibly (an OCR layer over a scan) never reaches
    /// <c>extract_runs</c>, so a character box PDFium refuses cannot turn that verdict into an
    /// error.
    /// </remarks>
    public static TextLayer ReadTextLayer(
        GrayImage gray,
        double scale,
        double invisibleFraction,
        (double Width, double Height) size,
        Func<(IReadOnlyList<TextLine> Runs, TextLayerStats Stats)> extractRuns)
    {
        if (invisibleFraction > MaxInvisibleFraction)
        {
            return new TextLayer(
                [], size, false, $"invisible text {Percent0(invisibleFraction)} (OCR layer)",
                new TextLayerStats());
        }

        (IReadOnlyList<TextLine> runs, TextLayerStats stats) = extractRuns();
        if (runs.Count == 0)
        {
            return new TextLayer([], size, false, "no text layer", stats);
        }

        if (stats.Bad > MaxBadCharFraction * Math.Max(stats.Chars, 1))
        {
            return new TextLayer(runs, size, false, BrokenFontMap, stats);
        }

        (int pairs, double visual) = VisualRtlFraction(runs);
        if (pairs >= MinRtlPairs && visual > MaxVisualRtlFraction)
        {
            stats = stats with { VisualRtl = Numerics.RoundHalfEven(visual, 3) };
            return new TextLayer(runs, size, false, VisualRtl, stats);
        }

        double empty = NoInkFraction(gray, runs, scale);
        stats = stats with { NoInk = Numerics.RoundHalfEven(empty, 4) };
        if (empty > MaxNoInkLineFraction)
        {
            return new TextLayer(runs, size, false, $"{Percent0(empty)} of text runs have no ink",
                                 stats);
        }

        return new TextLayer(runs, size, true, "", stats);
    }

    /// <summary>Python's <c>f"{x:.0%}"</c>: the value times 100, rounded half to even, then "%".</summary>
    private static string Percent0(double fraction)
        => ((long)Numerics.RoundHalfEven(fraction * 100.0, 0)).ToString(CultureInfo.InvariantCulture) + "%";

    public static (int Pairs, double Fraction) VisualRtlFraction(IReadOnlyList<TextLine> runs)
    {
        int pairs = 0, right = 0;
        foreach (TextLine run in runs)
            for (int i = 0; i + 1 < run.Chars.Count; i++)
                if (RtlText.IsRtl(First(run.Chars[i].Character)) &&
                    RtlText.IsRtl(First(run.Chars[i + 1].Character)))
                {
                    pairs++;
                    double a = run.Chars[i].Bbox[0] + run.Chars[i].Bbox[2];
                    double b = run.Chars[i + 1].Bbox[0] + run.Chars[i + 1].Bbox[2];
                    if (b > a) right++;
                }
        return (pairs, (double)right / Math.Max(pairs, 1));
    }

    private static int First(string s) => char.IsSurrogatePair(s, 0) ? char.ConvertToUtf32(s, 0) : s[0];

    public static int PageTurn(TextLayerStats stats)
    {
        IReadOnlyList<int>? quarters = stats.Quarters;
        if (quarters is null || quarters.Count < 4) return 0;
        int best = 1;
        for (int i = 2; i < 4; i++) if (quarters[i] > quarters[best]) best = i;
        return quarters[best] > 0.5 * Math.Max(stats.Chars, 1) ? best : 0;
    }

    public static bool VerticalLayout(TextLayerStats stats)
        => stats.Steps is { Count: >= 2 } steps && steps[0] >= 30 && steps[0] > 2 * steps[1];
}
