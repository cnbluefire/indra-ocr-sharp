namespace Indra.Ocr.Imaging;

/// <summary>
/// A view over 8-bit single-channel (grayscale) pixels, row-major, with a caller-provided row
/// <see cref="Stride"/> so padded / cropped buffers can be handed to Core without a copy.
/// Mirrors a numpy <c>np.uint8</c> array of shape <c>[H, W]</c>.
/// </summary>
/// <remarks>
/// This is the <b>only</b> image type. Core never writes through it — the pixels belong to the
/// caller — so <see cref="Data"/> is a <see cref="ReadOnlySpan{T}"/>.
/// <para>
/// The pixels come from one of two places, and the image carries whichever it was built with:
/// <list type="bullet">
///   <item>a <b>borrowed</b> buffer the caller keeps alive (a padded crop, a scanline of a bigger
///     buffer): construct from a <see cref="ReadOnlySpan{T}"/> — nothing is copied and nothing is
///     owned;</item>
///   <item><b>memory the image itself keeps alive</b> (a decoded image file, a page rasterised by
///     <c>Indra.Ocr.Pdf</c>): construct from a <see cref="ReadOnlyMemory{T}"/> or a
///     <see cref="byte"/> array — the image holds the heap object, so it stays valid after the
///     producer's frame returns.</item>
/// </list>
/// <see cref="Data"/> answers with the span of whichever of the two the image carries. Note that
/// the returned span does not by itself keep a memory-backed image's array alive: hold the
/// <see cref="GrayImage"/> while reading <see cref="Data"/>.
/// </para>
/// Because it is a <see cref="ref struct"/> it can never be stored on the heap or in a collection
/// (and so can be neither a generic type argument nor a lambda capture): images are produced and
/// consumed one at a time.
/// </remarks>
public readonly ref struct GrayImage
{
    private readonly ReadOnlySpan<byte> _span;
    private readonly ReadOnlyMemory<byte> _memory;

    /// <summary>
    /// Creates a view over a borrowed buffer. <paramref name="data"/> must hold at least
    /// <c>stride * (height - 1) + width</c> bytes (trailing padding after the last row is
    /// optional); it must outlive the image.
    /// </summary>
    public GrayImage(int width, int height, int stride, ReadOnlySpan<byte> data)
    {
        Validate(width, height, stride, data.Length);
        Width = width;
        Height = height;
        Stride = stride;
        _span = data;
        _memory = default;
    }

    /// <summary>
    /// Creates a view over memory the image keeps alive, so the pixels stay valid for as long as
    /// the image does.
    /// </summary>
    public GrayImage(int width, int height, int stride, ReadOnlyMemory<byte> data)
    {
        Validate(width, height, stride, data.Length);
        Width = width;
        Height = height;
        Stride = stride;
        _span = default;
        _memory = data;
    }

    /// <summary>Creates a tightly packed view (<c>stride == width</c>) over a borrowed buffer.</summary>
    public GrayImage(int width, int height, ReadOnlySpan<byte> data)
        : this(width, height, width, data)
    {
    }

    /// <summary>Creates a tightly packed view over memory the image keeps alive.</summary>
    public GrayImage(int width, int height, ReadOnlyMemory<byte> data)
        : this(width, height, width, data)
    {
    }

    /// <summary>
    /// Creates a tightly packed view over a byte array, which the image keeps alive (the
    /// overload the producers use — a decode, a render, a rotation).
    /// </summary>
    public GrayImage(int width, int height, byte[] data)
        : this(width, height, width, Memory(data))
    {
    }

    /// <inheritdoc cref="GrayImage(int, int, byte[])"/>
    public GrayImage(int width, int height, int stride, byte[] data)
        : this(width, height, stride, Memory(data))
    {
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row (<c>&gt;= Width</c>); never negative.</summary>
    public int Stride { get; }

    /// <summary>The whole buffer: the owned memory when there is one, else the borrowed span.</summary>
    public ReadOnlySpan<byte> Data => _memory.IsEmpty ? _span : _memory.Span;

    /// <summary>Bytes per pixel (always 1: single-channel 8-bit).</summary>
    public int BytesPerPixel => 1;

    /// <summary>Number of pixels (<c>Width * Height</c>), ignoring row padding.</summary>
    public int PixelCount => Width * Height;

    /// <summary>The <see cref="Width"/> pixel bytes of row <paramref name="y"/> (padding excluded).</summary>
    public ReadOnlySpan<byte> Row(int y) => Data.Slice(y * Stride, Width);

    /// <summary>Pixel value at column <paramref name="x"/>, row <paramref name="y"/>.</summary>
    public byte this[int y, int x] => Data[y * Stride + x];

    /// <summary>A sub-view at (<paramref name="x"/>, <paramref name="y"/>); no copy is made.</summary>
    public GrayImage Slice(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(width));
        int offset = y * Stride + x;
        return _memory.IsEmpty
            ? new GrayImage(width, height, Stride, _span.Slice(offset))
            : new GrayImage(width, height, Stride, _memory.Slice(offset));
    }

    private static ReadOnlyMemory<byte> Memory(byte[] data)
    {
        Guard.NotNull(data);
        return data;
    }

    private static void Validate(int width, int height, int stride, int length)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width) throw new ArgumentOutOfRangeException(nameof(stride), "stride must be >= width");
        int needed = stride * (height - 1) + width;
        if (length < needed)
            throw new ArgumentException($"data holds {length} bytes, need at least {needed}", "data");
    }
}
