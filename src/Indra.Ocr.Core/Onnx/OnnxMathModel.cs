using System;
using System.Collections.Generic;
using Indra.Ocr.Maths;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Indra.Ocr.Onnx;

/// <summary>
/// <see cref="IMathModel"/> backed by an ONNX Runtime session — one of the four math graphs. The
/// feed is filtered by the graph's own input names, as the reference does (the exports drop
/// unused inputs: the decoder's init graph does not read the mask).
/// </summary>
/// <remarks>
/// The object owns the session and disposes it. That is the opposite of
/// <see cref="OnnxRecModel"/>, which borrows its session: the OCR sessions have
/// <see cref="OcrModels"/> for an owner, the math ones have only the reader.
/// </remarks>
public sealed class OnnxMathModel : IMathModel
{
    private readonly InferenceSession _session;
    private readonly string[] _inputNames;

    public OnnxMathModel(InferenceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _inputNames = new string[session.InputMetadata.Count];
        int i = 0;
        foreach (string name in session.InputMetadata.Keys)
            _inputNames[i++] = name;
    }

    public IReadOnlyList<string> InputNames => _inputNames;

    public IReadOnlyList<MathTensor> Run(IReadOnlyDictionary<string, MathTensor> feed)
    {
        var inputs = new List<NamedOnnxValue>(feed.Count);
        foreach (KeyValuePair<string, MathTensor> kv in feed)
            inputs.Add(ToOnnx(kv.Key, kv.Value));

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
        var outputs = new List<MathTensor>();
        foreach (DisposableNamedOnnxValue result in results)
            outputs.Add(FromOnnx(result));
        return outputs;
    }

    public void Dispose() => _session.Dispose();

    private static NamedOnnxValue ToOnnx(string name, MathTensor tensor)
    {
        if (tensor.IsFloat32)
            return NamedOnnxValue.CreateFromTensor(name,
                                                   new DenseTensor<float>(tensor.Floats, tensor.Dimensions));
        return NamedOnnxValue.CreateFromTensor(name,
                                               new DenseTensor<long>(tensor.Longs, tensor.Dimensions));
    }

    private static MathTensor FromOnnx(DisposableNamedOnnxValue result)
    {
        if (result.ElementType != TensorElementType.Float)
            throw new InvalidOperationException(
                $"math model output {result.Name} is {result.ElementType}, expected float");
        Tensor<float> tensor = result.AsTensor<float>();
        return MathTensor.Float32(tensor.ToArray(), tensor.Dimensions.ToArray());
    }
}
