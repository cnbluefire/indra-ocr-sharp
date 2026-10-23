using System.Text.Json;
using Indra.Ocr.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit.Abstractions;

namespace Indra.Ocr.Tests;

/// <summary>
/// Checks the ORT session infrastructure against the Python reference: the same sessions the
/// model folder implies, the same tensor signatures, and the same metadata.
/// </summary>
public class OnnxModelTests
{
    private readonly ITestOutputHelper _output;

    public OnnxModelTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Model_set_matches_the_reference()
    {
        if (!Available())
            return;
        using var models = OcrModels.Load(TestModels.Directory!, threads: 1);
        var root = Fixtures.Load("onnx_models.json");
        var present = root.GetProperty("present");

        Assert.NotNull(models.Det);
        Assert.NotNull(models.Rec);
        Assert.Equal(present.GetProperty("mask").GetBoolean(), models.Mask is not null);
        Assert.Equal(present.GetProperty("order").GetBoolean(), models.Order is not null);
        Assert.Equal(present.GetProperty("orient").GetBoolean(), models.Orient is not null);
        Assert.Equal(present.GetProperty("script").GetBoolean(), models.Script is not null);
        Assert.Equal(present.GetProperty("rec_cjk").GetBoolean(), models.RecCjk is not null);
        Assert.Equal(models.RecCjk is not null, models.VocabCjk is not null);
        Assert.Equal(models.RecCjk is not null, models.HasCjk);

        var meta = root.GetProperty("meta");
        Assert.Equal(meta.GetProperty("det_long_side").GetInt32(), models.DetLongSide);
        Assert.Equal(meta.GetProperty("rec_height").GetInt32(), models.RecHeight);
        Assert.Equal(meta.TryGetProperty("rec_window", out JsonElement window)
                         ? window.GetInt32()
                         : 1,
                     models.RecWindow);

        // The vocabulary sizes the shipped meta.json implies (see docs/050-vocab.md).
        Assert.Equal(1677, models.Vocab.Characters.Length);
        Assert.Equal(1678, models.Vocab.ClassCount);
        Assert.Equal(9967, models.VocabCjk!.Characters.Length);
        Assert.Equal(9968, models.VocabCjk.ClassCount);
    }

    [Fact]
    public void Tensor_signatures_match_the_reference()
    {
        if (!Available())
            return;
        using var models = OcrModels.Load(TestModels.Directory!, threads: 1);
        var root = Fixtures.Load("onnx_models.json").GetProperty("models");

        foreach (var (name, session) in Sessions(models))
        {
            if (session is null)
                continue;
            var expected = root.GetProperty(name);
            AssertSignature(expected.GetProperty("inputs"), session.InputMetadata, $"{name} inputs");
            AssertSignature(expected.GetProperty("outputs"), session.OutputMetadata, $"{name} outputs");
        }
    }

    [Fact]
    public void Every_session_runs()
    {
        if (!Available())
            return;
        using var models = OcrModels.Load(TestModels.Directory!, threads: 1);
        var root = Fixtures.Load("onnx_models.json").GetProperty("models");

        var det = Run(models.Det, [Input("x", new DenseTensor<float>(new[] { 1, 1, 64, 64 }))]);
        AssertOutputs(root, "det", det);
        Assert.Equal(new[] { 1, 3 }, det[0].Dims[..2]);

        var rec = Run(models.Rec, RecInputs(64));
        AssertOutputs(root, "rec", rec);

        if (models.Order is not null)
            AssertOutputs(root, "order", Run(models.Order, OrderInputs(4)));

        if (models.Orient is not null)
            AssertOutputs(root, "orient", Run(models.Orient, [Input("x", new DenseTensor<float>(new[] { 1, 1, 768, 768 }))]));

        if (models.Script is not null)
            AssertOutputs(root, "script", Run(models.Script, RecInputs(64)));

        if (models.RecCjk is not null)
            AssertOutputs(root, "rec_cjk", Run(models.RecCjk, RecInputs(64)));
    }

    private static void AssertOutputs(JsonElement models, string name, (string Name, int[] Dims)[] actual)
    {
        string[] expected = models.GetProperty(name).GetProperty("outputs")
            .EnumerateArray().Select(o => o.GetProperty("name").GetString()!).ToArray();
        // The order matters: Python indexes run() results positionally.
        Assert.Equal(expected, actual.Select(o => o.Name).ToArray());
    }

    private static NamedOnnxValue[] RecInputs(int width)
    {
        var widths = new DenseTensor<long>(new[] { 1 });
        widths[0] = width;
        return
        [
            Input("x", new DenseTensor<float>(new[] { 1, 1, 32, width })),
            Input("widths", widths),
        ];
    }

    private static NamedOnnxValue[] OrderInputs(int n)
    {
        var mask = new DenseTensor<bool>(new[] { 1, n });
        for (int i = 0; i < n; i++)
            mask[0, i] = true;
        return
        [
            Input("feats", new DenseTensor<float>(new[] { 1, n, 16 })),
            Input("mask", mask),
        ];
    }

    private static NamedOnnxValue Input<T>(string name, DenseTensor<T> tensor) =>
        NamedOnnxValue.CreateFromTensor(name, tensor);

    /// <summary>Runs a session and reports each output's name and dimensions.</summary>
    private static (string Name, int[] Dims)[] Run(InferenceSession session, NamedOnnxValue[] inputs)
    {
        using var results = session.Run(inputs);
        return results.Select(r => (r.Name, DimsOf(r.Value))).ToArray();
    }

    private static int[] DimsOf(object value) => value switch
    {
        Tensor<float> t => t.Dimensions.ToArray(),
        Tensor<long> t => t.Dimensions.ToArray(),
        Tensor<bool> t => t.Dimensions.ToArray(),
        _ => throw new NotSupportedException($"unexpected tensor {value?.GetType()}"),
    };

    private static IEnumerable<(string Name, InferenceSession? Session)> Sessions(OcrModels models)
    {
        yield return ("det", models.Det);
        yield return ("rec", models.Rec);
        yield return ("mask", models.Mask);
        yield return ("order", models.Order);
        yield return ("orient", models.Orient);
        yield return ("script", models.Script);
        yield return ("rec_cjk", models.RecCjk);
    }

    private static void AssertSignature(
        JsonElement expected, IReadOnlyDictionary<string, NodeMetadata> actual, string what)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Count);
        foreach (var e in expected.EnumerateArray())
        {
            string name = e.GetProperty("name").GetString()!;
            Assert.True(actual.ContainsKey(name), $"{what}: missing '{name}'");
            var meta = actual[name];
            Assert.Equal(e.GetProperty("type").GetString(), OnnxType(meta.ElementType));
            Assert.Equal(
                e.GetProperty("shape").EnumerateArray().Select(d => d.GetInt32()).ToArray(),
                meta.Dimensions.ToArray());
        }
    }

    private static string OnnxType(Type type) => type switch
    {
        _ when type == typeof(float) => "tensor(float)",
        _ when type == typeof(long) => "tensor(int64)",
        _ when type == typeof(bool) => "tensor(bool)",
        _ => throw new NotSupportedException($"unexpected element type {type}"),
    };

    private bool Available()
    {
        if (TestModels.Directory is not null)
            return true;
        _output.WriteLine("skipped: resources/models is not present");
        return false;
    }
}
