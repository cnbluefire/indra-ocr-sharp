using Indra.Ocr.Pdf.Native;

namespace Indra.Ocr.Pdf;

/// <summary>
/// PDFium process-wide lifecycle and error strings, the parts of <c>pypdfium2</c> the
/// pipeline relies on implicitly.
/// </summary>
public static class Pdfium
{
    // System.Threading.Lock is .NET 9; below it a plain object gates the same way.
#if NET9_0_OR_GREATER
    private static readonly Lock Gate = new();
#else
    private static readonly object Gate = new();
#endif
    private static bool _initialized;

    /// <summary>Initialises PDFium once per process (<c>pypdfium2</c> does this on import).</summary>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            PdfiumView.FPDF_InitLibrary();
            _initialized = true;
        }
    }

    /// <summary>
    /// The text pypdfium2 gives a <c>FPDF_GetLastError</c> code
    /// (<c>pypdfium2.internal.consts.ErrorToStr</c>), which is what ends up in the
    /// "cannot open the PDF (...)" message.
    /// </summary>
    public static string ErrorText(ulong code) => code switch
    {
        0 => "Success",
        1 => "Unknown error",
        2 => "File access error",
        3 => "Data format error",
        4 => "Incorrect password error",
        5 => "Unsupported security scheme error",
        6 => "Page not found or content error",
        _ => $"Unhandled constant {code}",
    };
}
