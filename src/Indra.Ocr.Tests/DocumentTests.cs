using System.Security.Cryptography;
using System.Text.Json;
using Indra.Ocr.Images;
using Indra.Ocr.Imaging;
using Indra.Ocr.Pdf;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Step 160: the document converters �?<see cref="PdfConverter"/> for the trusted PDF text layer
/// and its OCR fallback, <see cref="ImageConverter"/> for image files �?against
/// <c>fixtures/document.json</c>, which the Python reference wrote for the sample PDFs of
/// <c>tools/scripts/test/pdf_samples.py</c> and one example page.
/// </summary>
/// <remarks>
/// The pure policy functions (<c>page_scale</c>, <c>right_to_left</c>, <c>assign_runs</c>) are
/// checked on their own; the end-to-end cases need the model folder and the example page, both
/// external material, so those tests return early when either is missing (like
/// <see cref="TestModels"/>). The sample PDFs travel inside the fixture, so the tests need no
/// Python and no file beyond what they write themselves.
/// </remarks>
[Collection("pdfium")]
public class DocumentTests
{
    private static readonly JsonElement Fx = Fixtures.Load("document.json");

    /// <summary>The <c>resources</c> folder (the example page lives under it), or null.</summary>
    private static string? Resources { get; } = FindResources();

