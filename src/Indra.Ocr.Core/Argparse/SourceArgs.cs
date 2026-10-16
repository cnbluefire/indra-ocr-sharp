namespace Indra.Ocr.Argparse;

/// <summary>
/// <c>cli.add_source_args</c> and <c>cli.source_options</c>: the text-layer/OCR group. Both hosts
/// add it — the reference's <c>viewer.py</c> imports the pair from <c>cli.py</c> — so it lives
/// beside the parser rather than in either of them, and the two help texts cannot drift apart.
/// </summary>
public static class SourceArgs
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
            ["--model-order"],
            action: ArgAction.StoreTrue,
            help: "order text-layer lines with the order model, not the PDF content order");
        group.AddArgument(
            ["--no-image-ocr"],
            action: ArgAction.StoreTrue,
            help: "on text-layer pages, do not OCR text inside images");
    }

    /// <summary><c>cli.source_options</c>: the mode, and the two flags the options invert.</summary>
    public static (string Mode, bool PdfOrder, bool ImageOcr) Options(Namespace a)
        => (a.Str("mode") ?? "auto", !a.Flag("model_order"), !a.Flag("no_image_ocr"));
}
