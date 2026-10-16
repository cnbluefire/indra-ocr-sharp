namespace Indra.Ocr.Pdf;

/// <summary>
/// A failure of the PDFium backend: the reference's <c>pypdfium2.PdfiumError</c>, raised while
/// opening a document, loading a page, reading a character box or rasterising.
/// </summary>
/// <remarks>
/// The name matters: <c>document._safe</c> reports a failing page as
/// <c>f"{type(e).__name__}: {e}"</c>, so this type's name is part of the note a caller prints —
/// and the reference's type really is <c>PdfiumError</c>. It lives in this project, the only one
/// that talks to PDFium, so the library name is honest here (Core, which must not mention it,
/// has no such type).
/// </remarks>
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
/// One character of a page's text layer, as the page reports it, in text-page space (the frame
/// PDFium reports character boxes in: crop box and page rotation applied, bottom-left origin).
/// </summary>
/// <remarks>
/// <paramref name="Left"/>, <paramref name="Right"/>, <paramref name="Bottom"/> and
/// <paramref name="Top"/> are <c>FPDFText_GetCharBox</c>'s outputs in its own order.
/// <paramref name="HasBox"/> is false when PDFium refused to give a box for the character;
/// the reference then skips the character, so the policy decides whether that matters
/// (<c>textlayer.get_box</c>).
/// <para>
/// This is raw PDFium output — the input of <see cref="PdfPage.ExtractRuns"/> — so it stays with
/// the PDF backend; the runs it produces (<c>Indra.Ocr.TextLayer.TextLine</c>) are the shared
/// data.
/// </para>
/// </remarks>
public readonly record struct PageChar(
    uint Code,
    float Angle,
    bool HasBox,
    double Left,
    double Right,
    double Bottom,
    double Top);
