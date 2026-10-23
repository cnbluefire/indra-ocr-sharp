using System.Runtime.InteropServices;
using Indra.Ocr.Imaging;
using Indra.Ocr.Text;
using Indra.Ocr.Pdf.Native;

namespace Indra.Ocr.Pdf;

/// <summary>
/// One open PDF page. Every member is the managed twin of a pypdfium2 call the reference
/// makes, with the same numbers: the page size is <c>FPDF_GetPageWidthF</c>/<c>HeightF</c>
/// (float, widened), the text layer is <c>FPDFText_*</c>, the render is
/// <c>page.render(scale=..., grayscale=True)</c> and the affine is
/// <c>FPDF_PageToDevice</c> sampled at 1000 device units per point.
/// </summary>
/// <remarks>
/// <see cref="ExtractRuns"/> is the PDFium half of <c>textlayer.extract_runs</c>: it reads the
/// characters, their angles and boxes off the page and hands back the shared
/// <see cref="TextLine"/>s and <see cref="TextLayerStats"/>; the verdict on them
/// (<see cref="TextLayerReader"/>) lives in Core.
/// </remarks>
public sealed class PdfPage : IDisposable
{
    // PDFium constants (fpdfview.h / fpdf_edit.h); the binding does not name them.
    private const int BitmapGray = 1; // FPDFBitmap_Gray
    private const int RenderAnnot = 0x01; // FPDF_ANNOT
    private const int RenderGrayscale = 0x08; // FPDF_GRAYSCALE
    private const int ObjectText = 1; // FPDF_PAGEOBJ_TEXT
    private const int ObjectForm = 5; // FPDF_PAGEOBJ_FORM

    /// <summary>Device units per point, as <c>textlayer._device_affine</c> uses.</summary>
    private const double DeviceUnitsPerPoint = 1000.0;

    private PdfiumPage? _page;

    internal PdfPage(PdfiumPage page) => _page = page;

    public double Width => PdfiumView.FPDF_GetPageWidthF(_page);

    public double Height => PdfiumView.FPDF_GetPageHeightF(_page);

    public IReadOnlyList<PageChar> ReadChars()
    {
        ThrowIfDisposed();
        PdfiumTextPage? textPage = PdfiumText.FPDFText_LoadPage(_page);
        if (textPage is null || textPage.Handle == IntPtr.Zero)
        {
            throw new PdfiumError("Failed to load text page.");
        }

        try
        {
            int count = PdfiumText.FPDFText_CountChars(textPage);
            var chars = new List<PageChar>(Math.Max(count, 0));
            for (int i = 0; i < count; i++)
            {
                uint code = PdfiumText.FPDFText_GetUnicode(textPage, i);
                double left = 0, right = 0, bottom = 0, top = 0;
                bool hasBox = PdfiumText.FPDFText_GetCharBox(
                    textPage, i, ref left, ref right, ref bottom, ref top) != 0;
                float angle = PdfiumText.FPDFText_GetCharAngle(textPage, i);
                chars.Add(new PageChar(code, angle, hasBox, left, right, bottom, top));
            }

            return chars;
        }
        finally
        {
            PdfiumText.FPDFText_ClosePage(textPage);
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
            PdfiumView.FPDF_PageToDevice(_page, 0, 0, sizeX, sizeY, 0, x, y, ref dx, ref dy);
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
            if (PdfiumEdit.FPDFTextObj_GetTextRenderMode(obj) == TextRenderMode.Invisible)
            {
                invisible++;
            }
        });

