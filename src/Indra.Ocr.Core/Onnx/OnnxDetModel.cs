using System;
using System.Linq;
using Indra.Ocr.Det;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Indra.Ocr.Onnx;

/// <summary>
/// <see cref="IDetModel"/> backed by an ONNX Runtime session — LineNet (<c>det.onnx</c>), one
/// input <c>x</c> f32 <c>[B, 1, H, W]</c> and one output f32 <c>[B, 3, H/2, W/2]</c>, mirroring
/// <c>pipeline.OrtOCR._detect_at</c>'s <c>self.det.run(None, {"x": ...})</c>.
/// </summary>
/// <remarks>The object borrows the session: it does not dispose it (the model set owns it).</remarks>
public sealed class OnnxDetModel : IPictureDetModel
{
    private readonly InferenceSession _session;
    private float[]? _pictures;
    private int _pictureHeight, _pictureWidth;

    public OnnxDetModel(InferenceSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public void Run(ReadOnlySpan<float> x, int height, int width, Span<float> logits)
    {
        var tensor = new DenseTensor<float>(x.ToArray(), new[] { 1, 1, height, width });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("x", tensor) });
        var output = results[0].AsTensor<float>();

        int oh = height / DetPost.OutStride, ow = width / DetPost.OutStride;
        var dims = output.Dimensions;
        if (dims.Length != 4 || dims[1] < 3 || dims[2] != oh || dims[3] != ow)
            throw new InvalidOperationException(
                $"det model returned [{string.Join(",", dims.ToArray())}], expected [1,3,{oh},{ow}]");

        var values = output.ToArray();
        if (values.Length < logits.Length)
            throw new InvalidOperationException(
                $"det model returned {values.Length} values, expected {logits.Length}");
        values.AsSpan(0, logits.Length).CopyTo(logits);
        if (dims[1] > 3)
        {
            _pictures = values.AsSpan(3 * oh * ow, oh * ow).ToArray();
            _pictureHeight = oh; _pictureWidth = ow;
        }
        else _pictures = null;
    }

    public float[]? TakePictureLogits(out int height, out int width)
    {
        height = _pictureHeight; width = _pictureWidth;
        float[]? value = _pictures; _pictures = null; return value;
    }
}
