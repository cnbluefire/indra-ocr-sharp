using System;
using Indra.Ocr.Rec;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Indra.Ocr.Onnx;

/// <summary>
/// <see cref="IRecModel"/> backed by an ONNX Runtime session — the CTC line recognizer
/// (<c>rec.onnx</c>, <c>rec.int8.onnx</c> or <c>rec_cjk.onnx</c>), two inputs <c>x</c> f32
/// <c>[B, 1, 32, W]</c> and <c>widths</c> int64 <c>[B]</c>, three outputs <c>ids</c> int64
/// <c>[B, T]</c>, <c>conf</c> f32 <c>[B, T]</c> and <c>lengths</c> int64 <c>[B]</c>.
/// </summary>
/// <remarks>The object borrows the session: it does not dispose it (the model set owns it).</remarks>
public sealed class OnnxRecModel : IRecModel
{
    private readonly InferenceSession _session;

    public OnnxRecModel(InferenceSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public RecOutput Run(ReadOnlySpan<float> x, ReadOnlySpan<long> widths, int batch, int height,
                         int width)
    {
        var xTensor = new DenseTensor<float>(x.ToArray(), new[] { batch, 1, height, width });
        var widthsTensor = new DenseTensor<long>(widths.ToArray(), new[] { batch });
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("x", xTensor),
            NamedOnnxValue.CreateFromTensor("widths", widthsTensor),
        });

        int outputs = 0;
        foreach (var _ in results) outputs++;
        if (outputs < 3)
            throw new InvalidOperationException($"rec model returned {outputs} outputs, expected 3");

        var ids = results[0].AsTensor<long>();
        var conf = results[1].AsTensor<float>();
        var lengths = results[2].AsTensor<long>();
        if (ids.Dimensions.Length != 2 || ids.Dimensions[0] != batch ||
            conf.Dimensions.Length != 2 || conf.Dimensions[0] != batch ||
            conf.Dimensions[1] != ids.Dimensions[1] ||
            lengths.Dimensions.Length != 1 || lengths.Dimensions[0] != batch)
            throw new InvalidOperationException(
                $"rec model returned ids [{string.Join(",", ids.Dimensions.ToArray())}], " +
                $"conf [{string.Join(",", conf.Dimensions.ToArray())}] and " +
                $"lengths [{string.Join(",", lengths.Dimensions.ToArray())}] for a batch of {batch}");

        float[]? blank = null, topv = null; long[]? topi = null; int topClasses = 0;
        if (outputs >= 6)
        {
            blank = results[3].AsTensor<float>().ToArray();
            var tv = results[4].AsTensor<float>(); topv = tv.ToArray();
            topi = results[5].AsTensor<long>().ToArray();
            topClasses = tv.Dimensions[^1];
        }
        return new RecOutput(ids.ToArray(), conf.ToArray(), lengths.ToArray(), ids.Dimensions[1],
                             blank, topv, topi, topClasses);
    }
}
