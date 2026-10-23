using Indra.Ocr.ArgumentParsing;
using Indra.Ocr.Cli;
using Indra.Ocr.Imaging;
using Indra.Ocr.Layout;
using Indra.Ocr.Maths;
using Indra.Ocr.Onnx;
using Indra.Ocr.Order;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// The reference's own <c>tests/test_textlayer.py</c>, ported case for case: the bad-character
/// count, the reading-order checks, <c>assign_runs</c>, the vocabulary gate, the CJK routing, the
/// turn-before-detection order, the clustering invariant, the skew estimate, the line joining and
/// the math helpers.
/// </summary>
/// <remarks>
/// Two things are adapted rather than copied. The reference pokes <c>OrtOCR</c>'s attributes
/// (<c>ocr.orient = None</c>, a stub <c>recognize</c>) because Python lets it; here the pieces are
/// seams (<see cref="IOrientModel"/>, <see cref="IRecModel"/>, <see cref="IScriptModel"/>) and the
/// cases are written against them, which is the same test on the same code path. And the two
/// random tests seed <see cref="Random"/> instead of numpy's <c>default_rng</c> — the properties
/// under test (clustering equals brute-force average linkage; the skew estimate undoes a tilt) do
/// not depend on which matrices come out.
/// </remarks>
public class ReferenceTextLayerTests
{
    // ------------------------------------------------------------------ textlayer.py

    /// <summary><c>test_bad_chars_count_replacement_and_private_use</c>.</summary>
    [Fact]
    public void Bad_chars_count_replacement_and_private_use()
    {
        Assert.Equal(0, TextLayerReader.BadCharCount("ok"));
        Assert.Equal(2, TextLayerReader.BadCharCount("a\ufffd\ue000"));
    }

    /// <summary><c>test_two_columns_in_reading_order_are_coherent</c>.</summary>
    [Fact]
    public void Two_columns_in_reading_order_are_coherent()
    {
        var (left, right) = Columns();
        float[] boxes = Boxes(left.Concat(right));
        Assert.Equal(0, Assembler.OrderBacktracks(boxes, 40));
        Assert.True(Assembler.PdfOrderCoherent(boxes, 40));
    }

    /// <summary><c>test_interleaved_columns_are_not_coherent</c>.</summary>
    [Fact]
    public void Interleaved_columns_are_not_coherent()
    {
        var (left, right) = Columns();
        float[] boxes = Boxes(left.Zip(right, (l, r) => new[] { l, r }).SelectMany(p => p));
        // Geometry alone cannot see it (a table is read the same way) ...
        Assert.Equal(0, Assembler.OrderBacktracks(boxes, 40));

        // ... the order model can: it puts the left column first. The reference builds the logits
        // from the true reading position (column * 100 + row) and compares the sign.
        int[] key = new int[40];
        for (int i = 0; i < 20; i++)
        {
            key[2 * i] = i;
            key[2 * i + 1] = 100 + i;
        }

        float[] logits = PairLogits(key);
        Assert.False(Assembler.PdfOrderCoherent(boxes, 40, logits));

        int[] good = Enumerable.Range(0, 40).OrderBy(i => key[i]).ToArray();
        float[] goodBoxes = ReorderBoxes(boxes, good);
        float[] goodLogits = ReorderMatrix(logits, good, 40);
        Assert.True(Assembler.PdfOrderCoherent(goodBoxes, 40, goodLogits));
    }

    /// <summary><c>test_lines_written_bottom_up_are_not_coherent</c>.</summary>
    [Fact]
    public void Lines_written_bottom_up_are_not_coherent()
    {
        var boxes = new List<float[]>();
        for (int i = 0; i < 20; i++)
            boxes.Add(Box(50, 400 - 15 * i, 500, 410 - 15 * i));
        Assert.False(Assembler.PdfOrderCoherent(Boxes(boxes), 20));
    }

    // ------------------------------------------------------------------ cli.source_options

    /// <summary><c>test_cli_source_flags</c>.</summary>
    /// <remarks>
    /// The reference builds a bare parser and adds only the group; the port's parser is the whole
    /// command line, which holds the same options with the same defaults, so the values are the
    /// same (an input is given because the positional is <c>nargs='+'</c>).
    /// </remarks>
    [Fact]
    public void Cli_source_flags()
    {
        var ap = CliParser.Build();
        Assert.Equal(("auto", true, true), SourceArguments.Options(ap.ParseArgs(["a.pdf"])));
        Assert.Equal("ocr",
                     SourceArguments.Options(ap.ParseArgs(["--mode", "ocr", "a.pdf"])).Mode);
        var (_, pdfOrder, imageOcr) =
            SourceArguments.Options(ap.ParseArgs(["--no-image-ocr", "a.pdf"]));
        Assert.True(pdfOrder);
        Assert.False(imageOcr);
    }

