// indra-ocr (C# port) — the command line: PDFs or images in, markdown (or text, or JSON) out.
namespace Indra.Ocr.Cli;

internal static class Program
{
    private static int Main(string[] args) => Cli.Run(args);
}
