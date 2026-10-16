using System.Text;
using System.Text.Json;
using Indra.Ocr.Imaging;
using Indra.Ocr.Pdf;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Step 150: the text-layer policy (<see cref="TextLayerReader"/>) against the
/// <c>textlayer</c> section of <c>fixtures/pdf.json</c>, which the Python reference
/// (<c>indra_ocr.textlayer</c>) wrote for every sample page.
/// </summary>
/// <remarks>
/// The PDFium surface itself is step 140's; these tests only run the policy over it, so a
/// divergence here is a porting bug, not a library difference.
/// </remarks>
public class TextLayerTests
{
    private static readonly JsonElement Fx = Fixtures.Load("pdf.json");
    private static readonly double CheckScale = Fx.GetProperty("check_scale").GetDouble();

    private static IEnumerable<(string Name, JsonElement Doc)> Documents()
    {
        foreach (var d in Fx.GetProperty("documents").EnumerateArray())
        {
            yield return (d.GetProperty("name").GetString()!, d);
        }
    }

    private static PdfDocument Open(JsonElement doc) =>
        PdfDocument.Open(
            B64.Bytes(doc.GetProperty("pdf").GetString()!),
            doc.GetProperty("password").ValueKind == JsonValueKind.Null
                ? null
                : doc.GetProperty("password").GetString());

    /// <summary>Exact equality, but signed zero must match too (0.0 vs -0.0 swaps bits).</summary>
    private static void Same(double expected, double actual) =>
        Assert.True(
            expected.Equals(actual) &&
            (expected != 0 || BitConverter.DoubleToInt64Bits(expected) ==
                              BitConverter.DoubleToInt64Bits(actual)),
            $"expected {expected:R}, got {actual:R}");

    /// <summary>Compares the keys the reference actually recorded (its stats dicts differ).</summary>
    private static void AssertStats(JsonElement want, TextLayerStats got)
    {
        foreach (var p in want.EnumerateObject())
        {
            switch (p.Name)
            {
                case "chars":
                    Assert.Equal(p.Value.GetInt32(), got.Chars);
                    break;
                case "rotated":
                    Assert.Equal(p.Value.GetInt32(), got.Rotated);
                    break;
                case "bad":
                    Assert.Equal(p.Value.GetInt32(), got.Bad);
                    break;
                case "off_page":
                    Assert.Equal(p.Value.GetInt32(), got.OffPage);
                    break;
                case "no_ink":
                    Same(p.Value.GetDouble(), got.NoInk!.Value);
                    break;
                default:
                    Assert.Fail($"unexpected stat '{p.Name}'");
                    break;
            }
        }

        // The reverse: a stat the reference did not set must not be set here either.
        Assert.True(
            want.TryGetProperty("no_ink", out _) == got.NoInk.HasValue,
            $"no_ink presence differs: reference {want.TryGetProperty("no_ink", out _)}");
    }

    private static void AssertRuns(JsonElement want, IReadOnlyList<TextLine> got)
    {
        Assert.True(
            want.GetArrayLength() == got.Count,
            $"{got.Count} runs, expected {want.GetArrayLength()}");
        int r = 0;
        foreach (var wr in want.EnumerateArray())
        {
            TextLine run = got[r++];
            var bbox = Fixtures.DoubleArray(wr.GetProperty("bbox"));
            Assert.True(bbox.Length == 4 && run.Bbox.Length == 4);
            for (int j = 0; j < 4; j++)
            {
                Same(bbox[j], run.Bbox[j]);
            }

            Assert.Equal(wr.GetProperty("text").GetString(), run.Text);

            var chars = wr.GetProperty("chars");
            Assert.True(chars.GetArrayLength() == run.Chars.Count);
            int k = 0;
            foreach (var wc in chars.EnumerateArray())
            {
                TextChar tc = run.Chars[k++];
                Assert.Equal(wc[0].GetString(), tc.Character);
                var box = Fixtures.DoubleArray(wc[1]);
                for (int j = 0; j < 4; j++)
                {
                    Same(box[j], tc.Bbox[j]);
                }
            }
        }
    }

