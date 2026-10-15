using System;
using Indra.Ocr.Pipeline;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Indra.Ocr.Onnx;

/// <summary>
/// <see cref="IOrientModel"/> backed by an ONNX Runtime session — <c>orient.onnx</c>, one input
/// <c>x</c> f32 <c>[B, 1, side, side]</c> and one output <c>p</c> f32 <c>[B, 4]</c>, mirroring
/// <c>pipeline.OrtOCR.orientation</c>'s <c>self.orient.run(None, {"x": ...})</c>.
/// </summary>
/// <remarks>
/// The object borrows the session: it does not dispose it (the model set owns it).
/// </remarks>
public sealed class OnnxOrientModel : IOrientModel
{
    private readonly InferenceSession _session;

    public OnnxOrientModel(InferenceSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public void Run(ReadOnlySpan<float> x, Span<float> p)
    {
        int side = (int)Math.Sqrt(x.Length);
        if (side * side != x.Length)
            throw new ArgumentException($"orient input of {x.Length} values is not a square", nameof(x));
        var tensor = new DenseTensor<float>(x.ToArray(), new[] { 1, 1, side, side });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("x", tensor) });
        var values = results[0].AsTensor<float>().ToArray();
        if (values.Length < p.Length)
            throw new InvalidOperationException(
                $"orient model returned {values.Length} values, expected at least {p.Length}");
        values.AsSpan(0, p.Length).CopyTo(p);
    }
}
