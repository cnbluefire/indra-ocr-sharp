using System.Text.RegularExpressions;
using Indra.Ocr.ArgumentParsing;
using Indra.Ocr.Cli;

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
    private static ParsedArguments Parse(params string[] argv) => CliParser.Build().ParseArgs(argv);

    private static ArgumentParserExit Fail(params string[] argv)
        => Assert.Throws<ArgumentParserExit>(() => CliParser.Build().ParseArgs(argv));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    /// <summary>The one host-dependent token of the captured help: <c>--threads</c>' default.</summary>
    private static readonly Regex HelpCores = new(@"here \d+\)");

    [Fact]
    public void Help_matches_the_reference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "cli_help.txt");
        string reference = Normalize(File.ReadAllText(path));
        // The fixture is the reference's own `--help` (`uv run indra-ocr --help`), captured on a
        // machine with eight usable cores — and `--threads`' help spells the host's count
        // (`usable_cores()`), so that one number belongs to the machine that captured it. It is put
        // back to this host's before the comparison; everything else is the reference's bytes. The
        // count's rule is `CliParser.UsableCores` and the whole text is compared against the
        // reference running on the *same* machine by tools/scripts/test/cli_parity.py.
        int substituted = 0;
        string expected = HelpCores.Replace(reference, _ =>
        {
            substituted++;
            return $"here {CliParser.UsableCores()})";
        });
        Assert.Equal(1, substituted);

        Assert.Equal(expected, Normalize(CliParser.Build().FormatHelp()));
    }

    [Fact]
    public void Help_short_flag_prints_the_same_help()
    {
        ArgumentParserExit exit = Assert.Throws<ArgumentParserExit>(() => CliParser.Build().ParseArgs(["-h"]));
        Assert.Equal(0, exit.Status);
        Assert.Equal("", exit.Stderr);
        Assert.Equal(Normalize(CliParser.Build().FormatHelp()), Normalize(exit.Stdout));
    }

    [Fact]
    public void Version_prints_the_product_line()
    {
        ArgumentParserExit exit = Assert.Throws<ArgumentParserExit>(() => CliParser.Build().ParseArgs(["--version"]));
        Assert.Equal(0, exit.Status);
        Assert.Equal("", exit.Stderr);
        Assert.Equal("indra-ocr 1.1.2\n", exit.Stdout);
    }

    [Fact]
    public void Errors_are_argparse_wording_and_exit_two()
    {
        ArgumentParserExit noInputs = Fail();
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
        Assert.EndsWith("indra-ocr: error: argument --max-pages: invalid _page_count value: 'x'\n",
                        Fail("--max-pages", "x", "a.pdf").Stderr);
        Assert.EndsWith("indra-ocr: error: argument --mode: invalid choice: 'nope' " +
                        "(choose from 'auto', 'ocr', 'text')\n", Fail("--mode", "nope", "a.pdf").Stderr);
    }

    [Fact]
    public void Ambiguous_abbreviation_lists_the_matches_in_option_order()
    {
        ArgumentParserExit exit = Fail("--m", "a.pdf");
        Assert.Equal(
            "indra-ocr: error: ambiguous option: --m could match --mode, " +
            "--model-dir, --max-pages, --math\n", exit.Stderr[(exit.Stderr.IndexOf("indra-ocr: error:")..)]);
    }

    [Fact]
    public void Defaults_are_the_reference_defaults()
    {
        ParsedArguments a = Parse("a.pdf");
        Assert.Equal(["a.pdf"], a.StrList("inputs"));
        Assert.Null(a.Str("out"));
        Assert.Null(a.Str("model_dir"));
        Assert.Equal("md", a.Str("format"));
        Assert.Equal("auto", a.Str("mode"));
        Assert.Null(a.Str("password"));
        Assert.Equal(0, a.Int("cores"));
        Assert.Equal(200.0, a.Float("dpi"));
        Assert.Equal(0, a.Int("max_pages"));
        Assert.False(a.Flag("math"));
        Assert.False(a.Flag("keep_furniture"));
        Assert.False(a.Flag("timings"));
        Assert.False(a.Flag("no_image_ocr"));
    }

    [Fact]
    public void Options_take_values_and_abbreviations()
    {
        ParsedArguments a = Parse("--form=json", "--dpi=100", "--threads", "3",
                            "--keep-furniture", "a.pdf", "b.png");
        Assert.Equal("json", a.Str("format"));
        Assert.Equal(100.0, a.Float("dpi"));
        Assert.Equal(3, a.Int("cores"));
        Assert.True(a.Flag("keep_furniture"));
        Assert.Equal(["a.pdf", "b.png"], a.StrList("inputs"));
    }

    [Fact]
    public void Paths_are_normalized_and_the_double_dash_ends_the_options()
    {
        ParsedArguments a = Parse("--out", "temp/x/", "a/../b.pdf");
        // `type=Path`: str(Path(s)) in the host's flavour (on POSIX a backslash is a name).
        Assert.Equal(Path.Combine("temp", "x"), a.Str("out"));
        Assert.Equal([Path.Combine("a", "..", "b.pdf")], a.StrList("inputs"));

        ParsedArguments after = Parse("--", "--nope");
        Assert.Equal(["--nope"], after.StrList("inputs"));
    }

    [Fact]
    public void Help_and_version_leave_nothing_in_the_namespace()
    {
        // argparse's SUPPRESS: the two actions never write their dest.
        Assert.Throws<ArgumentParserExit>(() => CliParser.Build().ParseArgs(["a.pdf", "--help"]));
        ParsedArguments a = Parse("a.pdf");
        Assert.Null(a.Get("help"));
        Assert.Null(a.Get("version"));
    }
}
