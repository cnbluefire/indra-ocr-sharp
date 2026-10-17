using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Indra.Ocr.Maths;
using Microsoft.ML.OnnxRuntime;

namespace Indra.Ocr.Onnx;

/// <summary>
/// The math model set: the four graphs and the metadata that goes with them — the loading half of
/// <c>math.MathReader.load</c> / <c>MathReader.__init__</c>.
/// </summary>
/// <remarks>
/// <c>math</c> is optional in the reference: without every file, <c>load</c> returns <c>None</c>
/// and the pipeline skips the step silently. The sessions share the caller's
/// <see cref="SessionOptions"/> — the reference builds one instance for the OCR and the math
/// sessions alike — and the caller keeps ownership of it.
/// </remarks>
public static class MathModels
{
    /// <summary>math.py's <c>FILES</c>: the four graphs.</summary>
    public static readonly string[] Graphs =
        ["mathcls.onnx", "math_enc.onnx", "math_dec_init.onnx", "math_dec_step.onnx"];

    /// <summary>math.py's <c>FILES</c> plus the metadata its <c>load</c> also requires.</summary>
    public static readonly string[] RequiredFiles =
        ["mathcls.onnx", "math_enc.onnx", "math_dec_init.onnx", "math_dec_step.onnx", "math_meta.json"];

    /// <summary>
    /// <c>MathReader.load</c>: the reader when the folder holds every math file, else
    /// <c>null</c>.
    /// </summary>
    public static MathReader? Load(string onnxDirectory, SessionOptions options)
    {
        Guard.NotNullOrEmpty(onnxDirectory);
        Guard.NotNull(options);

        foreach (string file in RequiredFiles)
            if (!File.Exists(Path.Combine(onnxDirectory, file)))
                return null;

        var models = new IMathModel[Graphs.Length];
        try
        {
            for (int i = 0; i < Graphs.Length; i++)
                models[i] = new OnnxMathModel(
                    OnnxSessions.Open(Path.Combine(onnxDirectory, Graphs[i]), options));
            return Create(onnxDirectory, models);
        }
        catch
        {
            foreach (IMathModel? model in models)
                model?.Dispose();
            throw;
        }
    }

    private static MathReader Create(string onnxDirectory, IMathModel[] models)
    {
        // The reference reads the metadata with read_text(), i.e. in the locale encoding, which
        // cannot hold the file's UTF-8 on a Chinese Windows; the port always reads UTF-8.
        using JsonDocument meta = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(onnxDirectory, "math_meta.json")));
        JsonElement root = meta.RootElement;
        JsonElement config = root.GetProperty("config");
        var tokens = new List<string>();
        foreach (JsonElement token in root.GetProperty("tokens").EnumerateArray())
            tokens.Add(token.GetString()!);

        return MathReader.Create(
            cls: models[0],
            enc: models[1],
            init: models[2],
            step: models[3],
            height: config.GetProperty("height").GetInt32(),
            maxTokens: config.GetProperty("max_tokens").GetInt32(),
            layers: config.GetProperty("dec_layers").GetInt32(),
            heads: config.GetProperty("dec_heads").GetInt32(),
            channels: config.GetProperty("channels").GetInt32(),
            tokens: tokens);
    }
}
