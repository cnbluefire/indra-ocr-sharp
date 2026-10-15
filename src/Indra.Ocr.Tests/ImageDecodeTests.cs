using System.Text.Json;
using Indra.Ocr.Hosting;
using Indra.Ocr.Imaging;

namespace Indra.Ocr.Tests;

public class ImageDecodeTests
{
    private static readonly JsonElement Fx = Fixtures.Load("image_decode.json");

    [Fact]
    public void Grayscale_decoding_matches_the_opencv_reference()
    {
        var dir = Path.Combine(Path.GetTempPath(), "indra_ocr_tests");
        Directory.CreateDirectory(dir);

        foreach (var c in Fx.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            byte[] bytes = Convert.FromBase64String(c.GetProperty("base64").GetString()!);
            // ImageSharp detects the format from the content, so an extension is optional.
            string path = Path.Combine(dir, name + ".bin");
            File.WriteAllBytes(path, bytes);

            var expectedFrames = c.GetProperty("frames").EnumerateArray().ToList();
            var got = ImageFiles.ReadImages(path);

            Assert.Equal(expectedFrames.Count, got.Count);
            for (int f = 0; f < got.Count; f++)
            {
                var expRows = expectedFrames[f].EnumerateArray().ToList();
                Assert.Equal(expRows.Count, got[f].Height);
                Assert.Equal(expRows[0].GetArrayLength(), got[f].Width);
                for (int y = 0; y < got[f].Height; y++)
                {
                    var row = Fixtures.IntArray(expRows[y]);
                    for (int x = 0; x < got[f].Width; x++)
                        Assert.True(row[x] == got[f][y, x],
                            $"{name} frame {f} at ({y},{x}): expected {row[x]} got {got[f][y, x]}");
                }
            }
        }
    }
}
