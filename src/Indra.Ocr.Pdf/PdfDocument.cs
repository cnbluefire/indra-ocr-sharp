using System.Runtime.InteropServices;
using Indra.Ocr.Pdf.Native;

namespace Indra.Ocr.Pdf;

/// <summary>
/// An open PDF document backed by PDFium. This project is the <b>only</b> place that
/// depends on the PDFium NuGet package, keeping <c>Indra.Ocr.Core</c> independent of it.
/// </summary>
/// <remarks>
/// PDFium can rasterise straight to 8-bit grayscale (<c>FPDFBitmap_Gray</c>), which is what
/// the Python reference asks pypdfium2 for
/// (<c>page.render(scale=dpi/72, grayscale=True)</c>), so rendered pages need no decoding
/// or colour conversion.
/// <para>
/// The file is read here and handed to <c>FPDF_LoadMemDocument64</c> rather than opened by
/// path with <c>FPDF_LoadDocument</c>: pypdfium2 passes the path as explicit UTF-8, while
/// the binding marshals a <c>string</c> in the process's ANSI code page, which would
/// break non-ASCII paths on Windows. The bytes stay pinned for the document's lifetime
/// (PDFium does not copy them), exactly as pypdfium2 keeps its input buffer alive.
/// </para>
/// </remarks>
public sealed class PdfDocument : IDisposable
{
    private readonly GCHandle _pin;
    private PdfiumDocument? _document;
    private bool _disposed;

    private PdfDocument(byte[] data, string? password)
    {
        Pdfium.EnsureInitialized();
        _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            _document = PdfiumView.FPDF_LoadMemDocument64(_pin.AddrOfPinnedObject(), (ulong)data.Length, password);
            // pypdfium2 checks the page count, not the handle: a null handle fails the same
            // way, and FPDF_GetPageCount tolerates it.
            if (_document is null || _document.Handle == IntPtr.Zero || PdfiumView.FPDF_GetPageCount(_document) < 1)
            {
                ulong code = PdfiumView.FPDF_GetLastError();
                throw new PdfiumError($"Failed to load document (PDFium: {Pdfium.ErrorText(code)}).", code);
            }
        }
        catch
        {
            _pin.Free();
            throw;
        }
    }

    /// <summary>Opens a PDF file; <paramref name="password"/> unlocks an encrypted one.</summary>
    public static PdfDocument Open(string path, string? password = null)
        => new(File.ReadAllBytes(path), password);

    /// <summary>Opens a PDF held in memory (what the tests use: no file needed).</summary>
    public static PdfDocument Open(ReadOnlySpan<byte> data, string? password = null)
        => new(data.ToArray(), password);

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return PdfiumView.FPDF_GetPageCount(_document);
        }
    }

    public (double Width, double Height) PageSize(int index)
    {
        using PdfPage page = LoadPage(index);
        return (page.Width, page.Height);
    }

    /// <summary>Loads one page (0-based); dispose it when done.</summary>
    public PdfPage LoadPage(int index)
    {
        ThrowIfDisposed();
        PdfiumPage? raw = PdfiumView.FPDF_LoadPage(_document, index);
        if (raw is null || raw.Handle == IntPtr.Zero)
        {
            throw new PdfiumError("Failed to load page.");
        }

        return new PdfPage(raw);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_document is not null)
        {
            PdfiumView.FPDF_CloseDocument(_document);
            _document = null;
        }

        _pin.Free();
    }

    private void ThrowIfDisposed() => Guard.NotDisposed(_disposed, this);
}
