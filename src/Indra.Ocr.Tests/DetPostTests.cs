using System.Text.Json;
using Indra.Ocr.Det;
using Indra.Ocr.Imaging;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for the LineNet pre/post-processing port —
/// <see cref="DetPost.PageToInput"/> and <see cref="DetPost.Decode"/>
/// (<c>indra_ocr/det_post.py</c>, see <c>docs/070-det-post.md</c>).
/// </summary>
/// <remarks>
/// The fixture's decoder cases feed the port the output of the <b>real</b> <c>det.onnx</c>
/// (both synthetic pages and crops of <c>temp/inputs/*.png</c>), so the logits carry the
/// statistics of a genuine forward pass; a no-model machine still runs every comparison.
/// </remarks>
public class DetPostTests
{
    private static readonly JsonElement Fx = Fixtures.Load("det_post.json");

    [Fact]
    public void Page_to_input_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("cases").EnumerateArray())
        {
            string tag = c.GetProperty("name").GetString()!;
            var image = B64.ZBytes(c.GetProperty("image").GetString()!);
            int h = c.GetProperty("h").GetInt32(), w = c.GetProperty("w").GetInt32();
            int ph = c.GetProperty("ph").GetInt32(), pw = c.GetProperty("pw").GetInt32();
            int nh = c.GetProperty("nh").GetInt32(), nw = c.GetProperty("nw").GetInt32();
            var gray = B64.ZBytes(c.GetProperty("gray").GetString()!);

            var ink = DetPost.PageToInput(new GrayImage(w, h, image), c.GetProperty("long_side").GetInt32(),
                                          out int gotH, out int gotW, out double scale);
            Assert.Equal(ph, gotH);
            Assert.Equal(pw, gotW);
            Assert.Equal(c.GetProperty("scale").GetDouble(), scale);
            Assert.Equal(ph * pw, ink.Length);

            // 1 - gray/255 in float32 over the resized page; the right/bottom padding to a
            // multiple of 32 stays 0 = white.
            int bad = -1;
            for (int y = 0; y < ph && bad < 0; y++)
                for (int x = 0; x < pw; x++)
                {
                    float want = y < nh && x < nw ? 1f - gray[y * nw + x] / 255f : 0f;
                    if (ink[y * pw + x] != want) { bad = y * pw + x; break; }
                }
            if (bad >= 0)
            {
                float want = bad / pw < nh && bad % pw < nw ? 1f - gray[(bad / pw) * nw + bad % pw] / 255f : 0f;
                Assert.Fail($"{tag}: ink[{bad / pw},{bad % pw}] = {ink[bad]:R}, expected {want:R}");
            }

            if (c.TryGetProperty("ink_map", out var full))
            {
                var expected = B64.ZFloats(full.GetString()!);
                AssertSameFloats(expected, ink, tag + " ink_map");
            }
            cases++;
        }
        Assert.True(cases >= 15, $"only {cases} page_to_input cases");
    }

    [Fact]
    public void Decode_matches_the_reference()
    {
        int boxes = 0;
        foreach (var c in Fx.GetProperty("cases").EnumerateArray()) boxes += DecodeCase(c);
        // Hand-built logit tensors pin the branches a real page rarely hits.
        int manual = 0;
        foreach (var c in Fx.GetProperty("decode_only").EnumerateArray())
        {
            boxes += DecodeCase(c);
            manual++;
        }
        int pages = Fx.GetProperty("cases").GetArrayLength();
        Assert.True(pages >= 15, $"only {pages} model-logit pages");
        Assert.True(manual >= 6, $"only {manual} hand-built cases");
        Assert.True(boxes >= 40, $"only {boxes} boxes in the fixture");
    }

    [Fact]
    public void Decode_defaults_match_the_explicit_arguments()
    {
        // The pipeline calls decode(out) with the defaults; the fixture passes them explicitly.
        var c = Fx.GetProperty("cases")[0];
        int oh = c.GetProperty("oh").GetInt32(), ow = c.GetProperty("ow").GetInt32();
        var logits = B64.ZFloats(c.GetProperty("logits").GetString()!);
        AssertSameFloats(DetPost.Decode(logits, oh, ow),
                         DetPost.Decode(logits, oh, ow, 0.5f, 2, 0.0, 0.0),
                         "defaults");
    }

    [Fact]
    public void Decode_returns_nothing_without_ink_above_the_threshold()
    {
        // sigmoid(0) is exactly 0.5 and the comparison is strict, so a zero logit is not ink.
        Assert.Empty(DetPost.Decode(new float[3 * 4 * 4], 4, 4));

        // A single above-threshold pixel is one 1-px component, dropped by min_area = 2.
        var logits = new float[3 * 4 * 4];
        logits[2 * 4 + 1] = 10f;                     // core channel, y = 2, x = 1
        Assert.Empty(DetPost.Decode(logits, 4, 4));

        // ... and min_area = 1 keeps it (one box = five floats).
        Assert.Equal(5, DetPost.Decode(logits, 4, 4, minArea: 1).Length);
    }

    private static int DecodeCase(JsonElement c)
    {
        string tag = c.GetProperty("name").GetString()!;
        int oh = c.GetProperty("oh").GetInt32(), ow = c.GetProperty("ow").GetInt32();
        var logits = B64.ZFloats(c.GetProperty("logits").GetString()!);
        var expected = B64.ZFloats(c.GetProperty("boxes").GetString()!);
        Assert.Equal(3 * oh * ow, logits.Length);

        var actual = DetPost.Decode(logits, oh, ow,
                                    c.GetProperty("thresh").GetSingle(),
                                    c.GetProperty("min_area").GetInt32(),
                                    c.GetProperty("grow_y").GetDouble(),
                                    c.GetProperty("grow_x").GetDouble());
        AssertSameFloats(expected, actual, tag);
        Assert.Equal(0, actual.Length % 5);
        return actual.Length / 5;
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
