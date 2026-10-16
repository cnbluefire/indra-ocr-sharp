using System.Runtime.InteropServices;
using Indra.Ocr.Imaging;
using Indra.Ocr.Document;
using PDFiumCore;

namespace Indra.Ocr.Pdf;

/// <summary>
/// One open PDF page. Every member is the managed twin of a pypdfium2 call the reference
/// makes, with the same numbers: the page size is <c>FPDF_GetPageWidthF</c>/<c>HeightF</c>
/// (float, widened), the text layer is <c>FPDFText_*</c>, the render is
/// <c>page.render(scale=..., grayscale=True)</c> and the affine is
/// <c>FPDF_PageToDevice</c> sampled at 1000 device units per point.
/// </summary>
public sealed class PdfPage : ITextPage
{
    // PDFium constants (fpdfview.h / fpdf_edit.h); the managed wrapper renames but does
    // not otherwise expose them.
    private const int BitmapGray = 1; // FPDFBitmap_Gray
    private const int RenderAnnot = 0x01; // FPDF_ANNOT
    private const int RenderGrayscale = 0x08; // FPDF_GRAYSCALE
    private const int ObjectText = 1; // FPDF_PAGEOBJ_TEXT
    private const int ObjectForm = 5; // FPDF_PAGEOBJ_FORM

    /// <summary>Device units per point, as <c>textlayer._device_affine</c> uses.</summary>
    private const double DeviceUnitsPerPoint = 1000.0;

    private FpdfPageT? _page;

    internal PdfPage(FpdfPageT page) => _page = page;

    public double Width => fpdfview.FPDF_GetPageWidthF(_page);

    public double Height => fpdfview.FPDF_GetPageHeightF(_page);

    public IReadOnlyList<PageChar> ReadChars()
    {
        ThrowIfDisposed();
        FpdfTextpageT? textPage = fpdf_text.FPDFTextLoadPage(_page);
        if (textPage is null || textPage.__Instance == IntPtr.Zero)
        {
            throw new TextPageError("Failed to load text page.");
        }

        try
        {
            int count = fpdf_text.FPDFTextCountChars(textPage);
            var chars = new List<PageChar>(Math.Max(count, 0));
            for (int i = 0; i < count; i++)
            {
                uint code = fpdf_text.FPDFTextGetUnicode(textPage, i);
                double left = 0, right = 0, bottom = 0, top = 0;
                bool hasBox = fpdf_text.FPDFTextGetCharBox(
                    textPage, i, ref left, ref right, ref bottom, ref top) != 0;
                float angle = fpdf_text.FPDFTextGetCharAngle(textPage, i);
                chars.Add(new PageChar(code, angle, hasBox, left, right, bottom, top));
            }

            return chars;
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(textPage);
        }
    }

    public double[,] DeviceAffine()
    {
        ThrowIfDisposed();
        int sizeX = (int)Math.Round(Width * DeviceUnitsPerPoint, MidpointRounding.ToEven);
        int sizeY = (int)Math.Round(Height * DeviceUnitsPerPoint, MidpointRounding.ToEven);

        (double X, double Y) Device(double x, double y)
        {
            int dx = 0, dy = 0;
            fpdfview.FPDF_PageToDevice(_page, 0, 0, sizeX, sizeY, 0, x, y, ref dx, ref dy);
            return (dx / DeviceUnitsPerPoint, dy / DeviceUnitsPerPoint);
        }

        (double X, double Y) origin = Device(0.0, 0.0);
        (double X, double Y) unitX = Device(1.0, 0.0);
        (double X, double Y) unitY = Device(0.0, 1.0);

        // np.stack([ex - o, ey - o, o], 1): columns are the x basis, the y basis, origin.
        var affine = new double[2, 3];
        affine[0, 0] = unitX.X - origin.X;
        affine[1, 0] = unitX.Y - origin.Y;
        affine[0, 1] = unitY.X - origin.X;
        affine[1, 1] = unitY.Y - origin.Y;
        affine[0, 2] = origin.X;
        affine[1, 2] = origin.Y;
        return affine;
    }

    public double InvisibleTextFraction()
    {
        ThrowIfDisposed();
        int total = 0;
        int invisible = 0;
        WalkObjects(maxDepth: 4, (obj, type) =>
        {
            if (type != ObjectText)
            {
                return;
            }

            total++;
            if (fpdf_edit.FPDFTextObjGetTextRenderMode(obj) == FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_INVISIBLE)
            {
                invisible++;
            }
        });

        return total == 0 ? 0.0 : (double)invisible / total;
    }

    public GrayPixels Render(double scale)
    {
        ThrowIfDisposed();
        // page.render(scale=..., grayscale=True) with no rotation and no crop:
        //   src_width = ceil(width * scale), src_height = ceil(height * scale),
        //   a packed FPDFBitmap_Gray buffer, filled white, then
        //   FPDF_RenderPageBitmap(bitmap, page, 0, 0, src_width, src_height, 0, flags).
        int width = (int)Math.Ceiling(Width * scale);
        int height = (int)Math.Ceiling(Height * scale);
        var pixels = new byte[width * height];
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            FpdfBitmapT? bitmap = fpdfview.FPDFBitmapCreateEx(
                width, height, BitmapGray, pin.AddrOfPinnedObject(), width);
            if (bitmap is null || bitmap.__Instance == IntPtr.Zero)
            {
                throw new TextPageError("Failed to create bitmap.");
            }

            try
            {
                // fill_color=(255, 255, 255, 255) -> 0xFFFFFFFF whatever the byte order.
                if (fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL) == 0)
                {
                    throw new TextPageError("Failed to fill bitmap rectangle.");
                }

                fpdfview.FPDF_RenderPageBitmap(
                    bitmap, _page, 0, 0, width, height, 0, RenderAnnot | RenderGrayscale);

                // PDFium honours the stride it was given, so the rows are already packed;
                // the copy is only a guard for a PDFium that pads.
                int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                if (stride == width)
                {
                    return new GrayPixels(width, height, pixels);
                }

                var packed = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(pixels, y * stride, packed, y * width, width);
                }

                return new GrayPixels(width, height, packed);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    public void Dispose()
    {
        if (_page is not null)
        {
            fpdfview.FPDF_ClosePage(_page);
            _page = null;
        }
    }

    /// <summary>
    /// Walks the page's object tree the way <c>page.get_objects(max_depth=...)</c> does:
    /// objects in order, form objects descended into after themselves.
    /// </summary>
    private void WalkObjects(int maxDepth, Action<FpdfPageobjectT, int> visit)
        => WalkObjects(null, 0, maxDepth, visit);

    private void WalkObjects(FpdfPageobjectT? form, int level, int maxDepth, Action<FpdfPageobjectT, int> visit)
    {
        int count = form is null
            ? fpdf_edit.FPDFPageCountObjects(_page)
            : fpdf_edit.FPDFFormObjCountObjects(form);
        if (count < 0)
        {
            throw new TextPageError("Failed to get number of pageobjects.");
        }

        for (int i = 0; i < count; i++)
        {
            FpdfPageobjectT? obj = form is null
                ? fpdf_edit.FPDFPageGetObject(_page, i)
                : fpdf_edit.FPDFFormObjGetObject(form, (ulong)i);
            if (obj is null || obj.__Instance == IntPtr.Zero)
            {
                throw new TextPageError("Failed to get pageobject.");
            }

            int type = fpdf_edit.FPDFPageObjGetType(obj);
            visit(obj, type);
            if (type == ObjectForm && level < maxDepth - 1)
            {
                WalkObjects(obj, level + 1, maxDepth, visit);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_page is null, this);
}