    [Fact]
    public void Runs_and_stats_match_the_reference()
    {
        foreach (var (name, doc) in Documents())
        {
            using PdfDocument pdf = Open(doc);
            int i = 0;
            foreach (var pageJson in doc.GetProperty("pages").EnumerateArray())
            {
                using PdfPage page = pdf.LoadPage(i++);
                var (runs, stats) = page.ExtractRuns();
                var want = pageJson.GetProperty("textlayer");
                AssertStats(want.GetProperty("stats"), stats);
                AssertRuns(want.GetProperty("runs"), runs);
            }
        }
    }

    [Fact]
    public void Verdict_matches_the_reference()
    {
        foreach (var (name, doc) in Documents())
        {
            using PdfDocument pdf = Open(doc);
            int i = 0;
            foreach (var pageJson in doc.GetProperty("pages").EnumerateArray())
            {
                using PdfPage page = pdf.LoadPage(i++);
                var want = pageJson.GetProperty("textlayer");

                var render = page.Render(CheckScale);
                TextLayer layer = TextLayerReader.ReadTextLayer(
                    render, CheckScale, page.InvisibleTextFraction(), (page.Width, page.Height),
                    () => page.ExtractRuns());

                var size = Fixtures.DoubleArray(pageJson.GetProperty("size"));
                Same(size[0], layer.Size.Width);
                Same(size[1], layer.Size.Height);
                Assert.True(
                    want.GetProperty("trusted").GetBoolean() == layer.Trusted,
                    $"{name} page {i - 1}: trusted {layer.Trusted}, expected " +
                    $"{want.GetProperty("trusted").GetBoolean()}");
                Assert.True(
                    want.GetProperty("reason").GetString() == layer.Reason,
                    $"{name} page {i - 1}: reason '{layer.Reason}', expected " +
                    $"'{want.GetProperty("reason").GetString()}'");
                AssertStats(want.GetProperty("layer_stats"), layer.Stats);
                // The verdict's own lines: empty for the invisible-text early return (the
                // runs themselves are extract_runs' business, checked above).
                Assert.True(
                    want.GetProperty("layer_lines").GetInt32() == layer.Lines.Count,
                    $"{name} page {i - 1}: {layer.Lines.Count} lines, expected " +
                    $"{want.GetProperty("layer_lines").GetInt32()}");
            }
        }
    }

    /// <summary>
    /// The ink check only sees the render it is handed: a white render means no run has ink,
    /// which is the verdict a scan with a hidden text layer gets.
    /// </summary>
    [Fact]
    public void No_ink_runs_are_detected()
    {
        var doc = Documents().Single(d => d.Name == "text").Doc;
        using PdfDocument pdf = Open(doc);
        using PdfPage page = pdf.LoadPage(0);
        var (runs, _) = page.ExtractRuns();
        Assert.True(runs.Count > 0);

        var white = new byte[200 * 120];
        Array.Fill(white, (byte)255);
        var gray = new GrayImage(200, 120, white);

        Same(1.0, TextLayerReader.NoInkFraction(gray, runs, 1.0));

        TextLayer layer = TextLayerReader.ReadTextLayer(
            gray, 1.0, page.InvisibleTextFraction(), (page.Width, page.Height),
            () => page.ExtractRuns());
        Assert.False(layer.Trusted);
        Assert.Equal("100% of text runs have no ink", layer.Reason);
        Same(1.0, layer.Stats.NoInk!.Value);

        // And the real render of the page does have ink under every run.
        var real = page.Render(CheckScale);
        Same(0.0, TextLayerReader.NoInkFraction(real, runs, CheckScale));
    }

    [Fact]
    public void Bad_char_count_matches_unicodedata()
    {
        int cases = 0;
        foreach (var e in Fx.GetProperty("bad_chars").EnumerateArray())
        {
            var sb = new StringBuilder();
            foreach (var c in e.GetProperty("codes").EnumerateArray())
            {
                int codePoint = c.GetInt32();
                sb.Append(codePoint <= 0xFFFF
                    ? ((char)codePoint).ToString()
                    : char.ConvertFromUtf32(codePoint));
            }

            Assert.True(
                e.GetProperty("count").GetInt32() == TextLayerReader.BadCharCount(sb.ToString()),
                $"bad_char_count({string.Join(",", e.GetProperty("codes").EnumerateArray().Select(
                    c => "U+" + c.GetInt32().ToString("X4")))})");
            cases++;
        }

        Assert.Equal(19, cases);
    }
}
