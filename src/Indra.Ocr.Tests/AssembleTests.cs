using System.Text.Json;
using Indra.Ocr.Order;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Step 130: <c>pipeline.OrtOCR.assemble</c> (with <c>gutter_cuts</c>, <c>split_line</c>,
/// <c>_decode</c>, <c>_is_space</c>) against <c>fixtures/assemble.json</c>, generated from the
/// Python reference by <c>tools/scripts/test/gen_assemble_fixtures.py</c>.
/// </summary>
/// <remarks>
/// The whole-page cases drive <see cref="Assembler"/> with a table-backed <see cref="IOrderModel"/>:
/// the fixture records the logits the reference's order model (the real <c>order.onnx</c> where the
/// pages are real, a fixed table where the case forces a class) returned for each line count, so
/// these tests do not need the model files.
/// </remarks>
public class AssembleTests
{
    private static readonly JsonElement Fx = Fixtures.Load("assemble.json");
    private static readonly Vocab Vocab = ReadVocab();

    private static Vocab ReadVocab()
    {
        var chars = new List<string>();
        foreach (var e in Fx.GetProperty("vocab").EnumerateArray())
            chars.Add(e.GetString()!);
        return new Vocab(chars);
    }

    // ------------------------------------------------------------------ helpers

    private static RecToken Token(JsonElement e) => new(
        e.TryGetProperty("id", out var id) ? id.GetInt64() : 0L,
        e.TryGetProperty("char", out var ch) ? ch.GetString() : null,
        e.GetProperty("x").GetDouble(),
        e.TryGetProperty("content", out var c) ? c.GetInt32() : null);

    private static List<RecToken> Tokens(JsonElement e)
    {
        var list = new List<RecToken>();
        foreach (var t in e.EnumerateArray())
            list.Add(Token(t));
        return list;
    }

    private static List<RecognizedLine> ReadRec(JsonElement e)
    {
        var list = new List<RecognizedLine>();
        foreach (var line in e.EnumerateArray())
            list.Add(new RecognizedLine(line.GetProperty("text").GetString()!,
                                        line.GetProperty("conf").GetDouble(),
                                        Tokens(line.GetProperty("tokens"))));
        return list;
    }

    /// <summary>Exact equality, but signed zero must match too (0.0 vs -0.0 swaps bits).</summary>
    private static void Same(double expected, double actual)
    {
        Assert.True(expected.Equals(actual) &&
                    (expected != 0 || BitConverter.DoubleToInt64Bits(expected) ==
                                      BitConverter.DoubleToInt64Bits(actual)),
                    $"expected {expected:R}, got {actual:R}");
    }

    private static void SameFloats(JsonElement expected, IReadOnlyList<float> actual)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Count);
        int i = 0;
        foreach (var v in expected.EnumerateArray())
            Assert.Equal(v.GetSingle(), actual[i++]);
    }

    /// <summary>The fixture's order model: fixed outputs looked up by line count.</summary>
    private sealed class FixtureOrderModel : IOrderModel
    {
        private readonly Dictionary<int, OrderOutput> _outputs = new();
        private readonly string _case;

        public FixtureOrderModel(JsonElement model, string @case = "?")
        {
            _case = @case;
            if (model.ValueKind != JsonValueKind.Object)
                return;
            foreach (var o in model.GetProperty("outputs").EnumerateArray())
            {
                static float[]? Arr(JsonElement o, string name) =>
                    o.GetProperty(name).ValueKind == JsonValueKind.Null
                        ? null
                        : B64.ZFloats(o.GetProperty(name).GetString()!);
                _outputs[o.GetProperty("n").GetInt32()] = new OrderOutput(
                    Arr(o, "a")!, Arr(o, "cls"), Arr(o, "same"), Arr(o, "tab"));
            }
        }

        public OrderOutput Run(ReadOnlySpan<float> feats, int n) =>
            _outputs.TryGetValue(n, out var o)
                ? o
                : throw new InvalidOperationException(
                    $"{_case}: the fixture has no order-model output for n = {n}; the test's call " +
                    "pattern must match the generator's");
    }

    // ------------------------------------------------------------------ the flat pieces

    [Fact]
    public void Is_space_matches_python()
    {
        var asm = new Assembler(null, false, Vocab);
        foreach (var c in Fx.GetProperty("is_space").EnumerateArray())
        {
            bool expected = c.GetProperty("expected").GetBoolean();
            Assert.True(expected == asm.IsSpace(Token(c.GetProperty("token"))),
                        $"is_space({c.GetProperty("name").GetString()}) should be {expected}");
        }
    }

    [Fact]
    public void Decode_matches_python()
    {
        var asm = new Assembler(null, false, Vocab);
        foreach (var c in Fx.GetProperty("decode").EnumerateArray())
            Assert.Equal(c.GetProperty("expected").GetString(),
                         asm.Decode(Tokens(c.GetProperty("tokens"))));
    }

    [Fact]
    public void Split_line_matches_python()
    {
        var asm = new Assembler(null, false, Vocab);
        foreach (var c in Fx.GetProperty("split_line").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var box = B64.ZFloats(c.GetProperty("box").GetString()!);
            var toks = Tokens(c.GetProperty("tokens"));
            double gap = c.GetProperty("gap").GetDouble();
            double[]? at = c.GetProperty("at").ValueKind == JsonValueKind.Null
                ? null
                : Fixtures.DoubleArray(c.GetProperty("at"));
            var parts = asm.SplitLine(box, toks, gap, at);

            var expected = c.GetProperty("expected");
            Assert.True(expected.GetArrayLength() == parts.Count,
                        $"{name}: {parts.Count} parts, expected {expected.GetArrayLength()}");
            int i = 0;
            foreach (var e in expected.EnumerateArray())
            {
                var part = parts[i++];
                SameFloats(e.GetProperty("box"), part.Box);
                Assert.Equal(e.GetProperty("text").GetString(), part.Text);
                var wantToks = Tokens(e.GetProperty("tokens"));
                Assert.Equal(wantToks.Count, part.Tokens.Count);
                for (int j = 0; j < wantToks.Count; j++)
                {
                    Assert.Equal(wantToks[j].Id, part.Tokens[j].Id);
                    Assert.Equal(wantToks[j].Character, part.Tokens[j].Character);
                    Same(wantToks[j].X, part.Tokens[j].X);
                    Assert.Equal(wantToks[j].ContentIndex, part.Tokens[j].ContentIndex);
                }
            }
        }
    }

    [Fact]
    public void Gutter_cuts_matches_python()
    {
        var asm = new Assembler(null, false, Vocab);
        foreach (var c in Fx.GetProperty("gutter_cuts").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var boxes = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            int n = c.GetProperty("n").GetInt32();
            var rec = ReadRec(c.GetProperty("rec"));
            var cuts = asm.GutterCuts(boxes, n, rec);

            var expected = c.GetProperty("expected");
            Assert.Equal(expected.GetArrayLength(), cuts.Count);
            int i = 0;
            foreach (var e in expected.EnumerateArray())
            {
                var want = Fixtures.DoubleArray(e);
                Assert.True(want.Length == cuts[i].Length,
                            $"{name}: cut {i} has {cuts[i].Length} positions, expected {want.Length}");
                for (int j = 0; j < want.Length; j++)
                    Same(want[j], cuts[i][j]);
                i++;
            }
        }
    }

    [Fact]
    public void Order_backtracks_matches_python()
    {
        foreach (var c in Fx.GetProperty("order_backtracks").EnumerateArray())
        {
            var boxes = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            Assert.Equal(c.GetProperty("expected").GetInt32(),
                         Assembler.OrderBacktracks(boxes, boxes.Length / 4));
        }
    }

    [Fact]
    public void Pdf_order_coherent_matches_python()
    {
        foreach (var c in Fx.GetProperty("pdf_order_coherent").EnumerateArray())
        {
            var boxes = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            int n = c.GetProperty("n").GetInt32();
            var logits = c.GetProperty("logits").ValueKind == JsonValueKind.Null
                ? Array.Empty<float>()
                : B64.ZFloats(c.GetProperty("logits").GetString()!);
            Assert.True(c.GetProperty("expected").GetBoolean() ==
                        Assembler.PdfOrderCoherent(boxes, n, logits),
                        c.GetProperty("name").GetString());
        }
    }

    // ------------------------------------------------------------------ whole pages

    [Fact]
    public void Assemble_matches_python()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("assemble").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var boxes = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            int n = c.GetProperty("n").GetInt32();
            var rec = ReadRec(c.GetProperty("rec"));
            var wh = c.GetProperty("page");
            var modelJson = c.GetProperty("model");
            FixtureOrderModel? model = modelJson.ValueKind == JsonValueKind.Null
                ? null
                : new FixtureOrderModel(modelJson, name);
            var asm = new Assembler(model, c.GetProperty("table_heads").GetBoolean(), Vocab,
                                    c.GetProperty("furniture").GetBoolean());
            var timings = new Dictionary<string, double>();
            var page = asm.Run(wh[0].GetDouble(), wh[1].GetDouble(), boxes, n, rec, timings,
                               c.GetProperty("content_order").GetBoolean());

            var exp = c.GetProperty("expected");
            var expLines = exp.GetProperty("lines");
            Assert.True(expLines.GetArrayLength() == page.Lines.Count,
                        $"{name}: {page.Lines.Count} lines, expected {expLines.GetArrayLength()}");
            int k = 0;
            foreach (var el in expLines.EnumerateArray())
            {
                var line = page.Lines[k++];
                var box = Fixtures.DoubleArray(el.GetProperty("box"));
                for (int j = 0; j < 4; j++)
                    Same(box[j], line.Box[j]);
                Assert.Equal(el.GetProperty("text").GetString(), line.Text);
                Same(el.GetProperty("conf").GetDouble(), line.Confidence);
            }
            Assert.Equal(exp.GetProperty("text").GetString(), page.Text);
            Assert.Equal(exp.GetProperty("note").GetString(), page.Note);
            Assert.Equal(exp.GetProperty("source").GetString(), page.Source);
            var md = exp.GetProperty("markdown");
            Assert.Equal(md.ValueKind == JsonValueKind.Null ? null : md.GetString(), page.Markdown);

            var expBlocks = exp.GetProperty("blocks");
            Assert.True(expBlocks.GetArrayLength() == page.Blocks.Count,
                        $"{name}: {page.Blocks.Count} blocks, expected {expBlocks.GetArrayLength()}");
            k = 0;
            foreach (var eb in expBlocks.EnumerateArray())
            {
                var block = page.Blocks[k++];
                Assert.Equal(eb.GetProperty("label").GetString(), block.Label);
                var bbox = Fixtures.DoubleArray(eb.GetProperty("bbox"));
                for (int j = 0; j < 4; j++)
                    Same(bbox[j], block.Bbox[j]);
                Assert.Equal(eb.GetProperty("text").GetString(), block.Text);
            }

            Assert.True(timings.ContainsKey("order_ms"), $"{name}: no order_ms");
            Assert.True(timings.ContainsKey("total_ms"), $"{name}: no total_ms");
            Assert.True(page.Timings.ContainsKey("total_ms"), $"{name}: the page lost the timings");
            cases++;
        }
        Assert.Equal(Fx.GetProperty("assemble").GetArrayLength(), cases);
    }

    /// <summary>The reference's constants have to be the ones the fixture was generated with.</summary>
    [Fact]
    public void Constants_match_python()
    {
        Assert.Equal(3.0, Assembler.SplitGap);
        Assert.Equal(1.8, Assembler.CellGap);
        Assert.Equal(1.6, Assembler.GutterGap);
        Assert.Equal(3, Assembler.GutterEdges);
        Assert.Equal(0.85, Assembler.MinModelAgreement);
        Assert.Equal(Fx.GetProperty("space_id").GetInt32(), Vocab.Index[" "]);
        Assert.Equal(Fx.GetProperty("vocab").GetArrayLength(), Vocab.Characters.Length);
        var classes = Fx.GetProperty("block_classes");
        Assert.Equal(classes.GetArrayLength(), OrderPost.BlockClasses.Length);
        int i = 0;
        foreach (var c in classes.EnumerateArray())
            Assert.Equal(c.GetString(), OrderPost.BlockClasses[i++]);
    }

    /// <summary>An empty page still decodes, and a page with no model never asks for one.</summary>
    [Fact]
    public void Empty_page_has_no_lines()
    {
        var asm = new Assembler(null, false, Vocab);
        var page = asm.Run(400, 200, Array.Empty<float>(), 0, Array.Empty<RecognizedLine>(),
                           new Dictionary<string, double>());
        Assert.Empty(page.Lines);
        Assert.Equal("", page.Markdown);
        Assert.Equal("", page.Text);
    }
}
