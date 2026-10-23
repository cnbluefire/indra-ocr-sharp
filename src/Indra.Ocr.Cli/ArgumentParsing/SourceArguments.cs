namespace Indra.Ocr.ArgumentParsing;

/// <summary>
/// <c>cli.add_source_args</c> and <c>cli.source_options</c>: the text-layer/OCR group. Both hosts
/// add it — the reference's <c>viewer.py</c> imports the pair from <c>cli.py</c>, and the viewer
/// project references this one — so it lives beside the parser, and the two help texts cannot
/// drift apart.
/// </summary>
public static class SourceArguments
{
    /// <summary>The group's title, as <c>add_argument_group</c> spells it.</summary>
    public const string Title = "text layer or OCR (PDFs)";

    /// <summary><c>cli.add_source_args</c>: the three options, in the reference's order.</summary>
    public static void AddTo(ArgumentParser ap)
    {
        ArgumentGroup group = ap.AddArgumentGroup(Title);
        group.AddArgument(
            ["--mode"],
            choices: ["auto", "ocr", "text"],
            @default: "auto",
            help: "auto: text layer where it passes the checks, OCR elsewhere (default); " +
                  "ocr: never read the text layer; text: use the text layer even when the " +
                  "checks fail");
        group.AddArgument(
            ["--no-image-ocr"],
            action: ArgumentAction.StoreTrue,
            help: "on text-layer pages, do not OCR text inside images");
    }

    /// <summary><c>cli.source_options</c>: the mode, and the two flags the options invert.</summary>
    public static (string Mode, bool PdfOrder, bool ImageOcr) Options(ParsedArguments a)
        => (a.Str("mode") ?? "auto", true, !a.Flag("no_image_ocr"));
}