    // ------------------------------------------------------------------ document.assign_runs

    /// <summary><c>test_runs_go_to_the_smallest_containing_line_in_x_order</c>.</summary>
    [Fact]
    public void Runs_go_to_the_smallest_containing_line_in_x_order()
    {
        float[] boxes = Boxes([Box(0, 0, 100, 20), Box(70, 2, 95, 18), Box(0, 30, 100, 50)]);
        var runs = new List<TextLine>
        {
            Run("ab c", 10, 5, 15),
            Run("\u03b1", 2, 3, 13),
            Run("xy", 75, 5, 15),
            Run("far", 500, 5, 15),
        };

        var (toks, lost) = TextLayerRuns.AssignRuns(runs, boxes, 1.0);
        Assert.Equal("\u03b1 ab c", Text(toks[0]));       // runs by x, own order kept
        Assert.Equal(1, toks[0][0].ContentIndex);          // content index of the run
        Assert.Equal("xy", Text(toks[1]));                 // inside both boxes: the smaller wins
        Assert.Empty(toks[2]);
        Assert.Equal(3, lost);
    }

    /// <summary><c>test_a_run_across_two_cells_splits_between_them</c>.</summary>
    [Fact]
    public void A_run_across_two_cells_splits_between_them()
    {
        const string text = "12 34";
        var chars = new List<TextChar>();
        for (int i = 0; i < text.Length; i++)
            chars.Add(new TextChar(text[i].ToString(), [i * 10.0, 0.0, i * 10.0 + 8, 10.0]));
        var run = new TextLine([0, 0, 48, 10], text, chars);
        float[] boxes = Boxes([Box(0, 0, 19, 10), Box(29, 0, 50, 10)]);   // two cells

        var (toks, lost) = TextLayerRuns.AssignRuns([run], boxes, 1.0);
        Assert.Equal(["12", "34"], toks.Select(tk => Text(tk).Trim()));
        Assert.Equal(0, lost);
    }

    /// <summary><c>test_runs_of_a_right_to_left_line_go_right_to_left</c>.</summary>
    [Fact]
    public void Runs_of_a_right_to_left_line_go_right_to_left()
    {
        float[] boxes = Boxes([Box(0, 0, 100, 20)]);
        // Two table cells on one row: "שלום" on the right, "עולם" on the left.
        var (toks, _) = TextLayerRuns.AssignRuns(
            [RtlRun("\u05e2\u05d5\u05dc\u05dd", 30), RtlRun("\u05e9\u05dc\u05d5\u05dd", 90)],
            boxes, 1.0);
        Assert.Equal("\u05e9\u05dc\u05d5\u05dd \u05e2\u05d5\u05dc\u05dd", Text(toks[0]));
    }

    // ------------------------------------------------------------------ document.readable

    /// <summary><c>test_ocr_only_reads_scripts_in_its_vocab</c>.</summary>
    /// <remarks>
    /// The reference hands <c>readable</c> a stand-in whose vocabulary it made up and whose
    /// <c>vocab_cjk</c> is unset, and sets <c>vocab_cjk</c> for its last assertion. The port reads
    /// the vocabulary off the recognizer, and the shipped set carries <c>rec_cjk</c> — so
    /// <c>vocab_cjk</c> is set from the start and the CJK assertion is the reference's last one.
    /// </remarks>
    [Fact]
    public void Ocr_only_reads_scripts_in_its_vocab()
    {
        if (TestModels.Directory is null)
            return;
        using var ocr = new OrtOcr(TestModels.Directory, threads: 4);
        Assert.True(ocr.VocabCjk is not null, "the shipped set has no CJK vocabulary");

        Assert.True(TextLayerRuns.Readable(ocr, [Line("hello world 123")]));
        Assert.False(TextLayerRuns.Readable(ocr, [Line("\u0645\u0631\u062d\u0628\u0627"), Line("ok")]));
        Assert.True(TextLayerRuns.Readable(ocr, [Line("1234 ...")]));   // no letters: nothing to rule out
        Assert.True(TextLayerRuns.Readable(ocr, [Line("\u4e2d\u6587\u6587\u672c")]));
    }

