using Indra.Ocr.Imaging;

namespace Indra.Ocr.PdfText;

/// <summary>
/// The PDFium error the reference raises (<c>pypdfium2.PdfiumError</c>). It is named
/// <c>PdfiumError</c> because <c>document._safe</c> reports a failing page as
/// <c>f"{type(e).__name__}: {e}"</c>, so the type name is part of the output.
/// </summary>
public sealed class PdfiumError : Exception
{
    public PdfiumError(string message, ulong? errorCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The <c>FPDF_GetLastError</c> code, when the error came from PDFium.</summary>
    public ulong? ErrorCode { get; }
}

/// <summary>
/// One character of a page's text layer, in PDF page space (the frame PDFium reports
/// character boxes in: crop box and page rotation applied, bottom-left origin).
/// </summary>
/// <remarks>
/// <paramref name="Left"/>, <paramref name="Right"/>, <paramref name="Bottom"/> and
/// <paramref name="Top"/> are <c>FPDFText_GetCharBox</c>'s outputs in its own order.
/// <paramref name="HasBox"/> is false when PDFium refused to give a box for the character;
/// the reference then skips the character, so the policy decides whether that matters
/// (<c>textlayer.get_box</c>).
/// </remarks>
public readonly record struct PdfChar(
    uint Code,
    float Angle,
    bool HasBox,
    double Left,
    double Right,
    double Bottom,
    double Top);

/// <summary>
/// One PDF page as the text-layer pipeline sees it: the surface <c>Indra.Ocr.Pdf</c>
/// implements with PDFium, and the step-150 tests implement with recorded data.
/// </summary>
/// <remarks>
/// The members mirror exactly the pypdfium2 calls <c>indra_ocr.textlayer</c> and
/// <c>indra_ocr.document</c> make, so the policy that consumes them (the text-layer
/// reader of step 150) stays free of PDFium.
/// </remarks>
public interface IPdfPage : IDisposable
{
    /// <summary>Page width in PDF canvas units, as rendered (<c>FPDF_GetPageWidthF</c>).</summary>
    double Width { get; }

    /// <summary>Page height in PDF canvas units, as rendered (<c>FPDF_GetPageHeightF</c>).</summary>
    double Height { get; }

    /// <summary>
    /// Every character of the text layer, in PDF order
    /// (<c>FPDFText_CountChars</c>/<c>GetUnicode</c>/<c>GetCharAngle</c>/<c>GetCharBox</c>).
    /// </summary>
    IReadOnlyList<PdfChar> ReadChars();

    /// <summary>
    /// The affine that maps PDF page coordinates to the rendered frame, as a
    /// <c>[2, 3]</c> matrix (<c>textlayer._device_affine</c>).
    /// </summary>
    double[,] DeviceAffine();

    /// <summary>
    /// Share of the page's text objects that are drawn invisibly (render mode 3), the
    /// OCR-layer-over-a-scan case (<c>textlayer.invisible_text_frac</c>).
    /// </summary>
    double InvisibleTextFraction();

    /// <summary>
    /// Rasterises the page at <paramref name="scale"/> pixels per canvas unit, grayscale,
    /// on white (<c>page.render(scale=..., grayscale=True)</c>).
    /// </summary>
    GrayPixels Render(double scale);
}

/// <summary>An open PDF document, as <c>document.Converter</c> needs it.</summary>
public interface IPdfDocument : IDisposable
{
    int PageCount { get; }

    /// <summary>Page width/height in canvas units, as rendered.</summary>
    (double Width, double Height) PageSize(int index);

    /// <summary>Loads one page (0-based); the caller disposes it.</summary>
    IPdfPage LoadPage(int index);
}
