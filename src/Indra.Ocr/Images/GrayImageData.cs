using Indra.Ocr.Imaging;

namespace Indra.Ocr.Images;

/// <summary>
/// Decoded grayscale pixels that <b>own</b> their buffer, so several of them can be held in a
/// list.
/// </summary>
/// <remarks>
/// <see cref="GrayImage"/> is a <c>ref struct</c> — a view — so a decoder cannot hand back several
/// frames as images: the type cannot be stored on the heap. This is its owning twin: the width,
/// the height, the row stride and the bytes, plus an implicit conversion to the view Core's
/// algorithms take.
/// <para>
/// Decoding a file gives a <c>List&lt;GrayImageData&gt;</c> with one entry per frame — a PNG has
/// one, a multi-page TIFF many — which mirrors <c>cv2.imreadmulti</c>'s list and is what lets the
/// image path decode the whole file up front and then OCR the frames one at a time, exactly like
/// the reference.
/// </para>
/// <para>
/// The pixels are always allocated by the decoder (a frame is colour, the pipeline wants 8-bit
/// gray), so the conversion to <see cref="GrayImage"/> is free: it only wraps the memory this
/// object already keeps alive.
/// </para>
/// </remarks>
public readonly record struct GrayImageData(int Width, int Height, int Stride, ReadOnlyMemory<byte> Data)
{
    /// <summary>Creates a tightly packed frame (<c>stride == width</c>).</summary>
    public GrayImageData(int width, int height, ReadOnlyMemory<byte> data)
        : this(width, height, width, data)
    {
    }

    /// <summary>The pixels as the view Core's algorithms take (no copy, nothing reallocated).</summary>
    public static implicit operator GrayImage(GrayImageData data)
        => new(data.Width, data.Height, data.Stride, data.Data);
}
