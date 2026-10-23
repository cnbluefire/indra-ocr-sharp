using System.Text.Json;
using Indra.Ocr;
using Indra.Ocr.Order;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Bit-parity tests for the torch-free reading-order half (<c>indra_ocr.order_post</c>, see
/// <see cref="OrderPost"/>) and for the float32 <c>np.log</c> kernel it leans on
/// (<see cref="NpyLog"/>) — see <c>docs/110-order-post.md</c>.
/// </summary>
/// <remarks>
/// The fixture's expectations are the reference's own output. The real cases replay the pages
/// recorded in <c>recognize.json</c> (real detector boxes + real recognizer texts) and the
/// order logits the real <c>order.onnx</c> produced for them, so no model file is needed here;
/// the synthetic cases cover the branches those pages do not reach (single boxes, exact ties,
/// overlapping boxes, degenerate sizes, every text-feature marker). All fixture arrays are raw
/// little-endian floats, so the assertions compare bit patterns.
/// </remarks>
public class OrderPostTests
{
    private static readonly JsonElement Fx = Fixtures.Load("order_post.json");
    private static readonly JsonElement Uni = Fixtures.Load("unicode_predicates.json");

    private static float[] Boxes(JsonElement c) => B64.ZFloats(c.GetProperty("boxes").GetString()!);

    private static string Name(JsonElement c) => c.GetProperty("name").GetString()!;

    [Fact]
    public void LineFeatures_matches_reference()
    {
        int cases = 0, withText = 0;
        foreach (var c in Fx.GetProperty("features").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var boxes = Boxes(c);
            var want = B64.ZFloats(c.GetProperty("expected").GetString()!);
            double w = c.GetProperty("page")[0].GetDouble();
            double h = c.GetProperty("page")[1].GetDouble();
            List<string>? texts = null;
            if (c.TryGetProperty("texts", out var te))
            {
                texts = [];
                foreach (var t in te.EnumerateArray())
                    texts.Add(t.GetString()!);
                withText++;
            }

            var got = OrderPost.LineFeatures(boxes, n, w, h, texts);
            Assert.Equal(n * OrderPost.FeatureCount, got.Length);
            AssertFloats(want, got, name);
            cases++;
        }
        Assert.True(cases >= 18, $"only {cases} feature cases in the fixture");
        Assert.True(withText >= 12, $"only {withText} feature cases carry texts");
    }

    [Fact]
    public void DecodeOrder_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("decode_order").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var a = B64.ZFloats(c.GetProperty("a").GetString()!);
            var want = Fixtures.IntArray(c.GetProperty("expected"));
            AssertOrder(want, OrderPost.DecodeOrder(a, n), name);
            cases++;
        }
        Assert.True(cases >= 20, $"only {cases} decode_order cases in the fixture");
    }

    [Fact]
    public void OrderTopDown_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("top_down").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var want = Fixtures.IntArray(c.GetProperty("expected"));
            AssertOrder(want, OrderPost.OrderTopDown(Boxes(c), n), name);
            cases++;
        }
        Assert.True(cases >= 10, $"only {cases} top_down cases in the fixture");
    }

    [Fact]
    public void OrderXyCut_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("xy_cut").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var want = Fixtures.IntArray(c.GetProperty("expected"));
            AssertOrder(want, OrderPost.OrderXyCut(Boxes(c), n), name);
            cases++;
        }
        Assert.True(cases >= 10, $"only {cases} xy_cut cases in the fixture");
    }

    [Fact]
    public void Cluster_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("cluster").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var same = B64.ZFloats(c.GetProperty("same").GetString()!);
            var got = OrderPost.Cluster(same, n);
            var want = new List<int[]>();
            foreach (var block in c.GetProperty("expected").EnumerateArray())
                want.Add(Fixtures.IntArray(block));

            Assert.Equal(want.Count, got.Count);
            for (int i = 0; i < want.Count; i++)
            {
                Assert.Equal(want[i].Length, got[i].Count);
                for (int k = 0; k < want[i].Length; k++)
                    if (want[i][k] != got[i][k])
                        Assert.Fail($"{name}: block {i} member {k}: expected {want[i][k]}, got {got[i][k]}");
            }
            cases++;
        }
        Assert.True(cases >= 10, $"only {cases} cluster cases in the fixture");
    }

    [Fact]
    public void DecodeBlocksOrder_matches_reference()
    {
        int cases = 0;
        foreach (var c in Fx.GetProperty("decode_blocks").EnumerateArray())
        {
            string name = Name(c);
            int n = c.GetProperty("n").GetInt32();
            var a = B64.ZFloats(c.GetProperty("a").GetString()!);
            var same = B64.ZFloats(c.GetProperty("same").GetString()!);
            var cls = B64.ZFloats(c.GetProperty("cls").GetString()!);
            var want = Fixtures.IntArray(c.GetProperty("expected"));
            AssertOrder(want, OrderPost.DecodeBlocksOrder(a, same, Boxes(c), n, cls), name);
            cases++;
        }
        Assert.True(cases >= 15, $"only {cases} decode_blocks cases in the fixture");
    }

    [Fact]
    public void Degenerate_inputs()
    {
        Assert.Empty(OrderPost.DecodeOrder([], 0));
        Assert.Empty(OrderPost.OrderTopDown([], 0));
        Assert.Empty(OrderPost.OrderXyCut([], 0));
        Assert.Empty(OrderPost.Cluster([], 0));
        Assert.Empty(OrderPost.LineFeatures([], 0, 700, 900));
        Assert.Equal([0], OrderPost.DecodeOrder([0f], 1));
        Assert.Equal([0], OrderPost.DecodeBlocksOrder([0f], [0f], [0f, 0f, 1f, 1f], 1, new float[10]));

        // cls is documented as [n, 10]; anything shorter would read past the block.
        Assert.Throws<ArgumentException>(() => OrderPost.DecodeBlocksOrder(
            new float[4], new float[4], new float[8], 2, new float[10]));
    }

    [Fact]
    public void NpyLog_matches_numpy()
    {
        var p = Fx.GetProperty("npylog");
        var inputs = Fixtures.FloatArray(p.GetProperty("inputs"));
        var want = Fixtures.FloatArray(p.GetProperty("expected"));

        AssertFloats(want, NpyLog.Log(inputs), "npylog");

        // The scalar entry point must agree with the vectorised one element by element.
        for (int i = 0; i < inputs.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(want[i]),
                BitConverter.SingleToInt32Bits(NpyLog.Log(inputs[i])));

        // The values JSON cannot carry: numpy's kernel returns -inf for both zeroes, -NPY_NANF
        // for anything negative (including -inf), NPY_NANF for NaN (either sign) and +inf for +inf.
        Assert.Equal(unchecked((int)0xFF800000), Bits(NpyLog.Log(0f)));
        Assert.Equal(unchecked((int)0xFF800000), Bits(NpyLog.Log(-0f)));
        Assert.Equal(unchecked((int)0xFFC00000), Bits(NpyLog.Log(-1f)));
        Assert.Equal(unchecked((int)0xFFC00000), Bits(NpyLog.Log(-2.5f)));
        Assert.Equal(unchecked((int)0xFFC00000), Bits(NpyLog.Log(float.NegativeInfinity)));
        Assert.Equal(unchecked((int)0x7FC00000), Bits(NpyLog.Log(float.NaN)));
        Assert.Equal(unchecked((int)0x7FC00000), Bits(NpyLog.Log(-float.NaN)));
        Assert.Equal(unchecked((int)0x7F800000), Bits(NpyLog.Log(float.PositiveInfinity)));
        Assert.Equal(0, Bits(NpyLog.Log(1f)));
    }

    [Fact]
    public void Unicode_predicates_match_python()
    {
        var digit = Ranges(Uni.GetProperty("digit"));
        var upper = Ranges(Uni.GetProperty("upper"));
        var space = Ranges(Uni.GetProperty("space"));
        var lower = Ranges(Uni.GetProperty("lower"));
        var alpha = Ranges(Uni.GetProperty("alpha"));

        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            bool wantDigit = InRanges(digit, cp);
            bool wantUpper = InRanges(upper, cp);
            bool wantSpace = InRanges(space, cp);
            bool wantLower = InRanges(lower, cp);
            bool wantAlpha = InRanges(alpha, cp);

            if (UnicodeText.IsDigitCodePoint(cp) != wantDigit)
                Assert.Fail($"isdigit(chr(0x{cp:X4})): expected {wantDigit}");
            if (UnicodeText.IsUpperCodePoint(cp) != wantUpper)
                Assert.Fail($"isupper(chr(0x{cp:X4})): expected {wantUpper}");
            if (UnicodeText.IsSpaceCodePoint(cp) != wantSpace)
                Assert.Fail($"isspace(chr(0x{cp:X4})): expected {wantSpace}");
            if (UnicodeText.IsLowerCodePoint(cp) != wantLower)
                Assert.Fail($"islower(chr(0x{cp:X4})): expected {wantLower}");
            if (UnicodeText.IsAlphaCodePoint(cp) != wantAlpha)
                Assert.Fail($"isalpha(chr(0x{cp:X4})): expected {wantAlpha}");

            string s = cp >= 0x10000 ? char.ConvertFromUtf32(cp) : ((char)cp).ToString();
            if (UnicodeText.CodePointCount(s) != 1)
                Assert.Fail($"len(chr(0x{cp:X4})) != 1");
            if (UnicodeText.IsDigit(s) != wantDigit)
                Assert.Fail($"str.isdigit(chr(0x{cp:X4})): expected {wantDigit}");
            if (UnicodeText.IsSpace(s) != wantSpace)
                Assert.Fail($"str.isspace(chr(0x{cp:X4})): expected {wantSpace}");
            // str.strip() removes exactly the isspace() code points, from both ends
            if (UnicodeText.Strip(s + "a" + s) != (wantSpace ? "a" : s + "a" + s))
                Assert.Fail($"str.strip() with chr(0x{cp:X4}): expected {wantSpace}");
        }
    }

    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x);

    private static void AssertFloats(float[] want, float[] got, string tag)
    {
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++)
            if (Bits(want[i]) != Bits(got[i]))
                Assert.Fail($"{tag}[{i}]: expected {want[i]:R} (0x{(uint)Bits(want[i]):X8}), " +
                            $"got {got[i]:R} (0x{(uint)Bits(got[i]):X8})");
    }

    private static void AssertOrder(int[] want, int[] got, string tag)
    {
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++)
            if (want[i] != got[i])
                Assert.Fail($"{tag}[{i}]: expected {want[i]}, got {got[i]}\n" +
                            $"  expected [{string.Join(',', want)}]\n" +
                            $"  got      [{string.Join(',', got)}]");
    }

    /// <summary>Reads a list of <c>[lo, hi]</c> code point ranges as inclusive pairs.</summary>
    private static (int Lo, int Hi)[] Ranges(JsonElement e)
    {
        var a = new (int, int)[e.GetArrayLength()];
        int i = 0;
        foreach (var r in e.EnumerateArray())
            a[i++] = (r[0].GetInt32(), r[1].GetInt32());
        return a;
    }

    private static bool InRanges((int Lo, int Hi)[] ranges, int cp)
    {
        foreach (var (lo, hi) in ranges)
            if (lo <= cp && cp <= hi)
                return true;
        return false;
    }
    [Fact]
    public void Cjk_display_width_and_sentence_end_match_1_1()
    {
        float[] boxes = [0, 0, 100, 20];
        float[] latin = OrderPost.LineFeatures(boxes, 1, 100, 100, ["ab."]);
        float[] cjk = OrderPost.LineFeatures(boxes, 1, 100, 100, ["中文。"]);
        Assert.True(cjk[OrderPost.GeomCount] > latin[OrderPost.GeomCount]);
        Assert.Equal(1f, cjk[OrderPost.GeomCount + 4]);
    }

    [Fact]
    public void Xycut_blocks_separate_whitespace_regions()
    {
        float[] boxes = [0, 0, 40, 10, 0, 12, 40, 22, 100, 0, 140, 10];
        List<List<int>> blocks = OrderPost.XyCutBlocks(boxes, 3);
        Assert.Equal(2, blocks.Count);
        Assert.Equal([0, 1], blocks[0]);
        Assert.Equal([2], blocks[1]);
    }
}