    // ------------------------------------------------------------------ pipeline.recognize

    /// <summary><c>test_lines_with_hanzi_go_to_the_cjk_recognizer</c>.</summary>
    /// <remarks>
    /// The reference pokes a half-built <c>OrtOCR</c>; the port's recognizer takes its models as
    /// arguments, so the same stand-ins are passed to <see cref="Recognizer"/>. Its main-recognizer
    /// tokens are class ids and its CJK ones are characters (the reference's own asymmetry).
    /// </remarks>
    [Fact]
    public void Lines_with_hanzi_go_to_the_cjk_recognizer()
    {
        var vocab = new Vocab(["a", "b"]);
        var vocabCjk = new Vocab(["\u4e2d", "b"]);
        var recognizer = new Recognizer(
            new FixedRec(1), vocab, script: new WideScript(), recCjk: new FixedRec(1),
            vocabCjk: vocabCjk, height: 32);

        var gray = new byte[200 * 800];
        Array.Fill(gray, (byte)255);
        float[] boxes = [10, 10, 100, 42, 1, 10, 100, 700, 132, 1];   // [N, 5]: x0 y0 x1 y1 score

        var lines = recognizer.Run(new GrayImage(800, 200, gray), boxes);
        Assert.NotEmpty(lines[0].Text);
        Assert.All(lines[0].Text, c => Assert.Equal('a', c));   // the reference's set(t0) == {"a"}
        Assert.True(lines[0].Tokens.All(t => !t.IsCharacter), "the main recognizer's tokens are ids");
        Assert.NotEmpty(lines[1].Text);
        Assert.All(lines[1].Text, c => Assert.Equal('\u4e2d', c));
        Assert.True(lines[1].Tokens.All(t => t.IsCharacter), "the CJK recognizer's tokens are characters");
        Assert.Equal(lines[1].Text, string.Concat(lines[1].Tokens.Select(t => t.Character)));
    }

    // ------------------------------------------------------------------ pipeline.upright

    /// <summary><c>test_a_turned_page_is_turned_upright_before_detection</c>.</summary>
    /// <remarks>
    /// The reference's stub also records the shape the detector is handed, which proves the turn
    /// happens first; here that order is <c>OrtOcr.Call</c>'s (upright, then detect on the upright
    /// view) and is covered end to end by <see cref="OrtOcrTests"/>. What this checks is the turn
    /// itself: the pixels come back swapped, and one quarter turn is reported.
    /// </remarks>
    [Fact]
    public void A_turned_page_is_turned_upright_before_detection()
    {
        var gray = new byte[300 * 500];
        Array.Fill(gray, (byte)255);
        byte[]? page = Orientation.Upright(new QuarterTurnOrient(), new GrayImage(500, 300, gray),
                                           out int width, out int height, out int turns,
                                           out double skew);
        Assert.NotNull(page);
        Assert.Equal(300, width);
        Assert.Equal(500, height);
        Assert.Equal(1, turns);            // the reference's page.rotation == 90
        Assert.Equal(0.0, skew);           // a blank page has no rows to straighten

        // With no orientation model there is no turn (the reference's orient = None).
        Assert.Null(Orientation.Upright(null, new GrayImage(500, 300, gray),
                                        out int w0, out int h0, out int t0, out _));
        Assert.Equal((500, 300, 0), (w0, h0, t0));
    }

    // ------------------------------------------------------------------ order_post._cluster

