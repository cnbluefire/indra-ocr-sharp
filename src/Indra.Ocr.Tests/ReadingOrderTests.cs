using System.Text.Json;
using Indra.Ocr.Onnx;
using Indra.Ocr.Order;

namespace Indra.Ocr.Tests;

/// <summary>
/// Tests for the reading-order driver (<c>pipeline.OrtOCR.reading_order</c>, see
/// <see cref="ReadingOrder"/>) and its model seam — see <c>docs/115-reading-order.md</c>.
/// </summary>
/// <remarks>
/// Two families. The real cases replay the pages recorded in <c>recognize.json</c> and run the
/// real <c>order.onnx</c> through <see cref="OnnxOrderModel"/>, comparing the order, the layout
/// heads and the pairwise logits with the reference's own output (skipped without the model
/// file). The stand-in cases need no model and pin the driver's *delegation*: which
/// <see cref="OrderPost"/> function runs, what the fallbacks are, and that the model is only
/// asked for a page of two or more lines.
/// </remarks>
public class ReadingOrderTests
{
    private static readonly JsonElement Fx = Fixtures.Load("order_post.json");

    [Fact]
    public void ReadingOrder_matches_reference()
    {
        var directory = TestModels.Directory;
        if (directory is null || !TestModels.IsCurrent ||
            !File.Exists(Path.Combine(directory, "order.onnx")))
            return;

        using var options = OnnxSessions.CreateOptions(1);
        using var session = OnnxSessions.Open(Path.Combine(directory, "order.onnx"), options);
        var model = new OnnxOrderModel(session);

        int cases = 0, withModel = 0, withLayout = 0, withTable = 0;
        foreach (var c in Fx.GetProperty("reading_order").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            int n = c.GetProperty("n").GetInt32();
            var boxes = B64.ZFloats(c.GetProperty("boxes").GetString()!);
            var texts = new List<string>();
            foreach (var t in c.GetProperty("texts").EnumerateArray())
                texts.Add(t.GetString()!);
            double w = c.GetProperty("page")[0].GetDouble();
            double h = c.GetProperty("page")[1].GetDouble();
            bool useModel = c.GetProperty("model").GetBoolean();
            bool tableHeads = c.GetProperty("table_heads").GetBoolean();

            var result = ReadingOrder.Run(useModel ? model : null, boxes, n, texts, w, h,
                                          tableHeads);
            if (useModel)
                withModel++;

            AssertOrder(Fixtures.IntArray(c.GetProperty("order")), result.Order, name);

            var wantCls = NullableFloats(c, "cls");
            var wantSame = NullableFloats(c, "same");
            var wantTab = NullableFloats(c, "tab");
            if (wantCls is null || wantSame is null)
            {
                Assert.Null(result.Layout);
            }
            else
            {
                Assert.NotNull(result.Layout);
                AssertFloats(wantCls, result.Layout!.Cls, name + ".cls");
                AssertFloats(wantSame, result.Layout.Same, name + ".same");
                if (wantTab is null)
                {
                    Assert.Null(result.Layout.Table);
                }
                else
                {
                    AssertFloats(wantTab, result.Layout.Table!, name + ".tab");
                    withTable++;
                }
                withLayout++;
            }

            var wantLogits = NullableFloats(c, "logits");
            if (wantLogits is null)
                Assert.Null(result.Logits);
            else
                AssertFloats(wantLogits, result.Logits!, name + ".logits");
            cases++;
        }

        Assert.True(cases >= 20, $"only {cases} reading_order cases in the fixture");
        Assert.True(withModel >= 14 && withLayout >= 10 && withTable >= 2,
                    $"fixture coverage: {withModel} model, {withLayout} layout, {withTable} table");
    }

    [Fact]
    public void ReadingOrder_drives_the_order_post_functions()
    {
        // A four-line page: two clear columns, so the block path has something to do.
        var boxes = new float[]
        {
            60f, 100f, 280f, 140f,    // 0  left column
            60f, 200f, 280f, 240f,    // 1  left column
            400f, 100f, 620f, 140f,   // 2  right column
            400f, 200f, 620f, 240f,   // 3  right column
        };
        const int n = 4;
        string[] texts = ["first", "second", "third", "fourth"];

        var a = new float[n * n];
        var same = new float[n * n];
        var cls = new float[n * 10];
        var tab = new float[3 * n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                a[i * n + j] = (i - j) * 1.5f;
                same[i * n + j] = i == j ? 0f : (i / 2 == j / 2 ? 3f : -3f);
            }
            cls[i * 10 + (i % 2 == 0 ? 0 : 1)] = 2f;
            for (int c = 0; c < 3; c++)
                tab[c * n * n + i * n + i] = c + 1f;
        }

