using Indra.Ocr.Imaging;

namespace Indra.Ocr.Pdf;

/// <summary>
/// A PDF document backed by PDFium. This project is the <b>only</b> place that depends on
/// the PDFium NuGet package, keeping <c>Indra.Ocr.Core</c> independent of it.
/// </summary>
/// <remarks>
/// PDFium can rasterise straight to 8-bit grayscale (<c>FPDFBitmap_Gray</c>), which is what
/// the Python reference asks pypdfium2 for
/// (<c>page.render(scale=dpi/72, grayscale=True)</c>), so rendered pages need no decoding
/// or colour conversion.
/// <para>
/// The full implementation (page rendering at a DPI and text-layer extraction via
/// <c>FPDFText_*</c>) lands in step 140. The API surface is defined here so the rest of the
/// pipeline can be written against it.
/// </para>
/// </remarks>
public sealed class PdfDocument : IDisposable
{
    public PdfDocument(string path, string? password = null)
    {
        Path = path;
        Password = password;
        throw new NotImplementedException("Indra.Ocr.Pdf.PdfDocument is implemented in step 140.");
    }

    public string Path { get; }

    public string? Password { get; }

    public int PageCount => throw new NotImplementedException();

    /// <summary>Rasterises one page (0-based) to grayscale at <paramref name="dpi"/>.</summary>
    public GrayImage Render(int pageIndex, double dpi = 200.0)
        => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