    /// <summary><c>test_fast_average_linkage_matches_the_brute_force</c>.</summary>
    [Fact]
    public void Fast_average_linkage_matches_the_brute_force()
    {
        var rng = new Random(1);
        for (int c = 0; c < 100; c++)
        {
            int n = rng.Next(1, 40);
            int groups = Math.Max(1, n / 4);
            var g = new int[n];
            for (int i = 0; i < n; i++)
                g[i] = rng.Next(groups);

            // float32 logits: the port's Cluster takes the model's own dtype, so the brute force
            // runs on the same narrowed values. The reference symmetrises ((L + L.T) / 2).
            var raw = new double[n * n];
            for (int i = 0; i < n * n; i++)
                raw[i] = Gaussian(rng) * 1.5;
            var logits = new float[n * n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    logits[i * n + j] = (float)((g[i] == g[j] ? 2.0 : -2.0)
                                                + (raw[i * n + j] + raw[j * n + i]) / 2.0);

            var got = Groups(OrderPost.Cluster(logits, n));
            var want = Brute(logits, n);
            Assert.True(want.Count == got.Count, $"case {c} (n={n}): {got.Count} blocks, expected {want.Count}");
            for (int i = 0; i < want.Count; i++)
                Assert.True(want[i].SequenceEqual(got[i]),
                            $"case {c} (n={n}) block {i}: [{string.Join(",", got[i])}] vs " +
                            $"[{string.Join(",", want[i])}]");
        }
    }

    // ------------------------------------------------------------------ pipeline.estimate_skew

    /// <summary><c>test_skew_estimate_recovers_a_tilt</c>.</summary>
    [Fact]
    public void Skew_estimate_recovers_a_tilt()
    {
        var rng = new Random(0);
        var page = new byte[1700 * 2200];
        Array.Fill(page, (byte)255);
        for (int y = 150; y < 2050; y += 45)   // text-like rows of ink blobs
        {
            int x = 150;
            while (x < 1550)
            {
                int w = rng.Next(20, 90);
                for (int yy = y; yy < Math.Min(y + 18, 2200); yy++)
                    for (int xx = x; xx < Math.Min(x + w, 1700); xx++)
                        page[yy * 1700 + xx] = 30;
                x += w + rng.Next(10, 25);
            }
        }

        Assert.True(Math.Abs(Orientation.EstimateSkew(new GrayImage(1700, 2200, page))) < 0.15);
        foreach (double tilt in new[] { 2.0, -1.3, 0.6 })
        {
            var tilted = new byte[1700 * 2200];
            Orientation.RotatePage(new GrayImage(1700, 2200, page), tilt, tilted, 1700);
            double est = Orientation.EstimateSkew(new GrayImage(1700, 2200, tilted));
            Assert.True(Math.Abs(est + tilt) <= 0.15, $"tilt {tilt}: estimate {est}");
        }

        var blank = new byte[600 * 800];
        Array.Fill(blank, (byte)255);
        Assert.Equal(0.0, Orientation.EstimateSkew(new GrayImage(600, 800, blank)));
    }

    // ------------------------------------------------------------------ layout_post

    /// <summary><c>test_cjk_lines_join_without_a_space_and_bare_bullets_start_items</c>.</summary>
    [Fact]
    public void Cjk_lines_join_without_a_space_and_bare_bullets_start_items()
    {
        Assert.Equal("\u6216\u5176\u4e2d\u7684\u4efb\u4f55\u90e8\u5206\uff0c\u5728 2020 \u5e74",
                     LayoutPost.JoinLines(["\u6216\u5176\u4e2d\u7684", "\u4efb\u4f55\u90e8\u5206\uff0c\u5728 2020 \u5e74"]));
        Assert.Equal("\u8aac\u660e\u3057\u307e\u3059\u3002\u6b21\u306b",
                     LayoutPost.JoinLines(["\u8aac\u660e\u3057\u307e\u3059\u3002", "\u6b21\u306b"]));
        Assert.Equal("\ud55c\uad6d\uc5b4 \ubb38\uc7a5 \ub2e4\uc74c \uc904",
                     LayoutPost.JoinLines(["\ud55c\uad6d\uc5b4 \ubb38\uc7a5", "\ub2e4\uc74c \uc904"]));   // Korean keeps the space
        Assert.Equal("tumorigenesis is", LayoutPost.JoinLines(["tumori-", "genesis is"]));
        Assert.Equal("first line second", LayoutPost.JoinLines(["first line", "second"]));

        Assert.True(LayoutPost.IsItem("\u2022"));
        Assert.True(LayoutPost.IsItem("\u2022 text"));
        Assert.True(LayoutPost.IsItem("12. text"));
        Assert.False(LayoutPost.IsItem("\u2022text"));
        Assert.False(LayoutPost.IsItem("12.5 kg"));
    }

    // ------------------------------------------------------------------ pipeline.unrotate_box

    /// <summary><c>test_unrotate_box_maps_a_deskewed_box_back</c>.</summary>
    [Fact]
    public void Unrotate_box_maps_a_deskewed_box_back()
    {
        var page = new byte[600 * 400];
        Array.Fill(page, (byte)255);
        for (int y = 180; y < 220; y++)        // a long dark bar
            for (int x = 100; x < 500; x++)
                page[y * 600 + x] = 0;

        var tilted = new byte[600 * 400];
        Orientation.RotatePage(new GrayImage(600, 400, page), -3.0, tilted, 600);
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < 400; y++)
            for (int x = 0; x < 600; x++)
                if (tilted[y * 600 + x] < 128)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

        Span<double> got = stackalloc double[4];
        Orientation.UnrotateBox([100, 180, 500, 220], 3.0, 600, 400, got);
        // The mapped box covers the tilted bar in the input page.
        Assert.True(got[0] <= minX + 2 && got[2] >= maxX - 2, $"x: [{got[0]}, {got[2]}] vs [{minX}, {maxX}]");
        Assert.True(got[1] <= minY + 2 && got[3] >= maxY - 2, $"y: [{got[1]}, {got[3]}] vs [{minY}, {maxY}]");

        Span<double> flat = stackalloc double[4];
        Orientation.UnrotateBox([10, 10, 20, 20], 0.0, 600, 400, flat);
        Assert.Equal([10.0, 10.0, 20.0, 20.0], flat.ToArray());
    }