        return total == 0 ? 0.0 : (double)invisible / total;
    }

    /// <summary>
    /// The upright text runs of this page, in content order, with the statistics
    /// (<c>textlayer.extract_runs</c>): the PDFium reading half of the module.
    /// </summary>
    public (List<TextLine> Runs, TextLayerStats Stats) ExtractRuns(int turn = 0, int? frame = null)
    {
        int outputFrame = frame ?? turn;
        IReadOnlyList<PageChar> source = ReadChars();
        // The linear part of the page-to-frame map: a character is upright when its baseline
        // runs left to right once mapped through it (any /Rotate, any mirroring).
        double[,] affine = DeviceAffine();

        var upright = new Dictionary<double, int?>();
        var bad = new Dictionary<string, int>();
        var chars = new List<string>();
        var quarters = new List<int>();
        var rawBoxes = new List<double[]>();
        var breaks = new List<int>(); // run ends after these positions
        var quarterCounts = new int[4];
        int nChars = 0, nRotated = 0, nBad = 0;

        for (int i = 0; i < source.Count; i++)
        {
            PageChar pc = source[i];
            string c = Chr(pc.Code);
            if (c is "\r" or "\n")
            {
                breaks.Add(chars.Count);
                continue;
            }

            if (c == "\x02")
            {
                c = "-"; // pdfium's mark for a hyphen at a line break
            }

            if (!UnicodeText.IsSpace(c))
            {
                nChars++;
                if (!bad.TryGetValue(c, out int k))
                {
                    k = TextLayerReader.BadCharCount(c);
                    bad[c] = k;
                }

                nBad += k;
                double a = pc.Angle;
                if (!upright.TryGetValue(a, out int? q))
                {
                    double cos = Math.Cos(a), sin = Math.Sin(a);
                    if (turn == 0 && outputFrame == 0)
                    {
                        double oldX = affine[0, 0] * cos + affine[0, 1] * sin;
                        double oldY = affine[1, 0] * cos + affine[1, 1] * sin;
                        if (oldX > 0 && Math.Abs(oldY) <= 1e-3 * Math.Max(Math.Abs(oldX), 1e-9)) q = 0;
                        else
                        {
                            double dx = affine[0, 0] * cos - affine[0, 1] * sin;
                            double dy = -affine[1, 0] * cos + affine[1, 1] * sin;
                            q = Quarter(dx, dy);
                        }
                    }
                    else
                    {
                        double dx = affine[0, 0] * cos - affine[0, 1] * sin;
                        double dy = -affine[1, 0] * cos + affine[1, 1] * sin;
                        q = Quarter(dx, dy);
                    }
                    upright[a] = q;
                }

                if (q is int absoluteQuarter) quarterCounts[absoluteQuarter]++;

                if (q != turn)
                {
                    nRotated++; // not upright on the rendered page
                    breaks.Add(chars.Count);
                    continue;
                }
                if (q is null)
                {
                    breaks.Add(chars.Count);
                    continue;
                }
                quarters.Add(((int)q - turn + 4) % 4);
            }
            else quarters.Add(quarters.Count == 0 ? 0 : quarters[^1]);

            chars.Add(c);
            if (!pc.HasBox)
            {
                throw new PdfiumError("Failed to get charbox.");
            }

            rawBoxes.Add([pc.Left, pc.Bottom, pc.Right, pc.Top]); // l, b, r, t
        }

        TextLayerStats stats = new() { Chars = nChars, Rotated = nRotated, Bad = nBad,
            Quarters = quarterCounts, Turn = turn, Frame = outputFrame };
        if (chars.Count == 0)
        {
            return ([], stats);
        }

        // Page space -> rendered frame, all characters at once: the four corners of each box
        // (l,b), (r,b), (l,t), (r,t), then their bounding box.
        int m = chars.Count;
        var boxes = new double[m][];
        var visible = new bool[m];
        int offPage = 0;
        double pageWidth = Width, pageHeight = Height;
        for (int i = 0; i < m; i++)
        {
            double l = rawBoxes[i][0], b = rawBoxes[i][1], r = rawBoxes[i][2], t = rawBoxes[i][3];
            double x0 = affine[0, 0] * l + affine[0, 1] * b + affine[0, 2];
            double x1 = affine[0, 0] * r + affine[0, 1] * b + affine[0, 2];
            double x2 = affine[0, 0] * l + affine[0, 1] * t + affine[0, 2];
            double x3 = affine[0, 0] * r + affine[0, 1] * t + affine[0, 2];
            double y0 = affine[1, 0] * l + affine[1, 1] * b + affine[1, 2];
            double y1 = affine[1, 0] * r + affine[1, 1] * b + affine[1, 2];
            double y2 = affine[1, 0] * l + affine[1, 1] * t + affine[1, 2];
            double y3 = affine[1, 0] * r + affine[1, 1] * t + affine[1, 2];
            var box = new[]
            {
                Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
                Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
                Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)),
                Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)),
            };
            boxes[i] = box;
            if (outputFrame != 0)
                boxes[i] = TurnBox(box, pageWidth, pageHeight, outputFrame);

            // Characters outside the rendered frame (beyond a crop box) are not on the page:
            // they are dropped, not read.
            double cx = (box[0] + box[2]) / 2, cy = (box[1] + box[3]) / 2;
            visible[i] = cx >= 0 && cx <= pageWidth && cy >= 0 && cy <= pageHeight;
            if (!visible[i])
            {
                offPage++;
            }
        }

        stats = stats with { OffPage = offPage };

        // A run is pdfium's own segment: the breaks it emits, plus the end of the text.
        var cuts = new List<int>(breaks) { m };
        for (int i = 1; i < quarters.Count; i++) if (quarters[i] != quarters[i - 1]) cuts.Add(i);
        cuts.Sort();
        var runs = new List<TextLine>();
        int start = 0;
        foreach (int end in cuts)
        {
            int runStart = start;
            var seg = new List<TextChar>();
            for (int i = start; i < end; i++)
            {
                if (visible[i])
                {
                    seg.Add(new TextChar(chars[i], boxes[i]));
                }
            }

            start = end;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            bool anyInk = false;
            foreach (TextChar sc in seg)
            {
                if (UnicodeText.IsSpace(sc.Character))
                {
                    continue;
                }

                anyInk = true;
                minX = Math.Min(minX, sc.Bbox[0]);
                minY = Math.Min(minY, sc.Bbox[1]);
                maxX = Math.Max(maxX, sc.Bbox[2]);
                maxY = Math.Max(maxY, sc.Bbox[3]);
            }

            if (!anyInk)
            {
                continue;
            }

            var text = new System.Text.StringBuilder();
            foreach (TextChar sc in seg)
            {
                text.Append(sc.Character);
            }

            int q = runStart < quarters.Count ? quarters[runStart] : 0;
            runs.Add(new TextLine([minX, minY, maxX, maxY], UnicodeText.CollapseSpaces(text.ToString()),
                                  seg, q, runs.Count));
        }

        return (runs, stats);
    }

    private static int? Quarter(double x, double y)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y), m = Math.Max(Math.Max(ax, ay), 1e-9);
        if (Math.Min(ax, ay) > 1e-3 * m) return null;
        if (ax >= ay) return x > 0 ? 0 : 2;
        return y > 0 ? 1 : 3;
    }

    private static double[] TurnBox(double[] b, double width, double height, int turn) => (turn % 4) switch
    {
        1 => [height - b[3], b[0], height - b[1], b[2]],
        2 => [width - b[2], height - b[3], width - b[0], height - b[1]],
        3 => [b[1], width - b[2], b[3], width - b[0]],
        _ => b,
    };

    /// <summary>
    /// Rasterises the page (<c>page.render(scale=..., grayscale=True)</c>). The returned image
    /// owns its pixels, so it outlives this call and the page.
    /// </summary>
    public GrayImage Render(double scale)
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
            PdfiumBitmap? bitmap = PdfiumView.FPDFBitmap_CreateEx(
                width, height, BitmapGray, pin.AddrOfPinnedObject(), width);
            if (bitmap is null || bitmap.Handle == IntPtr.Zero)
            {
                throw new PdfiumError("Failed to create bitmap.");
            }

            try
            {
                // fill_color=(255, 255, 255, 255) -> 0xFFFFFFFF whatever the byte order.
                if (PdfiumView.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL) == 0)
                {
                    throw new PdfiumError("Failed to fill bitmap rectangle.");
                }

                PdfiumView.FPDF_RenderPageBitmap(
                    bitmap, _page, 0, 0, width, height, 0, RenderAnnot | RenderGrayscale);

                // PDFium honours the stride it was given, so the rows are already packed;
                // the copy is only a guard for a PDFium that pads.
                int stride = PdfiumView.FPDFBitmap_GetStride(bitmap);
                if (stride == width)
                {
                    return new GrayImage(width, height, pixels);
                }

                var packed = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(pixels, y * stride, packed, y * width, width);
                }

                return new GrayImage(width, height, packed);
            }
            finally
            {
                PdfiumView.FPDFBitmap_Destroy(bitmap);
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
            PdfiumView.FPDF_ClosePage(_page);
            _page = null;
        }
    }

    /// <summary>
    /// Python's <c>chr</c>: one code unit, or a surrogate pair for an astral code point.
    /// <c>FPDFText_GetUnicode</c> hands out UTF-16 code units, so lone surrogates are normal.
    /// </summary>
    private static string Chr(uint code)
        => code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32((int)code);

    /// <summary>
    /// Walks the page's object tree the way <c>page.get_objects(max_depth=...)</c> does:
    /// objects in order, form objects descended into after themselves.
    /// </summary>
    private void WalkObjects(int maxDepth, Action<PdfiumPageObject, int> visit)
        => WalkObjects(null, 0, maxDepth, visit);

    private void WalkObjects(PdfiumPageObject? form, int level, int maxDepth, Action<PdfiumPageObject, int> visit)
    {
        int count = form is null
            ? PdfiumEdit.FPDFPage_CountObjects(_page)
            : PdfiumEdit.FPDFFormObj_CountObjects(form);
        if (count < 0)
        {
            throw new PdfiumError("Failed to get number of pageobjects.");
        }

        for (int i = 0; i < count; i++)
        {
            PdfiumPageObject? obj = form is null
                ? PdfiumEdit.FPDFPage_GetObject(_page, i)
                : PdfiumEdit.FPDFFormObj_GetObject(form, (ulong)i);
            if (obj is null || obj.Handle == IntPtr.Zero)
            {
                throw new PdfiumError("Failed to get pageobject.");
            }

            int type = PdfiumEdit.FPDFPageObj_GetType(obj);
            visit(obj, type);
            if (type == ObjectForm && level < maxDepth - 1)
            {
                WalkObjects(obj, level + 1, maxDepth, visit);
            }
        }
    }

    private void ThrowIfDisposed() => Guard.NotDisposed(_page is null, this);
}
