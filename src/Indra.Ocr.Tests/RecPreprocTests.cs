using System.Text.Json;
using Indra.Ocr.Imaging;
using Indra.Ocr.Rec;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for the recognition preprocessing port —
/// <see cref="RecPreproc.CropLine"/>, <see cref="RecPreproc.PadBatch"/> and
/// <see cref="RecPreproc.WidthBatches"/> (<c>indra_ocr/rec_preproc.py</c>, see
/// <c>docs/080-rec-preproc.md</c>).
/// </summary>
public class RecPreprocTests
{
    private static readonly JsonElement Fx = Fixtures.Load("rec_preproc.json");

    [Fact]
    public void Crop_line_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("crop").EnumerateArray())
        {
            string tag = c.GetProperty("name").GetString()!;
            var page = Fx.GetProperty("pages").GetProperty(c.GetProperty("page").GetString()!);
            int h = page.GetProperty("h").GetInt32(), w = page.GetProperty("w").GetInt32();
            var gray = B64.ZBytes(page.GetProperty("gray").GetString()!);
            var box = Fixtures.FloatArray(c.GetProperty("box"));

            var ink = RecPreproc.CropLine(new GrayImage(w, h, gray), box, out int outWidth,
                                          c.GetProperty("height").GetInt32(),
                                          c.GetProperty("pad_y").GetDouble(),
                                          c.GetProperty("pad_x").GetDouble(),
                                          c.GetProperty("max_width").GetInt32());
            Assert.Equal(c.GetProperty("out_w").GetInt32(), outWidth);
            Assert.Equal(c.GetProperty("height").GetInt32() * outWidth, ink.Length);
            AssertSameFloats(B64.ZFloats(c.GetProperty("ink").GetString()!), ink, tag);
            cases++;
        }
        Assert.True(cases >= 70, $"only {cases} crop cases");
    }

    [Fact]
    public void Pad_batch_pads_to_a_multiple_and_reports_the_widths()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("pad").EnumerateArray())
        {
            string tag = c.GetProperty("name").GetString()!;
            int height = c.GetProperty("height").GetInt32();
            int multiple = c.GetProperty("multiple").GetInt32();
            var widthsIn = Fixtures.IntArray(c.GetProperty("widths"));
            int expectedWidth = c.GetProperty("expected_width").GetInt32();
            var expectedWidths = Fixtures.IntArray(c.GetProperty("expected_widths"));

            var lines = new List<float[]>();
            foreach (int wd in widthsIn) lines.Add(Fill(height * wd, 0.5f));

            var widths = new long[lines.Count];
            var batch = RecPreproc.PadBatch(lines, height, widths, out int width, multiple);

            Assert.Equal(expectedWidth, width);
            Assert.Equal(expectedWidths.Select(v => (long)v), widths);
            Assert.Equal(0, expectedWidth % multiple);
            Assert.Equal(lines.Count * height * width, batch.Length);

            var expected = new float[batch.Length];
            for (int i = 0; i < lines.Count; i++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < expectedWidths[i]; x++)
                        expected[(i * height + y) * width + x] = 0.5f;
            AssertSameFloats(expected, batch, tag);
            cases++;
        }
        Assert.True(cases >= 4, $"only {cases} pad cases");
    }

    [Fact]
    public void Width_batches_matches_the_reference()
    {
        int cases = 0, batched = 0;
        foreach (var c in Fx.GetProperty("width_batches").EnumerateArray())
        {
            string tag = c.GetProperty("name").GetString()!;
            var widths = Fixtures.IntArray(c.GetProperty("widths"));
            var batches = RecPreproc.WidthBatches(widths,
                                                  c.GetProperty("max_batch").GetInt32(),
                                                  c.GetProperty("max_ratio").GetDouble(),
                                                  c.GetProperty("min_width").GetInt32(),
                                                  c.GetProperty("max_area").GetInt32());
            var expected = new List<int[]>();
            foreach (var b in c.GetProperty("expected").EnumerateArray())
                expected.Add(Fixtures.IntArray(b));

            Assert.Equal(expected.Count, batches.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], batches[i]);
                batched += batches[i].Length;
            }
            cases++;
        }
        Assert.True(cases >= 11, $"only {cases} batching cases");
        Assert.True(batched >= 100, $"only {batched} batched lines");
    }

    [Fact]
    public void Width_batches_keeps_equal_widths_in_input_order()
    {
        // Python's sorted() is stable, and the port has to be too: equal widths must not
        // reshuffle (a batch's first line fixes its width window).
        var batches = RecPreproc.WidthBatches(new[] { 90, 90, 90, 90 }, maxBatch: 2);
        Assert.Equal(new[] { 0, 1 }, batches[0]);
        Assert.Equal(new[] { 2, 3 }, batches[1]);
    }

    private static float[] Fill(int length, float value)
    {
        var a = new float[length];
        Array.Fill(a, value);
        return a;
    }

    private static void AssertSameFloats(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string tag)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i]) continue;
            if (float.IsNaN(expected[i]) && float.IsNaN(actual[i])) continue;
            Assert.Fail($"{tag}[{i}]: {actual[i]:R} (0x{BitConverter.SingleToUInt32Bits(actual[i]):X8}), " +
                        $"expected {expected[i]:R} (0x{BitConverter.SingleToUInt32Bits(expected[i]):X8})");
        }
    }
}
