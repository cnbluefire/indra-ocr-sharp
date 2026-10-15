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
/// <see cref="GrayImage"/>. The file is shared by linking it into each host project.
/// </remarks>
public static class ImageFiles
{
    /// <summary>
    /// Receives one decoded page. <see cref="GrayImage"/> is a <c>ref struct</c> view, so it
    /// cannot be stored: the callback must consume the pixels before returning (images are
    /// produced and used one at a time, nothing is buffered).
    /// </summary>
    public delegate void GrayImageConsumer(GrayImage image);

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

    /// <summary>
    /// Decodes every frame of an image file (all pages of a multi-page TIFF), handing each to
    /// <paramref name="consume"/> in order. Only one page's pixels are alive at a time.
    /// </summary>
    public static void ReadImages(string path, GrayImageConsumer consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var stream = File.OpenRead(path);
        using var image = Load(stream, out var formula);
        for (int fi = 0; fi < image.Frames.Count; fi++)
        {
            var (gray, w, h) = ToGray(image.Frames[fi], formula);
            consume(new GrayImage(w, h, gray));
        }
    }

    /// <summary>Decodes the first frame of an image held in memory (the viewer upload path).</summary>
    public static GrayImage ReadImage(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var image = Load(stream, out var formula);
        if (image.Frames.Count == 0) throw new InvalidDataException("no image frames");
        var (gray, w, h) = ToGray(image.Frames[0], formula);
        return new GrayImage(w, h, gray);
    }

    /// <summary>Encodes a grayscale image as JPEG (viewer previews; quality 82 like Python).</summary>
    public static byte[] EncodeJpeg(GrayImage image, int quality = 82)
    {
        using var img = Image.LoadPixelData<L8>(image.Data, image.Width, image.Height);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    /// <summary>Loads the whole file and picks the RGB-&gt;gray formula from its format.</summary>
    /// <remarks>
    /// NOTE: JPEG is decoded by libjpeg in OpenCV and by ImageSharp here, so colour JPEGs
    /// cannot be bit-exact; they fall back to the cvtColor coefficients.
    /// </remarks>
    private static Image<Rgba32> Load(Stream stream, out GrayFormula formula)
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
            formula = FormulaFor(format?.Name);
            stream.Position = 0;
            // OpenCV's imread honours EXIF orientation by default; mirror that.
            var image = Image.Load<Rgba32>(stream);
            image.Mutate(x => x.AutoOrient());
            return image;
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <summary>Converts one frame to a tightly packed grayscale buffer.</summary>
    private static (byte[] Gray, int Width, int Height) ToGray(ImageFrame<Rgba32> frame, GrayFormula formula)
    {
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
        return (gray, w, h);
    }
}
