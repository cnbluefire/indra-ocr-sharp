using Indra.Ocr.Imaging;

namespace Indra.Ocr.Tests;

public class BitmapDataTests
{
    [Fact]
    public void Rows_skip_padding_and_copy_into_a_tight_image()
    {
        const int w = 3, h = 2, stride = 5;
        // Row 0: 1 2 3 | * *     Row 1: 4 5 6 | * *   (padding filled with a sentinel)
        var buffer = new byte[]
        {
            1, 2, 3, 0xFF, 0xFF,
            4, 5, 6, 0xFF, 0xFF,
        };

        var bmp = new BitmapData(w, h, stride, buffer);
        Assert.Equal(w, bmp.Width);
        Assert.Equal(h, bmp.Height);
        Assert.Equal(stride, bmp.Stride);
        Assert.Equal(1, bmp.BytesPerPixel);

        Assert.Equal(new byte[] { 1, 2, 3 }, bmp.Row(0).ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, bmp.Row(1).ToArray());
        Assert.Equal(6, bmp[1, 2]);
        Assert.Equal(4, bmp[1, 0]);

        var img = GrayImage.From(bmp);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, img.Data);
        Assert.Equal(stride, bmp.Stride);
        // The image is tightly packed, so its own view has stride == width.
        Assert.Equal(w, img.AsBitmap().Stride);
    }

    [Fact]
    public void Rejects_a_stride_smaller_than_the_width()
    {
        var buffer = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitmapData(4, 2, 3, buffer));
    }

    [Fact]
    public void Rejects_a_buffer_that_is_too_short()
    {
        var buffer = new byte[7]; // need 5*(2-1)+3 = 8
        Assert.Throws<ArgumentException>(() => new BitmapData(3, 2, 5, buffer));
    }
}
