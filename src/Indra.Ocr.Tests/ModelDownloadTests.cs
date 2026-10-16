using Indra.Ocr.Onnx;

namespace Indra.Ocr.Tests;

/// <summary>
/// Step 170: the model downloader. The pattern test is pure; the download test talks to the
/// Hub, so it runs only when <c>INDRA_OCR_TEST_DOWNLOAD=1</c> is set (a network test does not
/// belong in a suite that must stay offline).
/// </summary>
public class ModelDownloadTests
{
    [Fact]
    public void Wanted_keeps_the_references_patterns()
    {
        Assert.True(ModelDownload.Wanted("det.onnx"));
        Assert.True(ModelDownload.Wanted("rec_cjk.onnx"));
        Assert.True(ModelDownload.Wanted("assets/logo.onnx")); // '*' crosses '/', as fnmatch
        Assert.True(ModelDownload.Wanted("meta.json"));
        Assert.True(ModelDownload.Wanted("config.json"));
        Assert.False(ModelDownload.Wanted("math_meta.json"));
        Assert.False(ModelDownload.Wanted("mathcls.onnx"));
        Assert.True(ModelDownload.Wanted("mathcls.onnx", math: true));
        Assert.True(ModelDownload.Wanted("math_meta.json", math: true));
        Assert.False(ModelDownload.Wanted("README.md"));
        Assert.False(ModelDownload.Wanted("model.safetensors"));
        Assert.False(ModelDownload.Wanted("assets/logo.png"));
    }

    [Fact]
    public void Matches_uses_fnmatch_semantics()
    {
        Assert.True(ModelDownload.Matches("abc", "a?c"));
        Assert.False(ModelDownload.Matches("abc", "a?d"));
        Assert.True(ModelDownload.Matches("", "*"));
        Assert.True(ModelDownload.Matches("x.onnx", "*"));
        Assert.False(ModelDownload.Matches("x.onnx", "*.json"));
    }

    [Fact]
    public void The_model_sets_are_the_references()
    {
        Assert.Equal(["det.onnx", "rec.onnx", "order.onnx", "meta.json"], ModelDownload.Files);
        Assert.Equal("datalab-to/indra-ocr", ModelDownload.HfRepo);
        Assert.Equal("v3.1.1", ModelDownload.HfRevision);
        Assert.Equal(5, ModelDownload.MathFiles.Length);
    }

    [Fact]
    public void Has_models_checks_the_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "indra-ocr-has-models-test");
        Directory.CreateDirectory(dir);
        foreach (string f in ModelDownload.Files)
            File.WriteAllText(Path.Combine(dir, f), "");

        Assert.True(ModelDownload.HasModels(dir));
        Assert.False(ModelDownload.HasModels(dir, math: true));

        File.Delete(Path.Combine(dir, "order.onnx"));
        Assert.False(ModelDownload.HasModels(dir));
    }

    /// <summary>
    /// Fills a folder from the Hub and checks it against the reference's file list. Opt-in:
    /// <c>INDRA_OCR_TEST_DOWNLOAD=1</c>.
    /// </summary>
    [Fact]
    public void Download_fills_a_folder_with_the_model_set()
    {
        if (Environment.GetEnvironmentVariable("INDRA_OCR_TEST_DOWNLOAD") != "1")
            return;

        string dir = Path.Combine(Path.GetTempPath(), "indra-ocr-model-download");
        ModelDownload.Download(dir);

        Assert.True(ModelDownload.HasModels(dir), "the model set is incomplete");
        foreach (string f in ModelDownload.Files)
            Assert.True(new FileInfo(Path.Combine(dir, f)).Length > 0, f);
        foreach (string f in ModelDownload.MathFiles)
            Assert.False(File.Exists(Path.Combine(dir, f)), $"{f} was downloaded without math");

        // A second call finds every file at the right size and downloads nothing.
        var sizes = ModelDownload.Files.ToDictionary(f => f, f => new FileInfo(Path.Combine(dir, f)).Length);
        ModelDownload.Download(dir);
        foreach (string f in ModelDownload.Files)
            Assert.True(new FileInfo(Path.Combine(dir, f)).Length == sizes[f], f);
    }
}
