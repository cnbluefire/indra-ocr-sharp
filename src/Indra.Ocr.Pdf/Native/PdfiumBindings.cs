// The P/Invoke surface of PDFium: the member names are the C API's, so every one of them can be
// checked against fpdfview.h / fpdf_text.h / fpdf_edit.h (they ship inside bblanchon.PDFium.Win32)
// without any renaming in between.
//
// The port binds PDFium itself rather than going through a managed wrapper.  The obvious wrapper,
// PDFiumCore, has no netstandard2.0 asset -- its lowest is netstandard2.1, which .NET Framework
// cannot reference at all -- while the three libraries multi-target net10.0;netstandard2.0
// (docs/335-multitarget-netstandard20.md).  One binding for both targets also means the two halves
// of the port cannot drift apart, and the native library is the same physical build either way:
// the bblanchon.PDFium.{Linux,macOS,Win32} packages (156.0.8076), which is what the wrapper pulled
// in.  PDFium renders straight to grayscale (FPDFBitmap_Gray), which is why this project -- and not
// Core -- is the one that touches it.
//
// Only the members the port calls are declared.  Nothing here is public: the consumers are
// PdfDocument, PdfPage and Pdfium, and the public surface of the project stays what it was.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Indra.Ocr.Pdf.Native;

/// <summary>A PDFium document handle (<c>FPDF_DOCUMENT</c>).</summary>
internal sealed class PdfiumDocument
{
    public IntPtr Handle { get; set; }
}

/// <summary>A PDFium page handle (<c>FPDF_PAGE</c>).</summary>
internal sealed class PdfiumPage
{
    public IntPtr Handle { get; set; }
}

/// <summary>A PDFium text-page handle (<c>FPDF_TEXTPAGE</c>).</summary>
internal sealed class PdfiumTextPage
{
    public IntPtr Handle { get; set; }
}

/// <summary>A PDFium page-object handle (<c>FPDF_PAGEOBJECT</c>).</summary>
internal sealed class PdfiumPageObject
{
    public IntPtr Handle { get; set; }
}

/// <summary>A PDFium bitmap handle (<c>FPDF_BITMAP</c>).</summary>
internal sealed class PdfiumBitmap
{
    public IntPtr Handle { get; set; }
}

/// <summary>PDF text rendering mode (<c>FPDF_TEXT_RENDERMODE</c>, fpdf_edit.h).</summary>
internal enum TextRenderMode
{
    Unknown = -1,
    Fill = 0,
    Stroke = 1,
    FillStroke = 2,
    Invisible = 3,
    FillClip = 4,
    StrokeClip = 5,
    FillStrokeClip = 6,
    Clip = 7,
}

/// <summary>
/// Where the native library every DllImport here names (<c>pdfium</c>) comes from.
/// </summary>
/// <remarks>
/// On .NET Core and .NET 5+ nothing has to happen: the host resolves the bare name through the
/// packages' <c>runtimes\&lt;rid&gt;\native\pdfium.dll</c>.  On .NET Framework the Win32 package's
/// targets copy that file next to the executable (which the search finds) *and* into <c>x64\</c>,
/// <c>x86\</c>, <c>arm64\</c> and the other platforms' sub-directories.  The root copy is enough
/// for the normal case; <see cref="EnsureLoaded"/> covers a host that ended up with only the
/// sub-directory copies, by loading this process's architecture one by full path first -- the
/// loader then hands that already-mapped module to the later name-based DllImport, no matter which
/// directory it came from.  Nothing is preloaded when the file is not there.
/// </remarks>
internal static class PdfiumNative
{
    /// <summary>The library name of every DllImport in this file.</summary>
    internal const string Library = "pdfium";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryW(string fileName);

    /// <summary>Makes an architecture-subdirectory copy of the library loadable, once.</summary>
    internal static void EnsureLoaded()
    {
        // The subdirectory layout is the Win32 package's, so this is Windows-only work.
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return;
        }

        string candidate = Path.Combine(
            AppContext.BaseDirectory, IntPtr.Size == 8 ? "x64" : "x86", Library + ".dll");
        if (File.Exists(candidate))
        {
            // A failure is left for the DllImport to report: it knows which entry point it needed
            // and says so in its exception.
            LoadLibraryW(candidate);
        }
    }
}