    // ------------------------------------------------------------------ math

    /// <summary><c>test_math_tokens_back_to_text</c>.</summary>
    [Fact]
    public void Math_tokens_back_to_text()
    {
        var toks = new List<string>();
        toks.AddRange("we have ".Select(c => c.ToString()));
        toks.AddRange(["$", "x", "_", "{", "i", "}", "\\leq", "1", "$"]);
        toks.AddRange(" here".Select(c => c.ToString()));
        Assert.Equal("we have $x _ { i } \\leq 1$ here", MathReader.Detokenize(toks));
        Assert.Equal("$\\alpha$", MathReader.Detokenize(["$", "\\alpha"]));   // an open span closes
        Assert.Equal("the bound", MathReader.Detokenize([" the", " bound"]));
    }

    /// <summary><c>test_math_models_are_optional</c>.</summary>
    [Fact]
    public void Math_models_are_optional()
    {
        string dir = Path.Combine(Path.GetTempPath(), "indra-ocr-math-optional");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "math_enc.onnx"), []);   // only some of the files

        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        Assert.Null(MathModels.Load(dir, options));
    }

    // ------------------------------------------------------------------ helpers

    private static (List<float[]> Left, List<float[]> Right) Columns()
    {
        var left = new List<float[]>();
        var right = new List<float[]>();
        for (int i = 0; i < 20; i++)
        {
            left.Add(Box(50, 100 + 15 * i, 290, 110 + 15 * i));
            right.Add(Box(310, 100 + 15 * i, 550, 110 + 15 * i));
        }

        return (left, right);
    }

    private static float[] Box(double x0, double y0, double x1, double y1)
        => [(float)x0, (float)y0, (float)x1, (float)y1];

    private static float[] Boxes(params float[][] boxes) => boxes.SelectMany(b => b).ToArray();

    private static float[] Boxes(IEnumerable<float[]> boxes) => boxes.SelectMany(b => b).ToArray();

    /// <summary>The reference's logits: the sign of the reading-position difference, times four.</summary>
    private static float[] PairLogits(int[] key)
    {
        int n = key.Length;
        var logits = new float[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                logits[i * n + j] = Math.Sign(key[j] - key[i]) * 4.0f;
        return logits;
    }

    private static float[] ReorderBoxes(float[] boxes, int[] order)
    {
        var result = new float[boxes.Length];
        for (int i = 0; i < order.Length; i++)
            Array.Copy(boxes, order[i] * 4, result, i * 4, 4);
        return result;
    }

    private static float[] ReorderMatrix(float[] matrix, int[] order, int n)
    {
        var result = new float[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                result[i * n + j] = matrix[order[i] * n + order[j]];
        return result;
    }

    /// <summary>The reference's <c>_box</c>-based run: characters laid out left to right.</summary>
    private static TextLine Run(string text, double x0, double y0, double y1, double w = 4.0)
    {
        var chars = new List<TextChar>();
        for (int i = 0; i < text.Length; i++)
            chars.Add(new TextChar(text[i].ToString(), [x0 + i * w, y0, x0 + (i + 1) * w, y1]));
        return new TextLine([x0, y0, x0 + w * text.Length, y1], text, chars);
    }

    /// <summary>A right-to-left run: logical order, drawn right to left from <paramref name="x0"/>.</summary>
    private static TextLine RtlRun(string text, double x0)
    {
        var chars = new List<TextChar>();
        for (int i = 0; i < text.Length; i++)
            chars.Add(new TextChar(text[i].ToString(),
                                   [x0 - (i + 1) * 4.0, 5, x0 - i * 4.0, 15]));
        return new TextLine([x0 - 4.0 * text.Length, 5, x0, 15], text, chars);
    }

    private static string Text(IEnumerable<RecToken> tokens)
        => string.Concat(tokens.Select(t => t.Character));

    /// <summary>A text line with no characters: all <c>readable</c> looks at is the text.</summary>
    private static TextLine Line(string text)
        => new(new double[4], text, Array.Empty<TextChar>());

    /// <summary>numpy's <c>rng.normal(0, 1)</c>, from a uniform double (Box-Muller).</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();   // (0, 1]
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>The reference's <c>sorted(tuple(c) for c in _cluster(L))</c>.</summary>
    private static List<List<int>> Groups(List<List<int>> blocks)
    {
        var groups = blocks.Select(b => b.OrderBy(i => i).ToList()).ToList();
        groups.Sort(CompareGroups);
        return groups;
    }

    private static int CompareGroups(List<int> a, List<int> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (a[i] != b[i])
                return a[i] - b[i];
        return a.Count - b.Count;
    }

    /// <summary>The reference's brute-force average linkage, on the same float32 logits.</summary>
    private static List<List<int>> Brute(float[] logits, int n)
    {
        var s = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                s[i, j] = logits[i * n + j];
        for (int i = 0; i < n; i++)
            s[i, i] = 0.0;

        var size = Enumerable.Repeat(1.0, n).ToArray();
        var alive = Enumerable.Repeat(true, n).ToArray();
        var members = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToArray();

        for (int step = 0; step + 1 < n; step++)
        {
            double best = double.NegativeInfinity;
            int a = -1, b = -1;
            for (int i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                for (int j = 0; j < n; j++)
                {
                    if (!alive[j] || i == j) continue;
                    double mean = s[i, j] / (size[i] * size[j]);
                    if (mean > best)
                    {
                        best = mean;
                        a = i;
                        b = j;
                    }
                }
            }

            if (a < 0 || best <= 0)
                break;

            for (int j = 0; j < n; j++) s[a, j] += s[b, j];
            for (int i = 0; i < n; i++) s[i, a] += s[i, b];
            s[a, a] = 0.0;
            size[a] += size[b];
            alive[b] = false;
            members[a].AddRange(members[b]);
        }

        var groups = new List<List<int>>();
        for (int i = 0; i < n; i++)
            if (alive[i])
            {
                members[i].Sort();
                groups.Add(members[i]);
            }

        groups.Sort(CompareGroups);
        return groups;
    }

    /// <summary>The reference's Orient stub: the content is turned one quarter turn.</summary>
    private sealed class QuarterTurnOrient : IOrientModel
    {
        public void Run(ReadOnlySpan<float> x, Span<float> p)
        {
            Assert.Equal(768 * 768, x.Length);
            p[0] = 0.01f;
            p[1] = 0.97f;
            p[2] = 0.01f;
            p[3] = 0.01f;
        }
    }

    /// <summary>The reference's Rec stub: one fixed token id per line, every other timestep.</summary>
    private sealed class FixedRec : IRecModel
    {
        private readonly long _token;

        public FixedRec(long token) => _token = token;

        public RecOutput Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height,
                             int width)
        {
            int t = width / 4;
            var ids = new long[batch * t];
            var conf = new float[batch * t];
            var lengths = new long[batch];
            for (int b = 0; b < batch; b++)
            {
                for (int i = 0; i < t; i += 2) ids[b * t + i] = _token;
                for (int i = 0; i < t; i++) conf[b * t + i] = 1.0f;
                lengths[b] = t;
            }

            return new RecOutput(ids, conf, lengths, t);
        }
    }

    /// <summary>The reference's Script stub: lines wider than 300 px "hold hanzi".</summary>
    private sealed class WideScript : IScriptModel
    {
        public float[] Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height,
                           int width)
        {
            var p = new float[batch];
            for (int b = 0; b < batch; b++)
                p[b] = widths[b] > 300 ? 1.0f : 0.0f;
            return p;
        }
    }
}
