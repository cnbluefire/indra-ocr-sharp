using System.Diagnostics;
using Indra.Ocr.Imaging;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

namespace Indra.Ocr.Pdf;

/// <summary>
/// The port of the PDF half of <c>document.Converter</c>: whole PDFs — the text layer where it can
/// be trusted, OCR elsewhere.
/// </summary>
/// <remarks>
/// For each page (<c>mode="auto"</c>): read the text layer and check it
/// (<see cref="TextLayerReader"/> over <see cref="PdfPage.ExtractRuns"/>); a page without a sound
/// layer (a scan, an OCR layer, a broken font map) goes to OCR. The detector then finds the visual
/// lines, every text-layer character goes to the detected line that contains its centre
/// (<see cref="TextLayerRuns.AssignRuns"/>), and the rest is the OCR path — lines split at wide
/// gaps, reading order, layout classes and markdown, with the PDF content order kept when it is
/// coherent.
/// <para>
/// <c>mode="ocr"</c> never reads the text layer; <c>mode="text"</c> uses it even when the checks
/// fail (and OCRs only pages with no text at all).
/// </para>
/// <para>
/// Image files are <c>Indra.Ocr.Images.ImageConverter</c>'s business — the other half of
/// <c>document.convert</c>. The two never meet: the caller picks one with
/// <see cref="OrtOcr.IsPdf"/>.
/// </para>
/// </remarks>
public sealed class PdfConverter
{
    /// <summary>Resolution of the render for the text-layer ink checks.</summary>
    public const double CheckDpi = 72.0;

    /// <summary>
    /// A page larger than this many pixels at the set dpi renders at a lower dpi (large scans
    /// reach ~65 MP).
    /// </summary>
    public const double MaxPagePixels = 100e6;

    private readonly OrtOcr _ocr;
    private readonly double _dpi;
    private readonly string _mode;
    private readonly bool _pdfOrder;
    private readonly bool _imageOcr;

    /// <summary>
    /// <paramref name="mode"/>: auto (text layer where sound, OCR elsewhere), ocr (always OCR,
    /// never read the text layer), text (text layer even when the checks fail).
    /// <paramref name="pdfOrder"/>: keep the PDF content order when it is coherent (false: the
    /// order model always orders the lines). <paramref name="imageOcr"/>: OCR detected lines that
    /// hold no text-layer characters (false: drop them).
    /// </summary>
    public PdfConverter(
        OrtOcr ocr,
        double dpi = 200.0,
        string mode = "auto",
        bool pdfOrder = true,
        bool imageOcr = true)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        if (mode is not ("auto" or "ocr" or "text"))
        {
            throw new ArgumentException($"mode must be auto, ocr or text, not {Repr(mode)}",
                                        nameof(mode));
        }

