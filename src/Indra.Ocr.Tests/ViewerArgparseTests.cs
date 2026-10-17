using Indra.Ocr.ArgumentParsing;
using Indra.Ocr.Viewer;

namespace Indra.Ocr.Tests;

/// <summary>
/// The viewer's command line as the program uses it: the help and the version (captured from the
/// reference's own, in <c>fixtures/viewer_help.txt</c> and <c>viewer_version.txt</c>), the error
/// wording, and the defaults the engine is built from.
/// </summary>
/// <remarks>
/// The byte-exact comparison of both command lines lives in the HTTP harness (step 240); these
/// tests keep the parser checkable without Python.
/// </remarks>
public class ViewerArgparseTests
{
    private static ParsedArguments Parse(params string[] argv) => ViewerParser.Build().ParseArgs(argv);

    private static ArgumentParserExit Fail(params string[] argv)
        => Assert.Throws<ArgumentParserExit>(() => ViewerParser.Build().ParseArgs(argv));

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void Help_matches_the_reference()
    {
        Assert.Equal(Normalize(Fixture("viewer_help.txt")),
                     Normalize(ViewerParser.Build().FormatHelp()));
    }

    [Fact]
    public void Version_matches_the_reference()
    {
        ArgumentParserExit exit = Assert.Throws<ArgumentParserExit>(() => Parse("--version"));
        Assert.Equal(0, exit.Status);
        Assert.Equal("", exit.Stderr);
        Assert.Equal(Normalize(Fixture("viewer_version.txt")), Normalize(exit.Stdout));
    }

    [Fact]
    public void Errors_are_argparse_wording_and_exit_two()
    {
        ArgumentParserExit nope = Fail("--nope");
        Assert.Equal(2, nope.Status);
        Assert.Equal("", nope.Stdout);
        Assert.EndsWith("indra-ocr-viewer: error: unrecognized arguments: --nope\n", nope.Stderr);
        Assert.StartsWith("usage: indra-ocr-viewer [-h] [--model-dir MODEL_DIR]", nope.Stderr);

        Assert.EndsWith("indra-ocr-viewer: error: argument --mode: invalid choice: 'nope' " +
                        "(choose from 'auto', 'ocr', 'text')\n", Fail("--mode", "nope").Stderr);
        Assert.EndsWith("indra-ocr-viewer: error: argument --port: invalid int value: 'x'\n",
                        Fail("--port", "x").Stderr);
        Assert.EndsWith("indra-ocr-viewer: error: argument --dpi: invalid float value: 'x'\n",
                        Fail("--dpi", "x").Stderr);
        Assert.EndsWith("indra-ocr-viewer: error: argument --threads: expected one argument\n",
                        Fail("--threads").Stderr);
    }

    [Fact]
    public void Ambiguous_abbreviation_lists_the_matches_in_option_order()
    {
        ArgumentParserExit exit = Fail("--t", "x");
        Assert.Equal("indra-ocr-viewer: error: ambiguous option: --t could match --threads, " +
                     "--tesseract, --tesseract-lang\n",
                     exit.Stderr[exit.Stderr.IndexOf("indra-ocr-viewer: error:")..]);
    }

    [Fact]
    public void Defaults_are_the_reference_defaults()
    {
        ViewerArgs a = ViewerParser.Read(Parse());
        Assert.Null(a.ModelDir);
        Assert.Equal(8765, a.Port);
        Assert.Equal(4, a.Threads);
        Assert.False(a.Tesseract);
        Assert.Equal("eng", a.TesseractLang);
        Assert.Equal("auto", a.Defaults.Mode);
        Assert.True(a.Defaults.PdfOrder);
        Assert.True(a.Defaults.ImageOcr);
        Assert.False(a.Defaults.Math);
        Assert.False(a.Defaults.Furniture);
        Assert.Equal(200.0, a.Defaults.Dpi);
        Assert.Equal(20, a.Defaults.MaxPages);
    }

    [Fact]
    public void Options_are_read_the_way_viewer_main_reads_them()
    {
        ViewerArgs a = ViewerParser.Read(
            Parse("--port", "9000", "--threads", "1", "--dpi", "72.5", "--max-pages", "3",
                  "--keep-furniture", "--tesseract", "--tesseract-lang", "deu", "--math",
                  "--mode", "text", "--model-order", "--no-image-ocr",
                  "--model-dir", "temp/models"));
        Assert.Equal(9000, a.Port);
        Assert.Equal(1, a.Threads);
        Assert.True(a.Tesseract);
        Assert.Equal("deu", a.TesseractLang);
        Assert.Equal(Path.Combine("temp", "models"), a.ModelDir);
        Assert.Equal("text", a.Defaults.Mode);
        Assert.False(a.Defaults.PdfOrder);
        Assert.False(a.Defaults.ImageOcr);
        Assert.True(a.Defaults.Math);
        Assert.True(a.Defaults.Furniture);
        Assert.Equal(72.5, a.Defaults.Dpi);
        Assert.Equal(3, a.Defaults.MaxPages);
    }
}
