using Indra.Ocr.ArgumentParsing;
using Indra.Ocr.Onnx;

namespace Indra.Ocr.Tests;

/// <summary>
/// The lines a model download reports make: what a host prints on stderr while the models arrive
/// (step 310). The newlines are Python's; <c>WriteError</c> is what turns them into the platform's.
/// </summary>
public class ConsoleOutputTests
{
    [Fact]
    public void The_first_report_names_the_file_from_and_to()
    {
        var report = new ModelDownloadProgress(
            "det.onnx", @"C:\models\det.onnx",
            "https://huggingface.co/datalab-to/indra-ocr/resolve/v3.1.1/det.onnx", 0, 17_179_869);

        Assert.Equal("downloading det.onnx (17.2 MB)\n" +
                     "  from https://huggingface.co/datalab-to/indra-ocr/resolve/v3.1.1/det.onnx\n" +
                     @"  to C:\models\det.onnx" + "\n",
                     ConsoleOutput.DownloadProgress(report));
    }

    [Fact]
    public void A_later_report_shows_how_far_it_is()
    {
        var report = new ModelDownloadProgress("det.onnx", @"C:\models\det.onnx", "https://hub/x",
                                               1_717_986, 17_179_869);

        Assert.Equal("det.onnx: 1.7 MB / 17.2 MB (9%)\n", ConsoleOutput.DownloadProgress(report));
    }

    [Fact]
    public void The_last_report_is_a_hundred_percent()
    {
        var report = new ModelDownloadProgress("det.onnx", @"C:\models\det.onnx", "https://hub/x",
                                               17_179_869, 17_179_869);

        Assert.Equal("det.onnx: 17.2 MB / 17.2 MB (100%)\n", ConsoleOutput.DownloadProgress(report));
    }

    /// <summary>A small file is read in the unit that fits it, not in megabytes.</summary>
    [Fact]
    public void A_small_file_reads_in_kilobytes()
    {
        var report = new ModelDownloadProgress("meta.json", @"C:\models\meta.json", "https://hub/x",
                                               1_200, 1_200);

        Assert.Equal("meta.json: 1.2 KB / 1.2 KB (100%)\n", ConsoleOutput.DownloadProgress(report));
    }

    /// <summary>Without a size there is no percentage to print.</summary>
    [Fact]
    public void A_report_without_a_size_has_no_percentage()
    {
        var start = new ModelDownloadProgress("det.onnx", @"C:\models\det.onnx", "https://hub/x", 0, 0);
        Assert.Equal("downloading det.onnx\n" + @"  from https://hub/x" + "\n" +
                     @"  to C:\models\det.onnx" + "\n",
                     ConsoleOutput.DownloadProgress(start));

        var later = new ModelDownloadProgress("det.onnx", @"C:\models\det.onnx", "https://hub/x",
                                              2_000_000, 0);
        Assert.Equal("det.onnx: 2.0 MB\n", ConsoleOutput.DownloadProgress(later));
    }

    /// <summary>
    /// <c>HF_HUB_DISABLE_PROGRESS_BARS</c> asks for quiet, with the truthiness the reference reads:
    /// <c>1</c>, <c>on</c>, <c>yes</c>, <c>true</c>, in any case.
    /// </summary>
    [Fact]
    public void The_references_switch_turns_the_lines_off()
    {
        string? saved = Environment.GetEnvironmentVariable("HF_HUB_DISABLE_PROGRESS_BARS");
        try
        {
            foreach (string value in new[] { "1", "on", "YES", "True" })
            {
                Environment.SetEnvironmentVariable("HF_HUB_DISABLE_PROGRESS_BARS", value);
                Assert.Null(ConsoleOutput.DownloadProgressCallback());
            }

            foreach (string? value in new[] { "0", "off", "", null })
            {
                Environment.SetEnvironmentVariable("HF_HUB_DISABLE_PROGRESS_BARS", value);
                Assert.NotNull(ConsoleOutput.DownloadProgressCallback());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HF_HUB_DISABLE_PROGRESS_BARS", saved);
        }
    }
}
