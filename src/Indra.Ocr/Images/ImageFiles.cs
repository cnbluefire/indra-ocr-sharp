using Indra.Ocr.Imaging;
using Indra.Ocr.Pipeline;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Indra.Ocr.Images;

/// <summary>
/// Decodes image <i>files</i> to grayscale with ImageSharp, reproducing how the Python
/// pipeline reads them (<c>cv2.imreadmulti</c> falling back to
/// <c>cv2.imread(path, IMREAD_GRAYSCALE)</c>).
/// </summary>
/// <remarks>
/// This is the encoded-image half of the input space, so it lives in <c>Indra.Ocr</c> — the
/// project that owns image codecs — never in <c>Indra.Ocr.Core</c>, which deliberately has no
/// image-codec dependency and only consumes <see cref="GrayImage"/>. The PDF decoder is the
/// sibling project <c>Indra.Ocr.Pdf</c>; both hand Core the same pixels and never reference each
/// other.
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

    /// <summary>
    /// Decodes every frame of an image file (a PNG has one, a multi-page TIFF many), in order
    /// (<c>pipeline.read_images</c>).
    /// </summary>
    /// <remarks>
    /// A file that decodes to nothing — or cannot be decoded at all — is an
    /// <see cref="InputError"/> with the reference's wording, exactly as
    /// <c>cv2.imreadmulti</c>/<c>cv2.imread</c> returning nothing does there. (<c>cv2</c> reports
    /// those as a false return value; ImageSharp throws, so the failures are translated here.)
    /// </remarks>
    public static List<GrayImageData> ReadImages(string path)
    {
        var frames = new List<GrayImageData>();
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Load(stream, out var formula);
            foreach (ImageFrame<Rgba32> frame in image.Frames)
            {
                frames.Add(ToGray(frame, formula));
            }
        }
        catch (Exception e) when (e is UnknownImageFormatException
                                      or InvalidImageContentException
                                      or NotSupportedException
                                      or IOException)
        {
            throw new InputError($"{path}: not a PDF or a readable image");
        }

        if (frames.Count == 0)
            throw new InputError($"{path}: not a PDF or a readable image");
        return frames;
    }

    /// <summary>Decodes the first frame of an image held in memory (the viewer upload path).</summary>
    public static GrayImageData ReadImage(ReadOnlySpan<byte> bytes)
    {
        using var image = Load(bytes, out var formula);
        if (image.Frames.Count == 0) throw new InvalidDataException("no image frames");
        return ToGray(image.Frames[0], formula);
    }

    /// <summary>Encodes a grayscale image as JPEG (viewer previews; quality 82 like Python).</summary>
    public static byte[] EncodeJpeg(GrayImage image, int quality = 82)
    {
        using var img = Image.LoadPixelData<L8>(image.Data, image.Width, image.Height);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    /// <summary>Loads an image from a stream and picks the RGB-&gt;gray formula from its format.</summary>
    /// <remarks>
    /// NOTE: JPEG is decoded by libjpeg in OpenCV and by ImageSharp here, so colour JPEGs
    /// cannot be bit-exact; they fall back to the cvtColor coefficients.
    /// </remarks>
    private static Image<Rgba32> Load(Stream stream, out GrayFormula formula)
    {
        // The decoders seek; buffer a stream that cannot.
        MemoryStream? buffered = null;
        if (!stream.CanSeek)
        {
            buffered = new MemoryStream();
            stream.CopyTo(buffered);
            stream = buffered;
        }

        try
        {
            return Oriented(Image.Load<Rgba32>(stream, out IImageFormat format), format, out formula);
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <summary>
    /// Loads an image straight out of the caller's bytes. <c>Image.Load</c> pins the span and
    /// reads it through an <c>UnmanagedMemoryStream</c>, so the buffer is not copied.
    /// </summary>
    /// <inheritdoc cref="Load(Stream, out GrayFormula)"/>
    private static Image<Rgba32> Load(ReadOnlySpan<byte> data, out GrayFormula formula)
        => Oriented(Image.Load<Rgba32>(data, out IImageFormat format), format, out formula);

    /// <summary>
    /// Records which decoder read the image and applies the EXIF orientation the way OpenCV's
    /// <c>imread</c> does by default.
    /// </summary>
    private static Image<Rgba32> Oriented(Image<Rgba32> image, IImageFormat? format, out GrayFormula formula)
    {
        formula = FormulaFor(format?.Name);
        image.Mutate(x => x.AutoOrient());
        return image;
    }

    /// <summary>Converts one frame to a tightly packed grayscale buffer it owns.</summary>
    private static GrayImageData ToGray(ImageFrame<Rgba32> frame, GrayFormula formula)
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
        return new GrayImageData(w, h, gray);
    }
}
