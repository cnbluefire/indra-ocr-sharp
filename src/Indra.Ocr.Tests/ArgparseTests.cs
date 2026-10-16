using Indra.Ocr.Cli;
using Indra.Ocr.Argparse;
using Indra.Ocr.Py;

namespace Indra.Ocr.Tests;

/// <summary>
/// The argparse port as the command line uses it: the help text (checked against the
/// reference's own, captured in <c>fixtures/cli_help.txt</c>), the errors and the
/// namespace the options produce.
/// </summary>
/// <remarks>
/// The byte-exact comparison of every output �?including the CRLF the reference's
/// text-mode streams carry �?lives in <c>tools/scripts/test/cli_parity.py</c>, which runs
/// both command lines. These tests keep the parser's behaviour checkable without Python.
/// </remarks>
public class ArgparseTests
{
    private static Namespace Parse(params string[] argv) => CliParser.Build().ParseArgs(argv);

    private static ArgparseExit Fail(params string[] argv)
        => Assert.Throws<ArgparseExit>(() => CliParser.Build().ParseArgs(argv));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void Help_matches_the_reference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "cli_help.txt");
        string expected = Normalize(File.ReadAllText(path));
        Assert.Equal(expected, Normalize(CliParser.Build().FormatHelp()));
    }

    [Fact]
    public void Help_short_flag_prints_the_same_help()
    {
        ArgparseExit exit = Assert.Throws<ArgparseExit>(() => CliParser.Build().ParseArgs(["-h"]));
        Assert.Equal(0, exit.Status);
        Assert.Equal("", exit.Stderr);
        Assert.Equal(Normalize(CliParser.Build().FormatHelp()), Normalize(exit.Stdout));
    }

    [Fact]
    public void Version_prints_the_product_line()
    {
        ArgparseExit exit = Assert.Throws<ArgparseExit>(() => CliParser.Build().ParseArgs(["--version"]));
        Assert.Equal(0, exit.Status);
        Assert.Equal("", exit.Stderr);
        Assert.Equal("indra-ocr 0.3.2\n", exit.Stdout);
    }

    [Fact]
    public void Errors_are_argparse_wording_and_exit_two()
    {
        ArgparseExit noInputs = Fail();
        Assert.Equal(2, noInputs.Status);
        Assert.Equal("", noInputs.Stdout);
        Assert.EndsWith("indra-ocr: error: the following arguments are required: inputs\n",
                        noInputs.Stderr);
        Assert.StartsWith("usage: indra-ocr [-h] [--version] [--out OUT]", noInputs.Stderr);

        Assert.EndsWith("indra-ocr: error: unrecognized arguments: --nope\n",
                        Fail("--nope", "a.pdf").Stderr);
        Assert.EndsWith("indra-ocr: error: argument --format: invalid choice: 'xml' " +
                        "(choose from 'md', 'text', 'json')\n", Fail("--format", "xml", "a.pdf").Stderr);
        Assert.EndsWith("indra-ocr: error: argument --dpi: invalid float value: 'x'\n",
                        Fail("--dpi", "x", "a.pdf").Stderr);
        Assert.EndsWith("indra-ocr: error: argument --dpi: expected one argument\n",
                        Fail("--dpi").Stderr);
        Assert.EndsWith("indra-ocr: error: argument --max-pages: invalid int value: 'x'\n",
                        Fail("--max-pages", "x", "a.pdf").Stderr);
        Assert.EndsWith("indra-ocr: error: argument --mode: invalid choice: 'nope' " +
                        "(choose from 'auto', 'ocr', 'text')\n", Fail("--mode", "nope", "a.pdf").Stderr);
    }

    [Fact]
    public void Ambiguous_abbreviation_lists_the_matches_in_option_order()
    {
        ArgparseExit exit = Fail("--m", "a.pdf");
        Assert.Equal(
            "indra-ocr: error: ambiguous option: --m could match --mode, --model-order, " +
            "--model-dir, --max-pages, --math\n", exit.Stderr[(exit.Stderr.IndexOf("indra-ocr: error:")..)]);
    }

    [Fact]
    public void Defaults_are_the_reference_defaults()
    {
        Namespace a = Parse("a.pdf");
        Assert.Equal(["a.pdf"], a.PathList("inputs").Select(p => p.ToString()));
        Assert.Null(a.PathOrNull("out"));
        Assert.Null(a.PathOrNull("model_dir"));
        Assert.Equal("md", a.Str("format"));
        Assert.Equal("auto", a.Str("mode"));
        Assert.Null(a.Str("password"));
        Assert.Equal(0, a.Int("cores"));
        Assert.Equal(200.0, a.Float("dpi"));
        Assert.Equal(0, a.Int("max_pages"));
        Assert.False(a.Flag("math"));
        Assert.False(a.Flag("keep_furniture"));
        Assert.False(a.Flag("timings"));
        Assert.False(a.Flag("model_order"));
        Assert.False(a.Flag("no_image_ocr"));
    }

    [Fact]
    public void Options_take_values_and_abbreviations()
    {
        Namespace a = Parse("--form=json", "--dpi=100", "--threads", "3", "--model-order",
                            "--keep-furniture", "a.pdf", "b.png");
        Assert.Equal("json", a.Str("format"));
        Assert.Equal(100.0, a.Float("dpi"));
        Assert.Equal(3, a.Int("cores"));
        Assert.True(a.Flag("model_order"));
        Assert.True(a.Flag("keep_furniture"));
        Assert.Equal(["a.pdf", "b.png"], a.PathList("inputs").Select(p => p.ToString()));
    }

    [Fact]
    public void Paths_are_normalized_and_the_double_dash_ends_the_options()
    {
        Namespace a = Parse("--out", "temp/x/", "a/../b.pdf");
        Assert.Equal("temp\\x", a.PathOrNull("out")!.Value.ToString());
        Assert.Equal(["a\\..\\b.pdf"], a.PathList("inputs").Select(p => p.ToString()));

        Namespace after = Parse("--", "--nope");
        Assert.Equal(["--nope"], after.PathList("inputs").Select(p => p.ToString()));
    }

    [Fact]
    public void Help_and_version_leave_nothing_in_the_namespace()
    {
        // argparse's SUPPRESS: the two actions never write their dest.
        Assert.Throws<ArgparseExit>(() => CliParser.Build().ParseArgs(["a.pdf", "--help"]));
        Namespace a = Parse("a.pdf");
        Assert.Null(a.Get("help"));
        Assert.Null(a.Get("version"));
    }
}
