using Microsoft.ML.OnnxRuntime;

namespace Indra.Ocr.Onnx;

/// <summary>
/// Shared ONNX Runtime session construction, matching what <c>OrtOCR.__init__</c> (and through
/// it <c>MathReader.load</c>) sets up in Python.
/// </summary>
public static class OnnxSessions
{
    /// <summary>The execution providers used when the caller does not name any.</summary>
    public static readonly string[] DefaultProviders = ["CPUExecutionProvider"];

    /// <summary>
    /// The one <see cref="SessionOptions"/> every session in a process shares. Python builds a
    /// single instance and hands it to each <c>InferenceSession</c>; the settings are what the
    /// reference was tuned with:
    /// <list type="bullet">
    /// <item><c>intra_op_num_threads = threads</c>, <c>inter_op_num_threads = 1</c>;</item>
    /// <item><c>graph_optimization_level = ORT_ENABLE_ALL</c>;</item>
    /// <item>the CPU memory arena <b>off</b> — with line batches of changing shape it kept
    /// growing (onnxruntime 1.31: <c>rec.onnx</c> +1.4 GB over 10 identical runs; a 40-page
    /// scan peaked at 4.9 GB) and it bought no measurable speed.</item>
    /// </list>
    /// </summary>
    public static SessionOptions CreateOptions(int threads, IReadOnlyList<string>? providers = null)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = false,
        };
        foreach (var provider in providers ?? DefaultProviders)
            AddProvider(options, provider);
        return options;
    }

    /// <summary>Opens a model file. The options instance stays owned by the caller.</summary>
    public static InferenceSession Open(string path, SessionOptions options) => new(path, options);

    private static void AddProvider(SessionOptions options, string name)
    {
        switch (name)
        {
            // CPU is ONNX Runtime's built-in default; appending it is a no-op.
            case "CPUExecutionProvider":
                break;
            case "CUDAExecutionProvider":
                options.AppendExecutionProvider_CUDA(0);
                break;
            case "DmlExecutionProvider":
            case "DirectMLExecutionProvider":
                options.AppendExecutionProvider_DML(0);
                break;
            case "TensorrtExecutionProvider":
                options.AppendExecutionProvider_Tensorrt(0);
                break;
            case "MIGraphXExecutionProvider":
                options.AppendExecutionProvider_MIGraphX(0);
                break;
            case "OpenVINOExecutionProvider":
                options.AppendExecutionProvider_OpenVINO("CPU_FP32");
                break;
            case "CoreMLExecutionProvider":
                options.AppendExecutionProvider_CoreML();
                break;
            default:
                throw new NotSupportedException($"unsupported ONNX Runtime provider '{name}'");
        }
    }
}
