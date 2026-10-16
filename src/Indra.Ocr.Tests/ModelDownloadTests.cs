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
    /// The copy loop reports at every tenth of a file and again at the end — the cadence the hosts
    /// turn into lines (step 310). The bytes themselves are still copied in full.
    /// </summary>
    [Fact]
    public void Copy_reports_at_every_tenth_and_at_the_end()
    {
        byte[] body = new byte[1_000_000];
        Random.Shared.NextBytes(body);
        using var src = new MemoryStream(body);
        using var dst = new MemoryStream();
        var reports = new List<long>();

        ModelDownload.Copy(src, dst, body.Length, reports.Add);

        Assert.Equal(body, dst.ToArray());
        Assert.True(reports.Count > 1, "a file this size is reported more than once");
        int previous = 0;
        foreach (long done in reports.Take(reports.Count - 1))
        {
            int percent = (int)(done * 100 / body.Length);
            Assert.True(percent - previous >= 10, $"{percent}% follows {previous}%");
            previous = percent;
        }

        Assert.Equal(body.Length, reports[^1]); // the end is always reported
    }

    /// <summary>A file smaller than one read is reported once, at the end.</summary>
    [Fact]
    public void Copy_reports_a_short_file_once()
    {
        using var src = new MemoryStream(new byte[1_000]);
        using var dst = new MemoryStream();
        var reports = new List<long>();

        ModelDownload.Copy(src, dst, size: 1_000, reports.Add);

        Assert.Equal([1_000L], reports);
    }

    /// <summary>A size the Hub did not give (0) is reported at the end alone.</summary>
    [Fact]
    public void Copy_reports_an_unknown_size_at_the_end()
    {
        using var src = new MemoryStream(new byte[200_000]);
        using var dst = new MemoryStream();
        var reports = new List<long>();

        ModelDownload.Copy(src, dst, size: 0, reports.Add);

        Assert.Equal([200_000L], reports);
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
