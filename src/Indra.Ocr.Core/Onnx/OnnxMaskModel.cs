using System;
using System.Linq;
using Indra.Ocr.Rec;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Indra.Ocr.Onnx;

/// <summary>
/// <see cref="IMaskModel"/> backed by an ONNX Runtime session — the CenterMask model
/// (<c>mask.onnx</c>, one input <c>x</c> f32 <c>[B, 1, 96, W]</c> and one output of the same
/// shape), mirroring <c>line_mask.load_mask</c> plus its <c>sess.run(None, {"x": batch})</c>.
/// </summary>
/// <remarks>
/// The object borrows the session: it does not dispose it (the model set owns it). The shipped
/// model set has no <c>mask.onnx</c>, so this adapter is exercised only when one is supplied
/// (see <c>docs/085-line-mask.md</c>).
/// </remarks>
public sealed class OnnxMaskModel : IMaskModel
{
    private readonly InferenceSession _session;

    public OnnxMaskModel(InferenceSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public void Run(ReadOnlySpan<float> x, int batch, int width, Span<float> p)
    {
        var tensor = new DenseTensor<float>(x.ToArray(), new[] { batch, 1, LineMask.WindowH, width });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("x", tensor) });
        var values = results[0].AsTensor<float>().ToArray();
        if (values.Length < p.Length)
            throw new InvalidOperationException(
                $"mask model returned {values.Length} values, expected at least {p.Length}");
        values.AsSpan(0, p.Length).CopyTo(p);
    }
}
