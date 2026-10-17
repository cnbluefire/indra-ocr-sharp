using System;
using Indra.Ocr.Order;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Indra.Ocr.Onnx;

/// <summary>
/// <see cref="IOrderModel"/> backed by an ONNX Runtime session — the reading-order model
/// (<c>order.onnx</c>): <c>feats</c> f32 <c>[1, n, 16]</c> and an all-true <c>mask</c> bool
/// <c>[1, n]</c> in; <c>a</c> f32 <c>[1, n, n]</c>, <c>cls</c> f32 <c>[1, n, 10]</c>,
/// <c>same</c> f32 <c>[1, n, n]</c> and <c>tab</c> f32 <c>[1, 3, n, n]</c> out.
/// </summary>
/// <remarks>
/// The outputs are read by position, as the reference does (its <c>outs[1]</c> is <c>cls</c>);
/// a session whose model has fewer outputs simply reports fewer heads. The object borrows the
/// session: it does not dispose it (the model set owns it).
/// </remarks>
public sealed class OnnxOrderModel : IOrderModel
{
    private readonly InferenceSession _session;

    public OnnxOrderModel(InferenceSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public OrderOutput Run(ReadOnlySpan<float> feats, int n)
    {
        var featsTensor = new DenseTensor<float>(feats.ToArray(),
                                                 new[] { 1, n, OrderPost.FeatureCount });
        var mask = new bool[n];
        mask.AsSpan().Fill(true);
        var maskTensor = new DenseTensor<bool>(mask, new[] { 1, n });
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("feats", featsTensor),
            NamedOnnxValue.CreateFromTensor("mask", maskTensor),
        });

        // a, cls, same, tab — the real model's declared order.
        var a = results[0].AsTensor<float>().ToArray();
        if (a.Length != n * n)
            throw new InvalidOperationException(
                $"order model returned {a.Length} pairwise logits, expected {n * n}");
        float[]? cls = results.Count > 1 ? results[1].AsTensor<float>().ToArray() : null;
        float[]? same = results.Count > 2 ? results[2].AsTensor<float>().ToArray() : null;
        float[]? tab = results.Count > 3 ? results[3].AsTensor<float>().ToArray() : null;
        return new OrderOutput(a, cls, same, tab);
    }
}
