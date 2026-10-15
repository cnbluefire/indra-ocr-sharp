using System.Text.Json;
using Indra.Ocr;
using Indra.Ocr.Layout;
using Indra.Ocr.Order;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for the page-layout half (<c>indra_ocr.layout_post</c>, see
/// <see cref="LayoutPost"/>) and for the Python <c>round</c> it uses (<see cref="Numerics"/>)
/// — see <c>docs/120-layout-post.md</c>.
/// </summary>
/// <remarks>
/// The fixture's expectations are the reference's own output. The synthetic pages in it hit every
/// block class (page/section headers, paragraphs with hyphen and CJK joins, list groups, the
/// geometric table with a <c>cells</c> map, the OrderNet table heads and the display-equation
/// path); the real cases replay the pages of <c>recognize.json</c> through the real
/// <c>order.onnx</c>, so no model file is needed here. Every fixture array is raw little-endian
/// bytes and the <c>round</c> corpus is raw ieee754 bit patterns, so the assertions compare bit
/// patterns rather than tolerances.
/// </remarks>
public class LayoutPostTests
{
    private static readonly JsonElement Fx = Fixtures.Load("layout_post.json");

    private static string Name(JsonElement c) => c.GetProperty("name").GetString()!;

    private static float[] Boxes(JsonElement c) => B64.ZFloats(c.GetProperty("boxes").GetString()!);

    private static float[] Tab(JsonElement c) => B64.ZFloats(c.GetProperty("tab").GetString()!);

    private static List<string> Texts(JsonElement c)
    {
        var list = new List<string>();
        foreach (var t in c.GetProperty("texts").EnumerateArray())
            list.Add(t.GetString()!);
        return list;
    }

    [Fact]
    public void RoundHalfEven_matches_python()
    {
        int cases = 0, negatives = 0, zeroes = 0;
        foreach (var c in Fx.GetProperty("round").EnumerateArray())
        {
            double x = B64.HexDouble(c.GetProperty("x").GetString()!);
            int digits = c.GetProperty("digits").GetInt32();
            double want = B64.HexDouble(c.GetProperty("expected").GetString()!);
            double got = Numerics.RoundHalfEven(x, digits);

            if (double.IsNaN(want))
            {
                Assert.True(double.IsNaN(got), $"round({x:R}, {digits}): expected nan, got {got:R}");
            }
            else if (BitConverter.DoubleToInt64Bits(want) != BitConverter.DoubleToInt64Bits(got))
            {
                Assert.Fail($"round({x:R}, {digits}): expected {want:R} " +
                            $"(0x{BitConverter.DoubleToInt64Bits(want):X16}), got {got:R} " +
                            $"(0x{BitConverter.DoubleToInt64Bits(got):X16})");
            }
            if (BitConverter.DoubleToInt64Bits(want) < 0) negatives++;
            if (want == 0.0) zeroes++;
            cases++;
        }
        Assert.True(cases >= 3000, $"only {cases} round cases in the fixture");
        Assert.True(negatives > 1000, $"only {negatives} round results are negative");
        Assert.True(zeroes > 0, "no round case rounds to zero");
    }

    [Fact]
    public void RoundHalfEven_edge_cases()
    {
        // The cases .NET's Math.Round gets wrong: Python rounds the *decimal* value of the double.
        Assert.Equal(0.1, Numerics.RoundHalfEven(0.15, 1));
        Assert.Equal(0.1, Numerics.RoundHalfEven(0.05, 1));
        Assert.Equal(2.67, Numerics.RoundHalfEven(2.675, 2));
        Assert.Equal(12.3, Numerics.RoundHalfEven(12.35, 1));
        Assert.Equal(123.5, Numerics.RoundHalfEven(123.45, 1));
        Assert.Equal(2.0, Numerics.RoundHalfEven(2.5, 0));
        Assert.Equal(4.0, Numerics.RoundHalfEven(3.5, 0));
        Assert.Equal(-2.0, Numerics.RoundHalfEven(-2.5, 0));
        Assert.True(double.IsNegative(Numerics.RoundHalfEven(-0.01, 1)));
        Assert.Equal(1e308, Numerics.RoundHalfEven(1e308, 4));
        Assert.Equal(1e308, Numerics.RoundHalfEven(1e308, 0));
        Assert.True(double.IsPositiveInfinity(Numerics.RoundHalfEven(double.PositiveInfinity, 2)));
        Assert.True(double.IsNaN(Numerics.RoundHalfEven(double.NaN, 2)));
    }

    [Fact]
    public void JoinLines_matches_reference()
    {
        int cases = 0, hyphen = 0, cjk = 0;
        foreach (var c in Fx.GetProperty("join_lines").EnumerateArray())
        {
            var texts = new List<string>();
            foreach (var t in c.GetProperty("texts").EnumerateArray())
                texts.Add(t.GetString()!);
            string expected = c.GetProperty("expected").GetString()!;
            string got = LayoutPost.JoinLines(texts);
            Assert.Equal(expected, got);
            if (expected.Contains("tumorigenesis") || expected.Contains("hyphenated"))
                hyphen++;
            if (expected.Contains('\u4e00') || expected.Contains('\u3042'))
                cjk++;
            cases++;
        }
        Assert.True(cases >= 20, $"only {cases} join_lines cases in the fixture");
        Assert.True(hyphen + cjk > 0);
    }

    [Fact]
    public void JoinLines_strips_like_python()
    {
        // Python's str.strip() (the port's UnicodeText.Strip) removes 0x1c..0x1f and \x85 / \xa0.
        Assert.Equal("first second", LayoutPost.JoinLines(["\u001cfirst\u001c", "\u001f second \u001f"]));
        Assert.Equal("text joined", LayoutPost.JoinLines(["\u00a0text\u00a0", "\u0085joined\u0085"]));
        Assert.Equal("", LayoutPost.JoinLines(["\u001c\u001d", "", "  "]));
    }

    [Fact]
    public void BalanceMath_matches_reference()
    {
        int cases = 0, opened = 0, closed = 0;
        foreach (var c in Fx.GetProperty("balance_math").EnumerateArray())
        {
            string text = c.GetProperty("text").GetString()!;
            string expected = c.GetProperty("expected").GetString()!;
            string got = LayoutPost.BalanceMath(text);
            Assert.Equal(expected, got);
            if (expected.Contains('$') && expected != text) closed++;
            if (got.Length > text.Length) opened++;
            cases++;
        }
        Assert.True(cases >= 20, $"only {cases} balance_math cases in the fixture");
        Assert.True(opened > 0 && closed > 0);
    }

    [Fact]
    public void SplitBlocks_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("split_blocks").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var order = Fixtures.IntArray(c.GetProperty("order"));
            var same = B64.ZFloats(c.GetProperty("same").GetString()!);
            var want = Groups(c.GetProperty("expected"));
            AssertGroups(want, LayoutPost.SplitBlocks(order, same, n), name);
            cases++;
        }
        Assert.True(cases >= 8, $"only {cases} split_blocks cases in the fixture");
    }

    [Fact]
    public void Rows_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("rows").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var want = Groups(c.GetProperty("expected"));
            AssertGroups(want, LayoutPost.Rows(Boxes(c), n), name);
            cases++;
        }
        Assert.True(cases >= 10, $"only {cases} rows cases in the fixture");
    }

    [Fact]
    public void MergeWrapped_matches_reference()
    {
        int cases = 0, merged = 0;
        foreach (var c in Fx.GetProperty("merge_wrapped").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var rows = Groups(c.GetProperty("rows"));
            var want = Groups(c.GetProperty("expected"));
            var got = LayoutPost.MergeWrapped(rows, Boxes(c), n);
            AssertGroups(want, got, name);
            if (got.Count < rows.Count)
                merged++;
            cases++;
        }
        Assert.True(cases >= 5, $"only {cases} merge_wrapped cases in the fixture");
        Assert.True(merged > 0, "no merge_wrapped case actually merged a row");
    }

    [Fact]
    public void TableHtml_matches_reference()
    {
        int cases = 0, spanned = 0, titled = 0, escaped = 0;
        foreach (var c in Fx.GetProperty("table_html").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var texts = Texts(c);
            string expected = c.GetProperty("expected").GetString()!;
            string got = LayoutPost.TableHtml(Boxes(c), n, texts);
            Assert.Equal(expected, got);
            if (expected.Contains("colspan")) spanned++;
            if (!expected.StartsWith("<table>")) titled++;
            if (expected.Contains("&amp;") || expected.Contains("&lt;") || expected.Contains("&gt;"))
                escaped++;
            cases++;
        }
        Assert.True(cases >= 8, $"only {cases} table_html cases in the fixture");
        Assert.True(spanned > 0 || titled > 0);
        Assert.True(escaped > 0, "no table case exercises html.escape");
    }

    [Fact]
    public void HeadsPlausible_matches_reference()
    {
        int cases = 0, yes = 0, no = 0;
        foreach (var c in Fx.GetProperty("heads_plausible").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            bool expected = c.GetProperty("expected").GetBoolean();
            bool got = LayoutPost.HeadsPlausible(Boxes(c), n, Tab(c));
            Assert.True(expected == got, $"{name}: expected {expected}, got {got}");
            if (expected) yes++;
            else no++;
            cases++;
        }
        Assert.True(cases >= 10, $"only {cases} heads_plausible cases in the fixture");
        Assert.True(yes > 0 && no > 0, "the fixture must cover both verdicts");
    }

    [Fact]
    public void TableHtmlHeads_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("table_html_heads").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            string expected = c.GetProperty("expected").GetString()!;
            string got = LayoutPost.TableHtmlHeads(Boxes(c), n, Texts(c), Tab(c));
            Assert.Equal(expected, got);
            cases++;
        }
        Assert.True(cases >= 10, $"only {cases} table_html_heads cases in the fixture");
    }

    [Fact]
    public void PageMarkdown_matches_reference()
    {
        int cases = 0, withCells = 0, withTab = 0, kept = 0;
        foreach (var c in Fx.GetProperty("page_markdown").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var boxes = Boxes(c);
            var texts = Texts(c);
            var order = Fixtures.IntArray(c.GetProperty("order"));
            var cls = B64.ZFloats(c.GetProperty("cls").GetString()!);
            var same = B64.ZFloats(c.GetProperty("same").GetString()!);
            bool keep = c.GetProperty("keep_furniture").GetBoolean();
            float[]? tab = c.GetProperty("tab").ValueKind == JsonValueKind.Null ? null : Tab(c);
            var cells = Cells(c);

            string expected = c.GetProperty("expected").GetString()!;
            string got = LayoutPost.PageMarkdown(boxes, n, texts, order, cls, same, keep, cells, tab);
            Assert.Equal(expected, got);

            // the same inputs through page_blocks
            var wantBlocks = c.GetProperty("expected_blocks");
            var blocks = LayoutPost.PageBlocks(boxes, n, texts, order, cls, same);
            Assert.Equal(wantBlocks.GetArrayLength(), blocks.Count);
            int b = 0;
            foreach (var wb in wantBlocks.EnumerateArray())
            {
                var block = blocks[b++];
                Assert.Equal(wb.GetProperty("label").GetString(), block.Label);
                Assert.Equal(wb.GetProperty("text").GetString(), block.Text);
                var want = Fixtures.DoubleArray(wb.GetProperty("bbox"));
                Assert.Equal(want.Length, block.Bbox.Length);
                for (int i = 0; i < want.Length; i++)
                    if (BitConverter.DoubleToInt64Bits(want[i]) !=
                        BitConverter.DoubleToInt64Bits(block.Bbox[i]))
                        Assert.Fail($"{name} bbox[{i}]: expected {want[i]:R}, got {block.Bbox[i]:R}");
            }

            if (cells is not null) withCells++;
            if (tab is not null) withTab++;
            if (keep) kept++;
            cases++;
        }
        Assert.True(cases >= 25, $"only {cases} page_markdown cases in the fixture");
        Assert.True(withCells >= 2 && withTab >= 2 && kept >= 2,
                    $"coverage: {withCells} cells, {withTab} tab, {kept} keep_furniture");
    }

    [Fact]
    public void PageMarkdown_reaches_every_block_class()
    {
        var c = Find("classes");
        string md = LayoutPost.PageMarkdown(Boxes(c), c.GetProperty("n").GetInt32(), Texts(c),
                                           Fixtures.IntArray(c.GetProperty("order")),
                                           B64.ZFloats(c.GetProperty("cls").GetString()!),
                                           B64.ZFloats(c.GetProperty("same").GetString()!),
                                           false, Cells(c));
        Assert.Contains("## 1 Introduction", md);              // section header
        Assert.Contains("hyphenated word", md);               // paragraph join
        Assert.Contains("\u2022 first item carried on", md);  // list group
        Assert.Contains("<table>", md);                       // table
        Assert.DoesNotContain("Quarterly report", md);        // page header dropped (furniture)
        Assert.DoesNotContain("Draft - page 1", md);          // footer dropped (furniture)
        string kept = LayoutPost.PageMarkdown(Boxes(c), c.GetProperty("n").GetInt32(), Texts(c),
                                             Fixtures.IntArray(c.GetProperty("order")),
                                             B64.ZFloats(c.GetProperty("cls").GetString()!),
                                             B64.ZFloats(c.GetProperty("same").GetString()!),
                                             true, Cells(c));
        Assert.Contains("Quarterly report", kept);
        Assert.Contains("Draft - page 1", kept);
    }

    [Fact]
    public void PageMarkdown_degenerate_inputs()
    {
        var c = Find("empty");
        int n = c.GetProperty("n").GetInt32();
        Assert.Equal(0, n);
        string md = LayoutPost.PageMarkdown(Boxes(c), n, Texts(c),
                                           Fixtures.IntArray(c.GetProperty("order")),
                                           B64.ZFloats(c.GetProperty("cls").GetString()!),
                                           B64.ZFloats(c.GetProperty("same").GetString()!));
        Assert.Equal("", md);
        Assert.Empty(LayoutPost.PageBlocks(Boxes(c), n, Texts(c),
                                          Fixtures.IntArray(c.GetProperty("order")),
                                          B64.ZFloats(c.GetProperty("cls").GetString()!),
                                          B64.ZFloats(c.GetProperty("same").GetString()!)));

        // a single line, and a table class with one line: no table is built
        var boxes = new float[] { 0f, 0f, 100f, 20f };
        var cls = new float[10];
        cls[Array.IndexOf(OrderPost.BlockClasses, "Table")] = 1f;
        var same = new float[] { 0f };
        string one = LayoutPost.PageMarkdown(boxes, 1, ["only line"], [0], cls, same);
        Assert.Equal("only line", one);
        string tabled = LayoutPost.PageMarkdown(boxes, 1, ["only line"], [0], cls, same,
                                               false, null, new float[3]);
        Assert.Equal("only line", tabled);
    }

    private static JsonElement Find(string name)
    {
        foreach (var c in Fx.GetProperty("page_markdown").EnumerateArray())
            if (Name(c) == name)
                return c;
        Assert.Fail($"no page_markdown case named '{name}'");
        throw new InvalidOperationException();
    }

    private static Dictionary<int, IReadOnlyList<TableCell>>? Cells(JsonElement c)
    {
        var e = c.GetProperty("cells");
        if (e.ValueKind == JsonValueKind.Null)
            return null;
        var cells = new Dictionary<int, IReadOnlyList<TableCell>>();
        foreach (var item in e.EnumerateArray())
        {
            var boxes = B64.ZFloats(item.GetProperty("boxes").GetString()!);
            var list = new List<TableCell>();
            int k = 0;
            foreach (var t in item.GetProperty("texts").EnumerateArray())
            {
                list.Add(new TableCell(boxes[k * 4], boxes[k * 4 + 1], boxes[k * 4 + 2],
                                       boxes[k * 4 + 3], t.GetString()!));
                k++;
            }
            cells[item.GetProperty("line").GetInt32()] = list;
        }
        return cells;
    }

    private static List<List<int>> Groups(JsonElement e)
    {
        var groups = new List<List<int>>();
        foreach (var g in e.EnumerateArray())
            groups.Add([.. Fixtures.IntArray(g)]);
        return groups;
    }

    private static void AssertGroups(List<List<int>> want, List<List<int>> got, string tag)
    {
        if (want.Count != got.Count)
            Assert.Fail($"{tag}: expected {want.Count} groups, got {got.Count}\n" +
                        $"  expected {Show(want)}\n  got      {Show(got)}");
        for (int i = 0; i < want.Count; i++)
            if (!want[i].SequenceEqual(got[i]))
                Assert.Fail($"{tag} group {i}: expected [{string.Join(',', want[i])}], " +
                            $"got [{string.Join(',', got[i])}]\n" +
                            $"  expected {Show(want)}\n  got      {Show(got)}");
    }

    private static string Show(List<List<int>> groups) =>
        "[" + string.Join(", ", groups.Select(g => "[" + string.Join(',', g) + "]")) + "]";
}