/// <summary>The <c>fpdfview.h</c> functions the port calls (including the bitmaps').</summary>
internal static class PdfiumView
{
    /// <summary>
    /// The first PDFium call any path through this project makes
    /// (<c>Pdfium.EnsureInitialized</c>), which makes this the place to get the native library
    /// loadable before an entry point is resolved.
    /// </summary>
    static PdfiumView() => PdfiumNative.EnsureLoaded();

    /// <summary><c>FPDF_InitLibrary</c>: process-wide, once (<see cref="Pdfium"/> gates it).</summary>
    internal static void FPDF_InitLibrary() => Raw.FPDF_InitLibrary();

    /// <summary><c>FPDF_LoadMemDocument64</c>: the caller keeps the buffer alive.</summary>
    internal static PdfiumDocument? FPDF_LoadMemDocument64(IntPtr data_buf, ulong size, string? password)
        => WrapDocument(Raw.FPDF_LoadMemDocument64(data_buf, size, password));

    /// <summary><c>FPDF_GetPageCount</c>.</summary>
    internal static int FPDF_GetPageCount(PdfiumDocument? document) => Raw.FPDF_GetPageCount(Handle(document));

    /// <summary><c>FPDF_GetLastError</c>.</summary>
    internal static ulong FPDF_GetLastError() => Raw.FPDF_GetLastError();

    /// <summary><c>FPDF_LoadPage</c> (0-based).</summary>
    internal static PdfiumPage? FPDF_LoadPage(PdfiumDocument? document, int page_index)
        => WrapPage(Raw.FPDF_LoadPage(Handle(document), page_index));

    /// <summary><c>FPDF_CloseDocument</c>.</summary>
    internal static void FPDF_CloseDocument(PdfiumDocument? document)
        => Raw.FPDF_CloseDocument(Handle(document));

    /// <summary><c>FPDF_ClosePage</c>.</summary>
    internal static void FPDF_ClosePage(PdfiumPage? page) => Raw.FPDF_ClosePage(Handle(page));

    /// <summary><c>FPDF_GetPageWidthF</c> (points).</summary>
    internal static float FPDF_GetPageWidthF(PdfiumPage? page) => Raw.FPDF_GetPageWidthF(Handle(page));

    /// <summary><c>FPDF_GetPageHeightF</c> (points).</summary>
    internal static float FPDF_GetPageHeightF(PdfiumPage? page) => Raw.FPDF_GetPageHeightF(Handle(page));

    /// <summary><c>FPDF_PageToDevice</c> (returns <c>FPDF_BOOL</c>), for the text-layer affine.</summary>
    internal static int FPDF_PageToDevice(PdfiumPage? page, int start_x, int start_y, int size_x, int size_y,
                                          int rotate, double page_x, double page_y,
                                          ref int device_x, ref int device_y)
        => Raw.FPDF_PageToDevice(Handle(page), start_x, start_y, size_x, size_y, rotate, page_x, page_y,
                                 ref device_x, ref device_y);

    /// <summary><c>FPDFBitmap_CreateEx</c>, over a caller-owned buffer.</summary>
    internal static PdfiumBitmap? FPDFBitmap_CreateEx(int width, int height, int format, IntPtr first_scan, int stride)
        => WrapBitmap(Raw.FPDFBitmap_CreateEx(width, height, format, first_scan, stride));

    /// <summary><c>FPDFBitmap_FillRect</c> (returns <c>FPDF_BOOL</c>).</summary>
    internal static int FPDFBitmap_FillRect(PdfiumBitmap? bitmap, int left, int top, int width, int height, ulong color)
        => Raw.FPDFBitmap_FillRect(Handle(bitmap), left, top, width, height, color);

    /// <summary><c>FPDF_RenderPageBitmap</c>.</summary>
    internal static void FPDF_RenderPageBitmap(PdfiumBitmap? bitmap, PdfiumPage? page, int start_x, int start_y,
                                               int size_x, int size_y, int rotate, int flags)
        => Raw.FPDF_RenderPageBitmap(Handle(bitmap), Handle(page), start_x, start_y, size_x, size_y, rotate, flags);

    /// <summary><c>FPDFBitmap_GetStride</c>.</summary>
    internal static int FPDFBitmap_GetStride(PdfiumBitmap? bitmap) => Raw.FPDFBitmap_GetStride(Handle(bitmap));

