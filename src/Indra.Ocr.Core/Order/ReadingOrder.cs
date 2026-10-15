using System;
using System.Collections.Generic;

namespace Indra.Ocr.Order;

/// <summary>
/// The order model's output for one page: the pairwise order logits (<c>a</c>) and, when the
/// model has layout heads, the per-line class logits (<c>cls</c>) and the same-block logits
/// (<c>same</c>) — plus the row/column/cell logits (<c>tab</c>) when the model carries them.
/// </summary>
/// <remarks>
/// The real <c>order.onnx</c> declares its outputs in exactly this order —
/// <c>a</c> f32 <c>[1, N, N]</c>, <c>cls</c> f32 <c>[1, N, 10]</c>, <c>same</c>
/// <c>[1, N, N]</c>, <c>tab</c> f32 <c>[1, 3, N, N]</c> — and the reference reads them by
/// position (<c>outs[1]</c> is <c>cls</c>, <c>outs[2]</c> is <c>same</c>), so the port does too.
/// A model with fewer than three outputs has no layout heads; <see cref="Table"/> may be set
/// even when <see cref="WasTableHeadsAskedFor"/> is false — the reference only *uses* it when it
/// is asked for.
/// </remarks>
public sealed class OrderOutput
{
    public OrderOutput(float[] pairwise, float[]? cls = null, float[]? same = null,
                       float[]? table = null)
    {
        Pairwise = pairwise ?? throw new ArgumentNullException(nameof(pairwise));
        Cls = cls;
        Same = same;
        Table = table;
    }

    /// <summary>Pairwise order logits, <c>[n, n]</c> (<c>a[i, j] &gt; 0</c>: i before j).</summary>
    public float[] Pairwise { get; }

    /// <summary>Layout class logits, <c>[n, 10]</c>, or <c>null</c> without layout heads.</summary>
    public float[]? Cls { get; }

    /// <summary>Same-block logits, <c>[n, n]</c>, or <c>null</c> without layout heads.</summary>
    public float[]? Same { get; }

    /// <summary>Row/column/cell logits, <c>[3, n, n]</c>, or <c>null</c> without the table heads.</summary>
    public float[]? Table { get; }

    /// <summary>The model's <c>len(outs) &gt;= 3</c>: the same-block and class heads are present.</summary>
    public bool HasLayoutHeads => Cls is not null && Same is not null;
}

/// <summary>
/// The order model (<c>order.onnx</c>): the <c>[n, 16]</c> line features of one page in, the
/// pairwise logits and (when the model has them) the layout heads out.
/// </summary>
/// <remarks>
/// The features are the whole model input set — the reference always passes an all-true
/// <c>mask</c> of the same length, so the port does too (inside the adapter).
/// </remarks>
public interface IOrderModel
{
    /// <summary>Runs the model over the <c>[n, 16]</c> line features of one page.</summary>
    OrderOutput Run(ReadOnlySpan<float> feats, int n);
}

/// <summary>The order model's layout heads as <c>reading_order</c> returns them.</summary>
public sealed class LayoutHeads
{
    public LayoutHeads(float[] cls, float[] same, float[]? table)
    {
        Cls = cls;
        Same = same;
        Table = table;
    }

    /// <summary>Layout class logits, <c>[n, 10]</c> (<c>BLOCK_CLASSES</c> order).</summary>
    public float[] Cls { get; }

    /// <summary>Same-block logits, <c>[n, n]</c>.</summary>
    public float[] Same { get; }

    /// <summary>Row/column/cell logits, <c>[3, n, n]</c>, only when the table heads were asked for.</summary>
    public float[]? Table { get; }
}

/// <summary>
/// What <c>pipeline.OrtOCR.reading_order</c> returns: the reading order of the lines, the layout
/// heads (when the model has any) and the pairwise logits (when the model ran).
/// </summary>
public sealed class ReadingOrderResult
{
    public ReadingOrderResult(int[] order, LayoutHeads? layout, float[]? logits)
    {
        Order = order;
        Layout = layout;
        Logits = logits;
    }

    /// <summary>Indices into the line list, in reading order.</summary>
    public int[] Order { get; }

    /// <summary>The layout heads, or <c>null</c> when the model has none (or did not run).</summary>
    public LayoutHeads? Layout { get; }

    /// <summary>The pairwise order logits <c>a</c> f32 <c>[n, n]</c>, or <c>null</c>.</summary>
    public float[]? Logits { get; }
}

/// <summary>
/// The reading-order driver, ported from <c>pipeline.OrtOCR.reading_order</c>: without an order
/// model (or for a page of fewer than two lines) it falls back to XY-cut; otherwise it runs the
/// model and decodes either the plain pairwise path or, when the model has layout heads, the
/// block path (<see cref="OrderPost.DecodeBlocksOrder"/>).
/// </summary>
public static class ReadingOrder
{
    /// <summary>
    /// Reading order for <paramref name="boxes"/> (<c>[n, 4]</c>) with the recognized
    /// <paramref name="texts"/> and the page size.
    /// </summary>
    /// <param name="model">The order model, or <c>null</c> when the model set has none.</param>
    /// <param name="tableHeads">
    /// The reference's <c>table_heads</c> flag: only then is the model's fourth output
    /// (<c>tab</c>) handed on in <see cref="LayoutHeads.Table"/>.
    /// </param>
    public static ReadingOrderResult Run(
        IOrderModel? model, ReadOnlySpan<float> boxes, int n, IReadOnlyList<string>? texts,
        double pageWidth, double pageHeight, bool tableHeads = false)
    {
        if (n < 2 || model is null)
            return new ReadingOrderResult(OrderPost.OrderXyCut(boxes, n), null, null);

        var feats = OrderPost.LineFeatures(boxes, n, pageWidth, pageHeight, texts);
        var outs = model.Run(feats, n);
        if (!outs.HasLayoutHeads)
            return new ReadingOrderResult(OrderPost.DecodeOrder(outs.Pairwise, n), null,
                                          outs.Pairwise);

        var layout = new LayoutHeads(outs.Cls!, outs.Same!,
                                     tableHeads ? outs.Table : null);
        var order = OrderPost.DecodeBlocksOrder(outs.Pairwise, outs.Same!, boxes, n, outs.Cls!);
        return new ReadingOrderResult(order, layout, outs.Pairwise);
    }
}
