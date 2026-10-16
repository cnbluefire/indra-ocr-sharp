using Indra.Ocr.Imaging;

namespace Indra.Ocr.Document;

/// <summary>
/// A failure of the document backend: the reference's <c>pypdfium2.PdfiumError</c>, raised while
/// opening a document, loading a page, reading a character box or rasterising.
/// </summary>
/// <remarks>
/// Core carries no library name, so the type is named after the surface it escapes from. The
/// name the reference's <c>document._safe</c> puts in a failing page's note is
/// <c>f"{type(e).__name__}: {e}"</c> — i.e. <c>PdfiumError</c> — so that one place restores it
/// (step 160), where the type name is the observable part.
/// </remarks>
public sealed class TextPageError : Exception
{
    public TextPageError(string message, ulong? errorCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The <c>FPDF_GetLastError</c> code, when the error came from PDFium.</summary>
    public ulong? ErrorCode { get; }
}

/// <summary>
/// One character of a page's text layer, as the page reports it, in text-page space (the frame
/// PDFium reports character boxes in: crop box and page rotation applied, bottom-left origin).
/// </summary>
/// <remarks>
/// <paramref name="Left"/>, <paramref name="Right"/>, <paramref name="Bottom"/> and
/// <paramref name="Top"/> are <c>FPDFText_GetCharBox</c>'s outputs in its own order.
/// <paramref name="HasBox"/> is false when PDFium refused to give a box for the character;
/// the reference then skips the character, so the policy decides whether that matters
/// (<c>textlayer.get_box</c>).
/// </remarks>
public readonly record struct PageChar(
    uint Code,
    float Angle,
    bool HasBox,
    double Left,
    double Right,
    double Bottom,
    double Top);

/// <summary>
/// One page of a document that has a text layer, as the pipeline sees it: the surface
/// <c>Indra.Ocr.Pdf</c> implements with PDFium, and the tests implement with recorded data.
/// </summary>
/// <remarks>
/// A page is both pixels and text: <see cref="Render"/> is what the OCR path uses (and what
/// every image input reduces to), and the character calls are the text layer a PDF adds on top.
/// The members mirror exactly the pypdfium2 calls <c>indra_ocr.textlayer</c> and
/// <c>indra_ocr.document</c> make, so the policy that consumes them stays free of PDFium —
/// the same arrangement as <see cref="Indra.Ocr.Imaging.GrayImage"/> for pixels.
/// </remarks>
public interface ITextPage : IDisposable
{
    /// <summary>Page width in PDF canvas units, as rendered (<c>FPDF_GetPageWidthF</c>).</summary>
    double Width { get; }

    /// <summary>Page height in PDF canvas units, as rendered (<c>FPDF_GetPageHeightF</c>).</summary>
    double Height { get; }

    /// <summary>
    /// Every character of the text layer, in PDF order
    /// (<c>FPDFText_CountChars</c>/<c>GetUnicode</c>/<c>GetCharAngle</c>/<c>GetCharBox</c>).
    /// </summary>
    IReadOnlyList<PageChar> ReadChars();

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
public interface ITextDocument : IDisposable
{
    int PageCount { get; }

    /// <summary>Page width/height in canvas units, as rendered.</summary>
    (double Width, double Height) PageSize(int index);

    /// <summary>Loads one page (0-based); the caller disposes it.</summary>
    ITextPage LoadPage(int index);
}
