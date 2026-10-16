using Indra.Ocr.Argparse;

namespace Indra.Ocr.Cli;

/// <summary>
/// The command line's arguments: <c>cli._main</c>'s parser and <c>cli.add_source_args</c>,
/// spelled option for option as the reference has them.
/// </summary>
/// <remarks>
/// It sits beside <see cref="Cli"/> rather than inside it so the tests can build the very
/// parser the program uses (and check its help and its errors against the reference's).
/// </remarks>
internal static class CliParser
{
    /// <summary>The reference's module docstring: the parser's description, verbatim.</summary>
    internal const string Description =
        "Command line: PDFs or images in, markdown (or text, or JSON lines) out.\n" +
        "\n" +
        "indra-ocr paper.pdf                    # markdown to stdout\n" +
        "indra-ocr *.pdf --out results/         # one .md per input\n" +
        "indra-ocr scan.png --format json       # lines with boxes, confidences, timings\n" +
        "\n" +
        "Pages run in parallel on all the cores the process may use (--cores to limit).\n";

    /// <summary>
    /// <c>cli._main</c>'s parser: the same arguments, the same help strings, the same groups.
    /// </summary>
    internal static ArgumentParser Build()
    {
        var ap = new ArgumentParser("indra-ocr", Description);
        ap.AddArgument(["--version"], action: ArgAction.Version,
                       version: $"{ProductInfo.Name} {ProductInfo.Version}");
        ap.AddPositional("inputs", nargs: Nargs.OneOrMore, type: ArgType.Path);
        ap.AddArgument(["--out"], type: ArgType.Path, @default: null,
                       help: "write one file per input here");
        ap.AddArgument(["--format"], choices: ["md", "text", "json"], @default: "md");
        SourceArgs.AddTo(ap);
        ap.AddArgument(["--model-dir"], type: ArgType.Path, @default: null);
        ap.AddArgument(["--cores", "--threads"], dest: "cores", type: ArgType.Int, @default: 0L,
                       help: "cores to use (default: all this process may run on, here " +
                             $"{UsableCores()})");
        ap.AddArgument(["--dpi"], type: ArgType.Float, @default: 200.0);
        ap.AddArgument(["--max-pages"], type: ArgType.Int, @default: 0L);
        ap.AddArgument(["--password"], @default: null, help: "password of encrypted PDFs");
        ap.AddArgument(["--math"], action: ArgAction.StoreTrue,
                       help: "write math as LaTeX ($...$); slower on pages with math " +
                             "(off by default)");
        ap.AddArgument(["--keep-furniture"], action: ArgAction.StoreTrue,
                       help: "keep page headers and footers (page numbers, running titles) " +
                             "in the markdown");
        ap.AddArgument(["--timings"], action: ArgAction.StoreTrue,
                       help: "print per-page timings to stderr");
        return ap;
    }

    /// <summary><c>parallel.usable_cores</c>: the cores this process may run on.</summary>
    internal static int UsableCores() => Math.Max(1, Environment.ProcessorCount);
}