        // Fewer than two lines, or no model: XY-cut, no heads, no logits — and the model is
        // never asked (the double throws if it is).
        var never = new ThrowingOrderModel();
        foreach (int small in (int[])[0, 1])
        {
            var few = ReadingOrder.Run(never, boxes.AsSpan(0, small * 4), small, texts, 700, 900);
            AssertOrder(OrderPost.OrderXyCut(boxes.AsSpan(0, small * 4), small), few.Order,
                        $"xycut n={small}");
            Assert.Null(few.Layout);
            Assert.Null(few.Logits);
        }
        var withoutModel = ReadingOrder.Run(null, boxes, n, texts, 700, 900);
        AssertOrder(OrderPost.OrderXyCut(boxes, n), withoutModel.Order, "xycut no model");
        Assert.Null(withoutModel.Layout);
        Assert.Null(withoutModel.Logits);

        // Fewer than three outputs: the plain pairwise decode.
        var pairwiseOnly = new FixedOrderModel(a);
        var plain = ReadingOrder.Run(pairwiseOnly, boxes, n, texts, 700, 900);
        AssertOrder(OrderPost.DecodeOrder(a, n), plain.Order, "decode_order");
        Assert.Null(plain.Layout);
        Assert.Same(a, plain.Logits);

        // Three outputs: the layout path, with no table logits unless they were asked for.
        var heads = new FixedOrderModel(a, cls, same);
        var blocks = ReadingOrder.Run(heads, boxes, n, texts, 700, 900);
        AssertOrder(OrderPost.DecodeBlocksOrder(a, same, boxes, n, cls), blocks.Order,
                    "decode_blocks_order");
        Assert.NotNull(blocks.Layout);
        Assert.Same(cls, blocks.Layout!.Cls);
        Assert.Same(same, blocks.Layout.Same);
        Assert.Null(blocks.Layout.Table);
        Assert.Same(a, blocks.Logits);

        // Four outputs: the table heads reach the caller only with table_heads on.
        var table = new FixedOrderModel(a, cls, same, tab);
        Assert.Same(tab, ReadingOrder.Run(table, boxes, n, texts, 700, 900, tableHeads: true)
            .Layout!.Table);
        Assert.Null(ReadingOrder.Run(table, boxes, n, texts, 700, 900).Layout!.Table);

        // The model is asked exactly once per page of two or more lines.
        var counting = new FixedOrderModel(a, cls, same);
        ReadingOrder.Run(counting, boxes.AsSpan(0, 4), 1, texts, 700, 900);
        Assert.Equal(0, counting.Calls);
        ReadingOrder.Run(counting, boxes, n, texts, 700, 900);
        Assert.Equal(1, counting.Calls);
    }

    /// <summary>An <see cref="IOrderModel"/> that returns fixed logits and counts its calls.</summary>
    private sealed class FixedOrderModel : IOrderModel
    {
        private readonly float[] _a;
        private readonly float[]? _cls, _same, _tab;

        public FixedOrderModel(float[] a, float[]? cls = null, float[]? same = null,
                               float[]? tab = null)
        {
            _a = a;
            _cls = cls;
            _same = same;
            _tab = tab;
        }

        public int Calls { get; private set; }

        public OrderOutput Run(ReadOnlySpan<float> feats, int n)
        {
            Calls++;
            return new OrderOutput(_a, _cls, _same, _tab);
        }
    }

    /// <summary>An <see cref="IOrderModel"/> that fails the test if it is ever run.</summary>
    private sealed class ThrowingOrderModel : IOrderModel
    {
        public OrderOutput Run(ReadOnlySpan<float> feats, int n) =>
            throw new InvalidOperationException("the order model must not be called");
    }

    private static float[]? NullableFloats(JsonElement c, string name)
    {
        var e = c.GetProperty(name);
        return e.ValueKind == JsonValueKind.Null ? null : B64.ZFloats(e.GetString()!);
    }

    private static void AssertFloats(float[] want, float[] got, string tag)
    {
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++)
        {
            int w = BitConverter.SingleToInt32Bits(want[i]);
            int g = BitConverter.SingleToInt32Bits(got[i]);
            if (w != g)
                Assert.Fail($"{tag}[{i}]: expected {want[i]:R} (0x{(uint)w:X8}), " +
                            $"got {got[i]:R} (0x{(uint)g:X8})");
        }
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
}
