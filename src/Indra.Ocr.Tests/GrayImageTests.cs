using Indra.Ocr.Imaging;

namespace Indra.Ocr.Tests;

public class GrayImageTests
{
    [Fact]
    public void Rows_skip_padding()
    {
        const int w = 3, h = 2, stride = 5;
        // Row 0: 1 2 3 | * *     Row 1: 4 5 6 | * *   (padding filled with a sentinel)
        var buffer = new byte[]
        {
            1, 2, 3, 0xFF, 0xFF,
            4, 5, 6, 0xFF, 0xFF,
        };

        var img = new GrayImage(w, h, stride, buffer);
        Assert.Equal(w, img.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(stride, img.Stride);
        Assert.Equal(1, img.BytesPerPixel);
        Assert.Equal(6, img.PixelCount);

        Assert.Equal(new byte[] { 1, 2, 3 }, img.Row(0).ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, img.Row(1).ToArray());
        Assert.Equal(6, img[1, 2]);
        Assert.Equal(4, img[1, 0]);
    }

    [Fact]
    public void Tight_view_defaults_the_stride_to_the_width()
    {
        var img = new GrayImage(3, 2, new byte[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(3, img.Stride);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, img.Data);
    }

    [Fact]
    public void Accepts_a_writable_span()
    {
        var buffer = new byte[6];
        Span<byte> view = buffer;
        var img = new GrayImage(3, 2, view); // Span<byte> binds to the ReadOnlySpan<byte> parameter
        Assert.Equal(3, img.Width);
        Assert.Equal(6, img.Data.Length);
    }

    [Fact]
    public void Slice_is_a_view_over_the_same_buffer()
    {
        var buffer = new byte[]
        {
            1, 2, 3, 0xFF, 0xFF,
            4, 5, 6, 0xFF, 0xFF,
        };
        var img = new GrayImage(3, 2, 5, buffer);
        var sub = img.Slice(1, 1, 2, 1);
        Assert.Equal(2, sub.Width);
        Assert.Equal(1, sub.Height);
        Assert.Equal(5, sub.Stride);
        Assert.Equal(new byte[] { 5, 6 }, sub.Row(0).ToArray());
    }

    [Fact]
    public void Rejects_a_stride_smaller_than_the_width()
    {
        var buffer = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrayImage(4, 2, 3, buffer));
    }

    [Fact]
    public void Rejects_a_buffer_that_is_too_short()
    {
        var buffer = new byte[7]; // need 5*(2-1)+3 = 8
        Assert.Throws<ArgumentException>(() => new GrayImage(3, 2, 5, buffer));
    }
}