    /// <summary><c>FPDFBitmap_Destroy</c>.</summary>
    internal static void FPDFBitmap_Destroy(PdfiumBitmap? bitmap) => Raw.FPDFBitmap_Destroy(Handle(bitmap));

    private static IntPtr Handle(PdfiumDocument? document) => document?.Handle ?? IntPtr.Zero;

    private static IntPtr Handle(PdfiumPage? page) => page?.Handle ?? IntPtr.Zero;

    private static IntPtr Handle(PdfiumBitmap? bitmap) => bitmap?.Handle ?? IntPtr.Zero;

    /// <summary>A PDFium handle is a null reference when the native pointer is null.</summary>
    private static PdfiumDocument? WrapDocument(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : new PdfiumDocument { Handle = pointer };

    private static PdfiumPage? WrapPage(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : new PdfiumPage { Handle = pointer };

    private static PdfiumBitmap? WrapBitmap(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : new PdfiumBitmap { Handle = pointer };

    /// <summary>The DllImports themselves, on the raw pointers.</summary>
    private static class Raw
    {
        // FPDF_CALLCONV is __stdcall only when PDFium is built with FPDFSDK_EXPORTS; the shipped
        // DLL is not, so every entry point is __cdecl.  The data types are the headers':
        // FPDF_DWORD is `unsigned long` (32-bit on Windows, 64-bit elsewhere) and FPDF_BOOL is int.
        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_InitLibrary")]
        internal static extern void FPDF_InitLibrary();

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_LoadMemDocument64")]
        internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr data_buf, ulong size, string? password);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_GetPageCount")]
        internal static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_GetLastError")]
        internal static extern ulong FPDF_GetLastError();

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_LoadPage")]
        internal static extern IntPtr FPDF_LoadPage(IntPtr document, int page_index);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_CloseDocument")]
        internal static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_ClosePage")]
        internal static extern void FPDF_ClosePage(IntPtr page);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_GetPageWidthF")]
        internal static extern float FPDF_GetPageWidthF(IntPtr page);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_GetPageHeightF")]
        internal static extern float FPDF_GetPageHeightF(IntPtr page);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_PageToDevice")]
        internal static extern int FPDF_PageToDevice(IntPtr page, int start_x, int start_y, int size_x, int size_y,
                                                     int rotate, double page_x, double page_y,
                                                     ref int device_x, ref int device_y);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFBitmap_CreateEx")]
        internal static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr first_scan,
                                                          int stride);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFBitmap_FillRect")]
        internal static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height,
                                                       ulong color);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDF_RenderPageBitmap")]
        internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int start_x, int start_y,
                                                          int size_x, int size_y, int rotate, int flags);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFBitmap_GetStride")]
        internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFBitmap_Destroy")]
        internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    }
}

/// <summary>The <c>fpdf_text.h</c> functions the port calls.</summary>
internal static class PdfiumText
{
    /// <summary><c>FPDFText_LoadPage</c>, closed with <c>FPDFText_ClosePage</c>.</summary>
    internal static PdfiumTextPage? FPDFText_LoadPage(PdfiumPage? page)
    {
        IntPtr pointer = Raw.FPDFText_LoadPage(page?.Handle ?? IntPtr.Zero);
        return pointer == IntPtr.Zero ? null : new PdfiumTextPage { Handle = pointer };
    }

    /// <summary><c>FPDFText_CountChars</c>.</summary>
    internal static int FPDFText_CountChars(PdfiumTextPage? text_page)
        => Raw.FPDFText_CountChars(Handle(text_page));

    /// <summary><c>FPDFText_GetUnicode</c>: one UTF-16 code unit.</summary>
    internal static uint FPDFText_GetUnicode(PdfiumTextPage? text_page, int index)
        => Raw.FPDFText_GetUnicode(Handle(text_page), index);

    /// <summary>
    /// <c>FPDFText_GetCharBox</c> (returns <c>FPDF_BOOL</c>): the character's box in page space, in
    /// the header's own order -- left, right, bottom, top.
    /// </summary>
    internal static int FPDFText_GetCharBox(PdfiumTextPage? text_page, int index,
                                            ref double left, ref double right,
                                            ref double bottom, ref double top)
        => Raw.FPDFText_GetCharBox(Handle(text_page), index, ref left, ref right, ref bottom, ref top);

