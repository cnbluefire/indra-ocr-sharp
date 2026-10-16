using Indra.Ocr.Argparse;
using Indra.Ocr.Py;

namespace Indra.Ocr.Viewer;

/// <summary>
/// The viewer's command line: <c>viewer._main</c>'s parser, spelled option for option as the
/// reference has it. It is the same parser engine the command line uses
/// (<see cref="ArgumentParser"/>), because <c>--help</c>, <c>--version</c> and the error messages
/// are part of the surface that has to match.
/// </summary>
internal static class ViewerParser
{
    /// <summary>The reference's module docstring: the parser's description, verbatim.</summary>
    internal const string Description =
        """
        Local viewer: drop in a PDF or an image, see what Indra OCR reads.

            indra-ocr-viewer                      # models from the Hugging Face repo
            open http://localhost:8765

        Each page shows its lines, layout blocks and reading order over the page (each
        can be shown or hidden), the rendered markdown, the markdown source, the lines
        with their confidences, the JSON the CLI writes, where the text came from (the
        PDF text layer, OCR, or both) and the time of each stage.  The pipeline options
        (text layer or OCR, math, page headers and footers, reading order, OCR of text
        inside images, dpi) are set in the page and apply when the document is read
        again.  Pages are read one at a time and appear as they finish.  With
        --tesseract (needs the tesseract binary and pytesseract) the page can also run
        Tesseract on each page.
        """;

    /// <summary><c>viewer._main</c>'s parser: the same arguments, the same help strings.</summary>
    internal static ArgumentParser Build()
    {
        var ap = new ArgumentParser("indra-ocr-viewer", Description);
        ap.AddArgument(["--model-dir"], type: ArgType.Path, @default: null,
                       help: "folder with the models (default: download from Hugging Face)");
        SourceArgs.AddTo(ap);
        ap.AddArgument(["--version"], action: ArgAction.Version,
                       version: $"{ProductInfo.Name} {ProductInfo.Version}");
        ap.AddArgument(["--port"], type: ArgType.Int, @default: 8765L);
        ap.AddArgument(["--threads"], type: ArgType.Int, @default: 4L);
        ap.AddArgument(["--dpi"], type: ArgType.Float, @default: 200.0,
                       help: "initial PDF render resolution");
        ap.AddArgument(["--max-pages"], type: ArgType.Int, @default: 20L,
                       help: "initial page limit per document");
        ap.AddArgument(["--keep-furniture"], action: ArgAction.StoreTrue,
                       help: "initially keep page headers and footers");
        ap.AddArgument(["--tesseract"], action: ArgAction.StoreTrue,
                       help: "allow comparing with Tesseract");
        ap.AddArgument(["--tesseract-lang"], @default: "eng");
        ap.AddArgument(["--math"], action: ArgAction.StoreTrue,
                       help: "initially write math as LaTeX");
        return ap;
    }

    /// <summary>The parsed command line, as <c>viewer._main</c> reads it off the namespace.</summary>
    internal static ViewerArgs Read(Namespace a)
    {
        (string mode, bool pdfOrder, bool imageOcr) = SourceArgs.Options(a);
        return new ViewerArgs(
            ModelDir: a.PathOrNull("model_dir"),
            Port: a.Int("port"),
            Threads: a.Int("threads"),
            Tesseract: a.Flag("tesseract"),
            TesseractLang: a.Str("tesseract_lang") ?? "eng",
            Defaults: new ViewerDefaults(
                Mode: mode,
                PdfOrder: pdfOrder,
                ImageOcr: imageOcr,
                Math: a.Flag("math"),
                Furniture: a.Flag("keep_furniture"),
                Dpi: a.Float("dpi"),
                MaxPages: a.Int("max_pages")));
    }
}

/// <summary>What the viewer's command line produces (<c>viewer._main</c>'s locals).</summary>
internal sealed record ViewerArgs(
    PyPath? ModelDir,
    int Port,
    int Threads,
    bool Tesseract,
    string TesseractLang,
    ViewerDefaults Defaults);