        _ocr = ocr;
        _dpi = dpi;
        _mode = mode;
        _pdfOrder = pdfOrder;
        _imageOcr = imageOcr;
    }

    /// <summary>The set resolution (pixels per point) before the large-page cap.</summary>
    public double Dpi => _dpi;

    public string Mode => _mode;

    public bool PdfOrder => _pdfOrder;

    public bool ImageOcr => _imageOcr;

    /// <summary>
    /// <c>page_scale</c>: pixels per point for a page of this size — the set dpi, lowered for
    /// pages so large that the render would pass <see cref="MaxPagePixels"/> (posters, plans).
    /// </summary>
    public static double PageScale(double width, double height, double dpi)
    {
        double k = dpi / 72.0;
        double area = Math.Max(width * height, 1.0);
        return Math.Min(k, Math.Sqrt(MaxPagePixels / area));
    }

    /// <inheritdoc cref="PageScale(double, double, double)"/>
    public double PageScale(PdfPage page) => PageScale(page.Width, page.Height, _dpi);

    /// <summary>
    /// <c>pdf_page</c>: one page of a PDF — the text layer when it can be trusted, OCR otherwise.
    /// </summary>
    public OcrPage PdfPage(PdfDocument doc, int index)
    {
        using PdfPage page = doc.LoadPage(index);
        var timings = new Dictionary<string, double>();
        string note = "mode=ocr";
        bool turn;
        if (_mode != "ocr")
        {
            var textClock = Stopwatch.StartNew();
            double s = CheckDpi / 72.0;
            TextLayer layer = TextLayerReader.ReadTextLayer(
                page.Render(s), s, page.InvisibleTextFraction(), (page.Width, page.Height),
                () => page.ExtractRuns());
            timings["text_ms"] = textClock.Elapsed.TotalMilliseconds;
            // A layer the recognizer cannot read beats OCR, unless its codes are junk.
            bool unreadable = layer.Reason != TextLayerReader.BrokenFontMap
                              && !TextLayerRuns.Readable(_ocr, layer.Lines);
            if (layer.Lines.Count > 0 && (layer.Trusted || _mode == "text" || unreadable))
            {
                return TextPage(page, layer, timings);
            }

            note = layer.Reason;
            // A layer with a broken font map still places its glyphs: when most run upright,
            // the page is upright (orient.onnx is not asked).
            TextLayerStats stats = layer.Stats;
            turn = !(layer.Reason == TextLayerReader.BrokenFontMap
                     && stats.Rotated < 0.5 * stats.Chars);
        }
        else
        {
            turn = true;
        }

        var renderClock = Stopwatch.StartNew();
        GrayImage gray = page.Render(PageScale(page));
        timings["render_ms"] = renderClock.Elapsed.TotalMilliseconds;

        OcrPage ocr = _ocr.Call(gray, turn);
        // The reference merges its own timings in front of the pipeline's (keeping a shared
        // key's position) and recomputes total_ms over all of them.
        var merged = new Dictionary<string, double>(timings);
        foreach (var kv in ocr.Timings)
        {
            merged[kv.Key] = kv.Value;
        }

        double total = 0.0;
        foreach (var kv in merged)
        {
            if (kv.Key != "total_ms")
            {
                total += kv.Value;
            }
        }

        merged["total_ms"] = total;
        return new OcrPage(ocr.Lines, merged, ocr.Markdown, ocr.Source, $"{note}; {ocr.Note}",
                           ocr.Blocks, ocr.Rotation, ocr.Skew);
    }

    /// <summary>
    /// <c>_text_page</c>: the trusted text layer, placed on the detected lines, with OCR only for
    /// the lines the layer does not cover.
    /// </summary>
    public OcrPage TextPage(PdfPage page, TextLayer layer, IDictionary<string, double> timings)
    {
        double k = PageScale(page);
        var renderClock = Stopwatch.StartNew();
        GrayImage gray = page.Render(k);
        timings["render_ms"] = renderClock.Elapsed.TotalMilliseconds;

        var detectClock = Stopwatch.StartNew();
        float[] boxes = _ocr.Detect(gray);
        timings["det_ms"] = detectClock.Elapsed.TotalMilliseconds;

        (List<List<RecToken>> toks, int lost) = TextLayerRuns.AssignRuns(layer.Lines, OrtOcr.Quads(boxes), k);
        int n = boxes.Length / 5;
        var rec = new RecognizedLine?[n];
        var empty = new List<int>();
        for (int i = 0; i < n; i++)
        {
            List<RecToken> tk = toks[i];
            string text = tk.Count > 0 ? _ocr.Decode(tk) : "";
            if (text.Length > 0)
            {
                rec[i] = new RecognizedLine(text, 1.0, tk);
            }
            else
            {
                empty.Add(i);
            }
        }

        string source = "text";
        if (empty.Count > 0 && _imageOcr && TextLayerRuns.Readable(_ocr, layer.Lines))
        {
            var recClock = Stopwatch.StartNew();
            IReadOnlyList<RecognizedLine> read = _ocr.Recognize(gray, Subset(boxes, empty));
            for (int j = 0; j < empty.Count; j++)
            {
                if (UnicodeText.Strip(read[j].Text).Length > 0)
                {
                    rec[empty[j]] = read[j];
                    source = "hybrid";
                }
            }

            timings["rec_ms"] = recClock.Elapsed.TotalMilliseconds;
        }

        // The reference reads the math lines here when the math models are loaded
        // (`getattr(self.ocr, "math", None) is not None`); OrtOcr refuses math=True until step
        // 200, so the branch is unreachable.

        var keep = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (rec[i] is not null)
            {
                keep.Add(i);
            }
        }

        var keepRec = new List<RecognizedLine>(keep.Count);
        foreach (int i in keep)
        {
            keepRec.Add(rec[i]!);
        }

        OcrPage outPage = _ocr.Assemble(gray.Width, gray.Height, OrtOcr.Quads(Subset(boxes, keep)),
                                        keep.Count, keepRec, timings, _pdfOrder);
        string note = lost > 0
            ? $"{outPage.Note}; {lost} chars outside detected lines"
            : outPage.Note;
        return new OcrPage(outPage.Lines, outPage.Timings, outPage.Markdown, source, note,
                           outPage.Blocks, outPage.Rotation, outPage.Skew);
    }

    /// <summary>
    /// <c>convert</c> for a PDF: (page index, page) for every page, capped at
    /// <paramref name="maxPages"/> (0: all). A page that fails yields an empty page whose
    /// <c>source</c> is "error" and whose <c>note</c> says why; a file that cannot be opened
    /// raises <see cref="InputError"/>. The caller picks this converter or
    /// <c>Indra.Ocr.Images.ImageConverter</c> with <see cref="OrtOcr.IsPdf"/>.
    /// </summary>
    public IEnumerable<(int Index, OcrPage Page)> Convert(string path, int maxPages = 0,
                                                          string? password = null)
    {
        if (!File.Exists(path))
        {
            throw new InputError($"{path}: no such file");
        }

        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(path, password);
        }
        catch (PdfiumError e)
        {
            // pypdfium2 says "password" in the error text when the document is locked.
            bool locked = e.Message.Contains("password", StringComparison.OrdinalIgnoreCase);
            throw new InputError(locked
                ? $"{path}: password-protected PDF (pass password=, or --password)"
                : $"{path}: cannot open the PDF ({e.Message})");
        }

        try
        {
            int total = doc.PageCount;
            int pages = maxPages <= 0 ? total : Math.Min(total, maxPages);
            for (int i = 0; i < pages; i++)
            {
                int index = i;
                yield return (index, SafePage.Read(() => PdfPage(doc, index)));
            }
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// <summary>The detector's boxes for a subset of lines (<c>boxes[idx]</c>).</summary>
    private static float[] Subset(ReadOnlySpan<float> boxes, IReadOnlyList<int> idx)
    {
        var subset = new float[idx.Count * 5];
        for (int i = 0; i < idx.Count; i++)
        {
            boxes.Slice(idx[i] * 5, 5).CopyTo(subset.AsSpan(i * 5));
        }

        return subset;
    }

    /// <summary>Python's <c>repr</c> of a string, for the mode error message.</summary>
    private static string Repr(string s)
    {
        var sb = new System.Text.StringBuilder("'");
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => char.IsControl(c) ? $"\\x{(int)c:x2}" : c.ToString(),
            });
        }

        return sb.Append('\'').ToString();
    }
}