    /// <summary><c>FPDFText_GetCharAngle</c> (radians).</summary>
    internal static float FPDFText_GetCharAngle(PdfiumTextPage? text_page, int index)
        => Raw.FPDFText_GetCharAngle(Handle(text_page), index);

    /// <summary><c>FPDFText_ClosePage</c>.</summary>
    internal static void FPDFText_ClosePage(PdfiumTextPage? text_page) => Raw.FPDFText_ClosePage(Handle(text_page));

    private static IntPtr Handle(PdfiumTextPage? text_page) => text_page?.Handle ?? IntPtr.Zero;

    /// <summary>The DllImports themselves, on the raw pointers.</summary>
    private static class Raw
    {
        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFText_LoadPage")]
        internal static extern IntPtr FPDFText_LoadPage(IntPtr page);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFText_CountChars")]
        internal static extern int FPDFText_CountChars(IntPtr text_page);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFText_GetUnicode")]
        internal static extern uint FPDFText_GetUnicode(IntPtr text_page, int index);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFText_GetCharBox")]
        internal static extern int FPDFText_GetCharBox(IntPtr text_page, int index,
                                                       ref double left, ref double right,
                                                       ref double bottom, ref double top);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFText_GetCharAngle")]
        internal static extern float FPDFText_GetCharAngle(IntPtr text_page, int index);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFText_ClosePage")]
        internal static extern void FPDFText_ClosePage(IntPtr text_page);
    }
}

/// <summary>The <c>fpdf_edit.h</c> functions the port calls (the page-object walk).</summary>
internal static class PdfiumEdit
{
    /// <summary><c>FPDFPage_CountObjects</c>.</summary>
    internal static int FPDFPage_CountObjects(PdfiumPage? page) => Raw.FPDFPage_CountObjects(Handle(page));

    /// <summary><c>FPDFPage_GetObject</c> (0-based, in content order).</summary>
    internal static PdfiumPageObject? FPDFPage_GetObject(PdfiumPage? page, int index)
        => Wrap(Raw.FPDFPage_GetObject(Handle(page), index));

    /// <summary><c>FPDFPageObj_GetType</c> (<c>FPDF_PAGEOBJ_*</c>).</summary>
    internal static int FPDFPageObj_GetType(PdfiumPageObject? page_object)
        => Raw.FPDFPageObj_GetType(Handle(page_object));

    /// <summary><c>FPDFTextObj_GetTextRenderMode</c>.</summary>
    internal static TextRenderMode FPDFTextObj_GetTextRenderMode(PdfiumPageObject? text)
        => Raw.FPDFTextObj_GetTextRenderMode(Handle(text));

    /// <summary><c>FPDFFormObj_CountObjects</c> (a form's own page objects).</summary>
    internal static int FPDFFormObj_CountObjects(PdfiumPageObject? form_object)
        => Raw.FPDFFormObj_CountObjects(Handle(form_object));

    /// <summary><c>FPDFFormObj_GetObject</c> (its index is an <c>unsigned long</c>).</summary>
    internal static PdfiumPageObject? FPDFFormObj_GetObject(PdfiumPageObject? form_object, ulong index)
        => Wrap(Raw.FPDFFormObj_GetObject(Handle(form_object), index));

    private static IntPtr Handle(PdfiumPage? page) => page?.Handle ?? IntPtr.Zero;

    private static IntPtr Handle(PdfiumPageObject? page_object) => page_object?.Handle ?? IntPtr.Zero;

    /// <summary>A PDFium handle is a null reference when the native pointer is null.</summary>
    private static PdfiumPageObject? Wrap(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : new PdfiumPageObject { Handle = pointer };

    /// <summary>The DllImports themselves, on the raw pointers.</summary>
    private static class Raw
    {
        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFPage_CountObjects")]
        internal static extern int FPDFPage_CountObjects(IntPtr page);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFPage_GetObject")]
        internal static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFPageObj_GetType")]
        internal static extern int FPDFPageObj_GetType(IntPtr page_object);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFTextObj_GetTextRenderMode")]
        internal static extern TextRenderMode FPDFTextObj_GetTextRenderMode(IntPtr text);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFFormObj_CountObjects")]
        internal static extern int FPDFFormObj_CountObjects(IntPtr form_object);

        [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl,
                   ExactSpelling = true, EntryPoint = "FPDFFormObj_GetObject")]
        internal static extern IntPtr FPDFFormObj_GetObject(IntPtr form_object, ulong index);
    }
}