    /// <summary>Where the sample PDFs are written for <see cref="PdfConverter.Convert"/>.</summary>
    private static string Work { get; } =
        Path.Combine(Path.GetTempPath(), "indra-ocr-document-tests");

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

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Python's <c>chr</c>: a code point as a string (a surrogate stays a lone one).</summary>
    private static string Chr(int codePoint) =>
        codePoint <= 0xFFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);

    /// <summary>The fixture's runs as <see cref="TextLine"/>s (the boxes are the generator's).</summary>
    private static List<TextLine> Runs(JsonElement runs)
    {
        var list = new List<TextLine>();
        foreach (var r in runs.EnumerateArray())
        {
            var chars = new List<TextChar>();
            foreach (var c in r.GetProperty("chars").EnumerateArray())
                chars.Add(new TextChar(Chr(c.GetProperty("cp").GetInt32()),
                                       Fixtures.DoubleArray(c.GetProperty("box"))));
            list.Add(new TextLine(new double[4], r.GetProperty("text").GetString()!, chars));
        }

        return list;
    }

    private static List<TextLine> TextRuns(JsonElement texts)
    {
        var list = new List<TextLine>();
        foreach (var t in texts.EnumerateArray())
            list.Add(new TextLine(new double[4], t.GetString()!, Array.Empty<TextChar>()));
        return list;
    }

    /// <summary>
    /// <c>document.convert</c>'s dispatch, as a host does it: a missing file first (the file's own
    /// bytes decide PDF vs image, and that check needs to read them), then the PDF or the image
    /// converter.
    /// </summary>
    private static IEnumerable<(int Index, OcrPage Page)> ConvertFile(
        OrtOcr ocr, JsonElement options, string path, int maxPages = 0, string? password = null)
    {
        if (!File.Exists(path))
            throw new InputError($"{path}: no such file");
        if (OrtOcr.IsPdf(path))
        {
            // The host chooses the pages: like the reference's _plan it opens the document for
            // its page count (InputError for one that will not open) and caps it at max_pages.
            int n;
            using (PdfDocument doc = PdfConverter.Open(path, password))
            {
                n = maxPages <= 0 ? doc.PageCount : Math.Min(doc.PageCount, maxPages);
            }

            return new PdfConverter(ocr, Dpi(options), Mode(options), PdfOrder(options),
                                    ImageOcr(options))
                .ConvertPages(path, password, Enumerable.Range(0, n));
        }

        return new ImageConverter(ocr).ConvertFrames(path, maxPages);
    }

    private static double Dpi(JsonElement options) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty("dpi", out var d)
            ? d.GetDouble()
            : 200.0;

    private static string Mode(JsonElement options) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty("mode", out var m)
            ? m.GetString()!
            : "auto";

    private static bool PdfOrder(JsonElement options) =>
        options.ValueKind != JsonValueKind.Object || !options.TryGetProperty("pdf_order", out var po)
        || po.GetBoolean();

    private static bool ImageOcr(JsonElement options) =>
        options.ValueKind != JsonValueKind.Object || !options.TryGetProperty("image_ocr", out var io)
        || io.GetBoolean();

    /// <summary>Writes a sample PDF out and returns its path.</summary>
    private static string WriteSample(string sample)
    {
        Directory.CreateDirectory(Work);
        string path = Path.Combine(Work, sample + ".pdf");
        string blob = Fx.GetProperty("pdfs").GetProperty(sample).GetString()!;
        File.WriteAllBytes(path, Convert.FromBase64String(blob));
        return path;
    }

    [Fact]
    public void Page_scale_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("page_scale").EnumerateArray())
        {
            double scale = PdfConverter.PageScale(c.GetProperty("w").GetDouble(),
                                               c.GetProperty("h").GetDouble(),
                                               c.GetProperty("dpi").GetDouble());
            Same(c.GetProperty("scale").GetDouble(), scale,
                 $"{c.GetProperty("w").GetDouble()}x{c.GetProperty("h").GetDouble()}" +
                 $"@{c.GetProperty("dpi").GetDouble()}");
            cases++;
        }

        Assert.True(cases >= 10, $"only {cases} cases ran");
    }

    [Fact]
    public void Right_to_left_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("right_to_left").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var tokens = new List<RecToken>();
            foreach (var cp in c.GetProperty("cps").EnumerateArray())
                tokens.Add(new RecToken(0, Chr(cp.GetInt32()), 0.0, 0));

            Assert.True(TextLayerRuns.RightToLeft(tokens) == c.GetProperty("rtl").GetBoolean(),
                        $"{name}: base direction differs");
            cases++;
        }

        Assert.True(cases >= 12, $"only {cases} cases ran");
    }

    [Fact]
    public void Assign_runs_matches_the_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("assign_runs").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            List<TextLine> runs = Runs(c.GetProperty("runs"));
            float[] boxes = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            int n = c.GetProperty("n").GetInt32();
            Assert.True(boxes.Length == n * 4, $"{name}: {boxes.Length / 4} boxes, expected {n}");

            var (toks, lost) = TextLayerRuns.AssignRuns(runs, boxes, c.GetProperty("k").GetDouble());
            var want = c.GetProperty("toks");
            Assert.True(want.GetArrayLength() == toks.Count,
                        $"{name}: {toks.Count} lines, expected {want.GetArrayLength()}");
            int i = 0;
            foreach (var line in want.EnumerateArray())
            {
                var got = toks[i];
                Assert.True(line.GetArrayLength() == got.Count,
                            $"{name} line {i}: {got.Count} tokens, expected " +
                            $"{line.GetArrayLength()}");
                int j = 0;
                foreach (var t in line.EnumerateArray())
                {
                    Assert.True(got[j].Character == Chr(t.GetProperty("cp").GetInt32()),
                                $"{name} line {i} token {j}: '{got[j].Character}', expected " +
                                $"'{Chr(t.GetProperty("cp").GetInt32())}'");
                    Same(t.GetProperty("x").GetDouble(), got[j].X,
                         $"{name} line {i} token {j} x");
                    Assert.True(t.GetProperty("ci").GetInt32() == got[j].ContentIndex,
                                $"{name} line {i} token {j}: content index " +
                                $"{got[j].ContentIndex}");
                    j++;
                }

                i++;
            }

            Assert.True(c.GetProperty("lost").GetInt32() == lost,
                        $"{name}: {lost} characters outside the boxes, expected " +
                        $"{c.GetProperty("lost").GetInt32()}");
            cases++;
        }

        Assert.True(cases >= 12, $"only {cases} cases ran");
    }

    [Fact]
    public void Readable_matches_the_reference()
    {
        if (TestModels.Directory is null || !TestModels.IsCurrent)
            return;
        using var ocr = new OrtOcr(TestModels.Directory, threads: 4);
        int cases = 0;
        foreach (var c in Fx.GetProperty("readable").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            bool readable = TextLayerRuns.Readable(ocr, TextRuns(c.GetProperty("texts")));
            Assert.True(readable == c.GetProperty("readable").GetBoolean(),
                        $"{name}: readable={readable}, expected {c.GetProperty("readable").GetBoolean()}");
            cases++;
        }

        Assert.True(cases >= 18, $"only {cases} cases ran");
    }

    [Fact]
    public void Convert_over_sample_pdfs_matches_the_reference()
    {
        if (TestModels.Directory is null || !TestModels.IsCurrent)
            return;
        using var ocr = new OrtOcr(TestModels.Directory, threads: 4);
        int cases = 0;
        foreach (var c in Fx.GetProperty("pdf_cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            string path = WriteSample(c.GetProperty("pdf").GetString()!);
            string? password = c.GetProperty("password").ValueKind == JsonValueKind.Null
                ? null
                : c.GetProperty("password").GetString();

            var got = ConvertFile(ocr, c.GetProperty("options"), path,
                              c.GetProperty("max_pages").GetInt32(), password).ToList();
            var want = c.GetProperty("pages");
            Assert.True(got.Count == want.GetArrayLength(),
                        $"{name}: {got.Count} pages, expected {want.GetArrayLength()}");
            int i = 0;
            foreach (var p in want.EnumerateArray())
            {
                Assert.True(p.GetProperty("index").GetInt32() == got[i].Index,
                            $"{name}: page index {got[i].Index}");
                AssertPage(p.GetProperty("page"), got[i].Page, $"{name} page {i}");
                i++;
            }

            cases++;
        }

        Assert.True(cases >= 16, $"only {cases} cases ran");
    }

    [Fact]
    public void Convert_over_an_image_matches_the_reference()
    {
        if (Resources is null || TestModels.Directory is null || !TestModels.IsCurrent)
            return;
        using var ocr = new OrtOcr(TestModels.Directory, threads: 4);
        int cases = 0;
        foreach (var c in Fx.GetProperty("image_cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            string path = Path.Combine(Resources, c.GetProperty("file").GetString()!);
            Assert.True(Sha256(File.ReadAllBytes(path)) == c.GetProperty("sha256").GetString(),
                        $"{name}: the example page changed �?regenerate fixtures/document.json");

            var got = ConvertFile(ocr, c.GetProperty("options"), path).ToList();
            var want = c.GetProperty("pages");
            Assert.True(got.Count == want.GetArrayLength(),
                        $"{name}: {got.Count} pages, expected {want.GetArrayLength()}");
            int i = 0;
            foreach (var p in want.EnumerateArray())
            {
                Assert.True(p.GetProperty("index").GetInt32() == got[i].Index,
                            $"{name}: page index {got[i].Index}");
                AssertPage(p.GetProperty("page"), got[i].Page, $"{name} page {i}");
                i++;
            }

            cases++;
        }

        Assert.True(cases >= 1, $"only {cases} cases ran");
    }

    [Fact]
    public void ConvertPage_and_Convert_give_one_result()
    {
        if (TestModels.Directory is null)
            return;
        using var ocr = new OrtOcr(TestModels.Directory, threads: 4);

        // A PDF page: ConvertPage is the one-index form of ConvertPages.
        string path = WriteSample("text");
        var pages = new PdfConverter(ocr).ConvertPages(path, null, [0]).ToList();
        OcrPage one = new PdfConverter(ocr).ConvertPage(path, null, 0);
        Assert.True(pages.Count == 1, $"{pages.Count} pages");
        Assert.True(one.Markdown == pages[0].Page.Markdown,
                    "ConvertPage's markdown differs from ConvertPages'");
        Assert.True(one.Source == pages[0].Page.Source && one.Note == pages[0].Page.Note,
                    "ConvertPage's source/note differ");
        Assert.True(one.Lines.Count == pages[0].Page.Lines.Count, "the line count differs");

        // The pages come back in the order they were asked for (the text sample has two).
        var order = new PdfConverter(ocr).ConvertPages(path, null, [1, 0])
                                         .Select(p => p.Index).ToList();
        Assert.Equal([1, 0], order);

        if (Resources is null)
            return;

        // An image file: Convert is the one-result form of ConvertFrames.
        JsonElement ic = Fx.GetProperty("image_cases").EnumerateArray().First();
        string image = Path.Combine(Resources, ic.GetProperty("file").GetString()!);
        var frame = new ImageConverter(ocr).ConvertFrames(image, maxPages: 1).Single();
        OcrPage? img = new ImageConverter(ocr).Convert(image);
        Assert.NotNull(img);
        Assert.True(img!.Markdown == frame.Page.Markdown,
                    "the single frame's markdown differs from ConvertFrames'");
    }

    [Fact]
    public void Convert_reports_the_reference_errors()
    {
        if (TestModels.Directory is null)
            return;
        using var ocr = new OrtOcr(TestModels.Directory, threads: 4);

        int cases = 0;
        foreach (var c in Fx.GetProperty("error_cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            string path = WriteSample(c.GetProperty("pdf").GetString()!);
            string? password = c.GetProperty("password").ValueKind == JsonValueKind.Null
                ? null
                : c.GetProperty("password").GetString();
            string expected = $"{path}: {c.GetProperty("message").GetString()}";

            var e = Assert.Throws<InputError>(
                () => ConvertFile(ocr, default, path, 0, password).ToList());
            Assert.True(e.Message == expected,
                        $"{name}: '{e.Message}', expected '{expected}'");
            cases++;
        }

        // A file that is neither a PDF nor a readable image, and a file that is not there at all.
        // The decoder is asked directly: the host's dispatch reads the file's own bytes to pick
        // one, so it cannot answer for a path it cannot read.
        Directory.CreateDirectory(Work);
        string junk = Path.Combine(Work, "junk.txt");
        File.WriteAllText(junk, "not a document");
        var notAnImage = Assert.Throws<InputError>(
            () => new ImageConverter(ocr).ConvertFrames(junk).ToList());
        Assert.True(notAnImage.Message == $"{junk}: not a PDF or a readable image",
                    $"junk: '{notAnImage.Message}'");

        string missing = Path.Combine(Work, "missing.png");
        var noSuchFile = Assert.Throws<InputError>(
            () => new ImageConverter(ocr).ConvertFrames(missing).ToList());
        Assert.True(noSuchFile.Message == $"{missing}: no such file",
                    $"missing: '{noSuchFile.Message}'");

        Assert.True(cases >= 2, $"only {cases} cases ran");
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

        Assert.True(want.GetProperty("source").GetString() == page.Source,
                    $"{name}: source '{page.Source}', expected " +
                    $"'{want.GetProperty("source").GetString()}'");
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
}
