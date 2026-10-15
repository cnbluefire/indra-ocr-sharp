namespace Indra.Ocr.Imaging;

/// <summary>
/// An owning, tightly packed 8-bit grayscale buffer: what a host project produces (an
/// image-file decode, or a PDF page rasterised by <c>Indra.Ocr.Pdf</c>) and hands to Core
/// as a <see cref="GrayImage"/> view.
/// </summary>
/// <remarks>
/// Rows are stored without padding (<c>Pixels.Length == Width * Height</c>), which is what
/// the Python reference works with: <c>pypdfium2</c> renders with a stride equal to the
/// pixel width and <c>document._render</c> then forces the array contiguous
/// (<c>np.ascontiguousarray</c>), and the image decoders do the same with their output.
/// </remarks>
public sealed class GrayPixels
{
    public GrayPixels(int width, int height)
        : this(width, height, new byte[checked(width * height)])
    {
    }

    public GrayPixels(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"Expected {width * height} pixels for {width}x{height}, got {pixels.Length}.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The raw little-endian gray bytes, row-major, no padding.</summary>
    public byte[] Pixels { get; }

    /// <summary>A borrowed view over <see cref="Pixels"/> for the pipeline.</summary>
    public GrayImage View => new(Width, Height, Pixels);
}
