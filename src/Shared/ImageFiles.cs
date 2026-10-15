using Indra.Ocr.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Indra.Ocr.Hosting;

/// <summary>
/// Decodes image <i>files</i> to grayscale with ImageSharp, reproducing how the Python
/// pipeline reads them (<c>cv2.imreadmulti</c> falling back to
/// <c>cv2.imread(path, IMREAD_GRAYSCALE)</c>).
/// </summary>
/// <remarks>
/// This lives in the host projects (Cli / Viewer) — never in <c>Indra.Ocr.Core</c>, which
/// deliberately has no image-codec dependency and only consumes
/// <see cref="BitmapData"/>. The file is shared by linking it into each host project.
/// </remarks>
public static class ImageFiles
{
    /// <summary>
    /// Which fixed-point RGB-&gt;Gray conversion OpenCV applies, i.e. which decoder produced
    /// the pixels. OpenCV 5.0.0 does <b>not</b> use one formula for
    /// <c>cv2.imread(..., IMREAD_GRAYSCALE)</c>: the conversion happens inside the codec.
    /// </summary>
    /// <remarks>
    /// Measured against cv2 5.0.0 (0 mismatches over 1,000,000 random RGB triples each):
    /// <list type="bullet">
    ///   <item><see cref="Png"/>: libpng <c>png_set_rgb_to_gray</c> non-gamma path —
    ///     coefficients <c>(9797, 19234, 3737)</c>, plain <b>truncating</b> shift.</item>
    ///   <item><see cref="BmpTiff"/>: BMP and TIFF decoders — <c>(9798, 19234, 3736)</c>,
    ///     <b>rounded</b>.</item>
    ///   <item><see cref="Cvt"/>: <c>cv2.cvtColor(COLOR_BGR2GRAY)</c>
    ///     (<c>RGB2Gray&lt;uchar&gt;</c>) — <c>(9798, 19235, 3735)</c>, <b>rounded</b>.</item>
    /// </list>
    /// All three sets sum to 2^15, so a grey source (R=G=B) round-trips exactly.
    /// </remarks>
    public enum GrayFormula
    {
        /// <summary>libpng RGB-&gt;grey (PNG), truncating.</summary>
        Png,

        /// <summary>OpenCV BMP/TIFF decoders, rounded.</summary>
        BmpTiff,

        /// <summary><c>cv2.cvtColor(BGR2GRAY)</c> (default), rounded.</summary>
        Cvt,
    }

    private const int PngR = 9797, PngG = 19234, PngB = 3737;
    private const int BmpR = 9798, BmpG = 19234, BmpB = 3736;
    private const int CvtR = 9798, CvtG = 19235, CvtB = 3735;
    private const int Round = 1 << 14;

    /// <summary>OpenCV's RGB-&gt;Gray conversion for the given decoder (alpha ignored).</summary>
    public static byte RgbToGray(byte r, byte g, byte b, GrayFormula formula) => formula switch
    {
        GrayFormula.Png => (byte)((r * PngR + g * PngG + b * PngB) >> 15),
        GrayFormula.BmpTiff => (byte)((r * BmpR + g * BmpG + b * BmpB + Round) >> 15),
        _ => (byte)((r * CvtR + g * CvtG + b * CvtB + Round) >> 15),
    };

    /// <summary>Maps an ImageSharp decoder name to OpenCV's matching conversion.</summary>
    internal static GrayFormula FormulaFor(string? formatName) => formatName switch
    {
        "PNG" => GrayFormula.Png,
        "BMP" => GrayFormula.BmpTiff,
        "TIFF" => GrayFormula.BmpTiff,
        _ => GrayFormula.Cvt,
    };

    /// <summary>All grayscale pages of an image file (every frame of a multi-page TIFF).</summary>
    public static List<GrayImage> ReadImages(string path)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream);
    }

    /// <summary>Decodes a single image from bytes (the viewer upload path).</summary>
    public static GrayImage ReadImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var pages = Decode(ms);
        return pages.Count > 0 ? pages[0] : throw new InvalidDataException("no image frames");
    }

    /// <summary>Encodes a grayscale image as JPEG (viewer previews; quality 82 like Python).</summary>
    public static byte[] EncodeJpeg(GrayImage image, int quality = 82)
    {
        using var img = Image.LoadPixelData<L8>(image.Data, image.Width, image.Height);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    private static List<GrayImage> Decode(Stream stream)
    {
        // Detecting the format needs a seekable stream; buffer if the caller gave us one.
        MemoryStream? buffered = null;
        if (!stream.CanSeek)
        {
            buffered = new MemoryStream();
            stream.CopyTo(buffered);
            stream = buffered;
        }

        try
        {
            stream.Position = 0;
            var format = Image.DetectFormat(stream);
            stream.Position = 0;
            return Decode(stream, FormulaFor(format?.Name));
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    private static List<GrayImage> Decode(Stream stream, GrayFormula formula)
    {
        using var image = Image.Load<Rgba32>(stream);

        // OpenCV's imread honours EXIF orientation by default; mirror that.
        image.Mutate(x => x.AutoOrient());

        // NOTE: JPEG is decoded by libjpeg in OpenCV and by ImageSharp here, so colour JPEGs
        // cannot be bit-exact; they fall back to the cvtColor coefficients.
        var pages = new List<GrayImage>(image.Frames.Count);
        for (int fi = 0; fi < image.Frames.Count; fi++)
        {
            var frame = image.Frames[fi];
            int w = frame.Width, h = frame.Height;
            var gray = new byte[w * h];
            var buffer = frame.PixelBuffer;
            for (int y = 0; y < h; y++)
            {
                var row = buffer.DangerousGetRowSpan(y);
                int off = y * w;
                for (int x = 0; x < w; x++)
                {
                    var p = row[x];
                    gray[off + x] = RgbToGray(p.R, p.G, p.B, formula);
                }
            }
            pages.Add(new GrayImage(w, h, gray));
        }
        return pages;
    }
}
