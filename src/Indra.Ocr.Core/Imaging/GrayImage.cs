namespace Indra.Ocr.Imaging;

/// <summary>
/// An 8-bit single-channel (grayscale) image, row-major. Mirrors the numpy arrays the
/// Python pipeline passes around (<c>np.uint8</c>, shape <c>[H, W]</c>).
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Length <see cref="Width"/>*<see cref="Height"/>, index <c>y*Width + x</c>.</summary>
    public byte[] Data { get; }

    public GrayImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Data = new byte[width * height];
    }

    public GrayImage(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (data.Length != width * height) throw new ArgumentException("data length");
        Width = width;
        Height = height;
        Data = data;
    }

    public byte this[int y, int x]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public Span<byte> Row(int y) => Data.AsSpan(y * Width, Width);

    public GrayImage Clone() => new(Width, Height, (byte[])Data.Clone());

    /// <summary>Zero-copy view over this image (its rows are tightly packed).</summary>
    public BitmapData AsBitmap() => new(Width, Height, Width, Data);

    /// <summary>
    /// Copies a borrowed (possibly padded) view into a tightly packed, owning image — the
    /// bridge from the <see cref="BitmapData"/> input contract to Core's working type.
    /// </summary>
    public static GrayImage From(BitmapData bitmap)
    {
        var image = new GrayImage(bitmap.Width, bitmap.Height);
        for (int y = 0; y < bitmap.Height; y++)
            bitmap.Row(y).CopyTo(image.Row(y));
        return image;
    }
}
