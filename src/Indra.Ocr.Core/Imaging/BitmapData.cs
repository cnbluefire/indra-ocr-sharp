namespace Indra.Ocr.Imaging;

/// <summary>
/// A non-owning view over 8-bit single-channel (grayscale) pixels, row-major, with a
/// caller-provided row <see cref="Stride"/> so padded / sub-sampled buffers can be handed
/// to Core without a copy.
/// </summary>
/// <remarks>
/// This is the <b>only</b> image representation Core accepts from the outside. Producing it
/// is the caller's job:
/// <list type="bullet">
///   <item>image files (<c>.png</c>, <c>.jpg</c>, <c>.tif</c>, ...) are decoded by the host
///     projects (Cli / Viewer) with ImageSharp, mirroring
///     <c>cv2.imread(..., IMREAD_GRAYSCALE)</c>;</item>
///   <item>PDF pages are rasterised by <c>Indra.Ocr.Pdf</c>, which asks PDFium for
///     <c>FPDFBitmap_Gray</c> output so no conversion is needed.</item>
/// </list>
/// Because it is a <see cref="ref struct"/> it can never be stored on the heap, which keeps
/// the "borrowed pixels" contract honest.
/// </remarks>
public readonly ref struct BitmapData
{
    /// <summary>
    /// Creates a view. <paramref name="data"/> must hold at least
    /// <c>stride * (height - 1) + width</c> bytes (trailing padding after the last row is
    /// optional).
    /// </summary>
    public BitmapData(int width, int height, int stride, ReadOnlySpan<byte> data)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width) throw new ArgumentOutOfRangeException(nameof(stride), "stride must be >= width");
        int needed = stride * (height - 1) + width;
        if (data.Length < needed)
            throw new ArgumentException($"data holds {data.Length} bytes, need at least {needed}", nameof(data));
        Width = width;
        Height = height;
        Stride = stride;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row (<c>&gt;= Width</c>); never negative.</summary>
    public int Stride { get; }

    /// <summary>The whole borrowed buffer.</summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>Bytes per pixel (always 1: single-channel 8-bit).</summary>
    public int BytesPerPixel => 1;

    /// <summary>The <see cref="Width"/> pixel bytes of row <paramref name="y"/> (padding excluded).</summary>
    public ReadOnlySpan<byte> Row(int y) => Data.Slice(y * Stride, Width);

    /// <summary>Pixel value at column <paramref name="x"/>, row <paramref name="y"/>.</summary>
    public byte this[int y, int x] => Data[y * Stride + x];
}
