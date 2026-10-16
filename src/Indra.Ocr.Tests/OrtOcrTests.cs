using System.Security.Cryptography;
using System.Text.Json;
using Indra.Ocr.Images;
using Indra.Ocr.Imaging;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;

namespace Indra.Ocr.Tests;

/// <summary>
/// Step 155: the <see cref="OrtOcr"/> driver end to end — <c>detect</c>, <c>recognize</c>,
/// <c>assemble</c> and <c>__call__</c> — against <c>fixtures/pipeline.json</c>, which the Python
/// reference wrote for the example pages shipped under <c>resources/indra-src/assets/examples</c>.
/// </summary>
/// <remarks>
/// The inputs and the model folder are external material that is not in git, so the tests return
/// early when either is missing (like <see cref="TestModels"/>); the fixture itself is committed,
/// so the expectations stay reviewable. The reference's recorded page hashes are checked too, so
/// a re-downloaded image cannot silently change what is being compared.
/// </remarks>
public class OrtOcrTests
{
    private static readonly JsonElement Fx = Fixtures.Load("pipeline.json");

    /// <summary>The <c>resources</c> folder (the example pages live under it), or null.</summary>
    private static string? Resources { get; } = FindResources();

    private static string? FindResources()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "resources");
            if (Directory.Exists(Path.Combine(candidate, "indra-src", "assets", "examples")))
                return candidate;
        }
        return null;
    }

    /// <summary>Exact equality, including the sign of zero (0.0 and -0.0 swap bits).</summary>
    private static void Same(double expected, double actual, string what) =>
        Assert.True(
            expected.Equals(actual) &&
            (expected != 0 || BitConverter.DoubleToInt64Bits(expected) ==
                              BitConverter.DoubleToInt64Bits(actual)),
            $"{what}: expected {expected:R}, got {actual:R}");

    private static void SameFloats(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string what)
    {
        Assert.True(expected.Length == actual.Length,
                    $"{what}: {actual.Length} values, expected {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(BitConverter.SingleToInt32Bits(expected[i]) ==
                        BitConverter.SingleToInt32Bits(actual[i]),
                        $"{what}[{i}]: expected {expected[i]:R}, got {actual[i]:R}");
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonElement Input(string name)
    {
        foreach (var i in Fx.GetProperty("inputs").EnumerateArray())
        {
            if (i.GetProperty("name").GetString() == name)
                return i;
        }

        throw new InvalidOperationException($"no recorded input '{name}'");
    }

    /// <summary>Decodes the example page with the host decoder and checks what was recorded.</summary>
    private static GrayImage Decode(JsonElement input)
    {
        string name = input.GetProperty("name").GetString()!;
        string path = Path.Combine(Resources!, input.GetProperty("file").GetString()!);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(Sha256(bytes) == input.GetProperty("sha256").GetString(),
                    $"{name}: the example page changed — regenerate fixtures/pipeline.json");

        byte[]? pixels = null;
        int w = 0, h = 0, frames = 0;
        foreach (GrayImageData frame in ImageFiles.ReadImages(path))
        {
            frames++;
            pixels = frame.Data.ToArray();
            w = frame.Width;
            h = frame.Height;
        }
        Assert.True(frames == 1, $"{name}: {frames} frames");
        Assert.True(h == input.GetProperty("h").GetInt32() && w == input.GetProperty("w").GetInt32(),
                    $"{name}: decoded {h}x{w}, expected " +
                    $"{input.GetProperty("h").GetInt32()}x{input.GetProperty("w").GetInt32()}");
        return new GrayImage(w, h, pixels!);
    }

    /// <summary>Applies the case's derivation, exactly as the generator did.</summary>
    private static GrayImage Derive(GrayImage gray, JsonElement derive)
    {
        if (derive.ValueKind == JsonValueKind.Null)
            return gray;
        if (derive.TryGetProperty("rot90", out var turnsElement))
        {
            int turns = turnsElement.GetInt32();
            var rotated = new byte[gray.PixelCount];
            Orientation.Rotate90(gray, rotated, turns);
            bool swapped = turns % 2 != 0;
            return new GrayImage(swapped ? gray.Height : gray.Width,
                                 swapped ? gray.Width : gray.Height, rotated);
        }

        double degrees = derive.GetProperty("rotate_page").GetDouble();
        var tilted = new byte[gray.PixelCount];
        Orientation.RotatePage(gray, degrees, tilted, gray.Width);
        return new GrayImage(gray.Width, gray.Height, tilted);
    }

    private static OrtOcr Open(JsonElement options)
    {
        bool adaptive = !options.TryGetProperty("adaptive", out var a) || a.GetBoolean();
        bool tableHeads = options.TryGetProperty("table_heads", out var t) && t.GetBoolean();
        bool furniture = options.TryGetProperty("furniture", out var f) && f.GetBoolean();
        return new OrtOcr(TestModels.Directory!, threads: 4, adaptive: adaptive,
                          tableHeads: tableHeads, furniture: furniture);
    }

    private static void AssertRecognition(JsonElement want, IReadOnlyList<RecognizedLine> rec,
                                          string name)
    {
        var lines = want.GetProperty("lines");
        Assert.True(lines.GetArrayLength() == rec.Count,
                    $"{name}: {rec.Count} lines, expected {lines.GetArrayLength()}");
        var counts = want.GetProperty("tok_counts");
        int i = 0;
        foreach (var line in lines.EnumerateArray())
        {
            Assert.True(line.GetProperty("text").GetString() == rec[i].Text,
                        $"{name} line {i}: text '{rec[i].Text}', expected " +
                        $"'{line.GetProperty("text").GetString()}'");
            Same(line.GetProperty("conf").GetDouble(), rec[i].Confidence, $"{name} line {i} conf");
            i++;
        }

        // Every token, in order: its id (or character) and its page x.
        int[] ids = B64.ZInts(want.GetProperty("tok_ids").GetString()!);
        double[] xs = B64.ZDoubles(want.GetProperty("tok_xs").GetString()!);
        Assert.True(ids.Length == xs.Length);
        var chars = new Dictionary<int, string>();
        foreach (var c in want.GetProperty("tok_chars").EnumerateArray())
            chars[c.GetProperty("i").GetInt32()] = c.GetProperty("char").GetString()!;

        var flat = new List<RecToken>();
        foreach (var line in rec)
            flat.AddRange(line.Tokens);
        Assert.True(flat.Count == ids.Length,
                    $"{name}: {flat.Count} tokens, expected {ids.Length}");
        for (int k = 0; k < flat.Count; k++)
        {
            if (chars.TryGetValue(k, out string? character))
            {
                Assert.True(flat[k].Character == character,
                            $"{name} token {k}: character '{flat[k].Character}', expected '{character}'");
            }
            else
            {
                Assert.True(flat[k].Id == ids[k],
                            $"{name} token {k}: id {flat[k].Id}, expected {ids[k]}");
            }

            Same(xs[k], flat[k].X, $"{name} token {k} x");
        }
    }

    private static void AssertPage(JsonElement want, OcrPage page, string name)
    {
        var lines = want.GetProperty("lines");
        Assert.True(lines.GetArrayLength() == page.Lines.Count,
                    $"{name}: {page.Lines.Count} lines, expected {lines.GetArrayLength()}");
        int i = 0;
        foreach (var line in lines.EnumerateArray())
        {
            OcrLine got = page.Lines[i];
            var box = Fixtures.DoubleArray(line.GetProperty("box"));
            Assert.True(box.Length == 4);
            for (int j = 0; j < 4; j++)
                Same(box[j], got.Box[j], $"{name} line {i} box[{j}]");
            Assert.True(line.GetProperty("text").GetString() == got.Text,
                        $"{name} line {i}: text '{got.Text}', expected " +
                        $"'{line.GetProperty("text").GetString()}'");
            Same(line.GetProperty("conf").GetDouble(), got.Confidence, $"{name} line {i} conf");
            i++;
        }

        string? markdown = want.GetProperty("markdown").ValueKind == JsonValueKind.Null
            ? null
            : want.GetProperty("markdown").GetString();
        Assert.True(markdown == page.Markdown, $"{name}: markdown differs");

        var blocks = want.GetProperty("blocks");
        Assert.True(blocks.GetArrayLength() == page.Blocks.Count,
                    $"{name}: {page.Blocks.Count} blocks, expected {blocks.GetArrayLength()}");
        int b = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            var got = page.Blocks[b];
            Assert.True(block.GetProperty("label").GetString() == got.Label,
                        $"{name} block {b}: label '{got.Label}'");
            var bbox = Fixtures.DoubleArray(block.GetProperty("bbox"));
            for (int j = 0; j < 4; j++)
                Same(bbox[j], got.Bbox[j], $"{name} block {b} bbox[{j}]");
            Assert.True(block.GetProperty("text").GetString() == got.Text,
                        $"{name} block {b}: text differs");
            b++;
        }

        Assert.True(want.GetProperty("source").GetString() == page.Source, $"{name}: source");
        Assert.True(want.GetProperty("note").GetString() == page.Note,
                    $"{name}: note '{page.Note}', expected " +
                    $"'{want.GetProperty("note").GetString()}'");
        Assert.True(want.GetProperty("rotation").GetInt32() == page.Rotation,
                    $"{name}: rotation {page.Rotation}");
        Same(want.GetProperty("skew").GetDouble(), page.Skew, $"{name}: skew");

        var keys = want.GetProperty("timings").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.True(keys.SequenceEqual(page.Timings.Keys),
                    $"{name}: timings keys [{string.Join(", ", page.Timings.Keys)}], expected " +
                    $"[{string.Join(", ", keys)}]");
        // total_ms is the sum of the others (the reference recomputes it).
        Same(page.Timings.Where(kv => kv.Key != "total_ms").Sum(kv => kv.Value),
             page.Timings["total_ms"], $"{name}: total_ms");
    }

    [Fact]
    public void Detect_recognize_and_page_match_the_reference()
    {
        if (Resources is null || TestModels.Directory is null)
            return;
        int cases = 0;
        foreach (var c in Fx.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            GrayImage gray = Derive(Decode(Input(c.GetProperty("input").GetString()!)),
                                     c.GetProperty("derive"));
            Assert.True(Sha256(gray.Data) == c.GetProperty("gray_sha256").GetString(),
                        $"{name}: the derived page differs — regenerate fixtures/pipeline.json");

            var options = c.GetProperty("options");
            bool turn = options.GetProperty("turn").GetBoolean();
            using var ocr = Open(options);

            UprightPage upright = ocr.Upright(gray, turn);
            GrayImage view = upright.Page;

            float[] boxes = ocr.Detect(view);
            var wantDetect = c.GetProperty("detect");
            int n = wantDetect.GetProperty("n").GetInt32();
            Assert.True(boxes.Length == n * 5, $"{name}: {boxes.Length / 5} boxes, expected {n}");
            SameFloats(B64.ZFloats(wantDetect.GetProperty("boxes").GetString()!), boxes,
                       $"{name}: detect");

            AssertRecognition(c.GetProperty("rec"), ocr.Recognize(view, boxes), name);
            AssertPage(c.GetProperty("page"), ocr.Call(gray, turn), name);
            cases++;
        }

        Assert.True(cases >= 10, $"only {cases} cases ran");
    }
}
