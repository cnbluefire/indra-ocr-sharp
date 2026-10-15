using System;
using Indra.Ocr.Rec;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Indra.Ocr.Onnx;

/// <summary>
/// <see cref="IScriptModel"/> backed by an ONNX Runtime session — the hanzi line classifier
/// (<c>script.onnx</c>), the recognizer's two inputs and one output <c>p</c> f32 <c>[B]</c>.
/// </summary>
/// <remarks>The object borrows the session: it does not dispose it (the model set owns it).</remarks>
public sealed class OnnxScriptModel : IScriptModel
{
    private readonly InferenceSession _session;

    public OnnxScriptModel(InferenceSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public float[] Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height,
                       int width)
    {
        var xTensor = new DenseTensor<float>(x.ToArray(), new[] { batch, 1, height, width });
        var widthsTensor = new DenseTensor<long>(widths.ToArray(), new[] { batch });
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("x", xTensor),
            NamedOnnxValue.CreateFromTensor("widths", widthsTensor),
        });

        var p = results[0].AsTensor<float>();
        var values = p.ToArray();
        if (values.Length < batch)
            throw new InvalidOperationException(
                $"script model returned {values.Length} probabilities, expected {batch}");
        return values.Length == batch ? values : values[..batch];
    }
}
