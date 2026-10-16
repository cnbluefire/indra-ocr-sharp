// indra-ocr (C# port) — the command line: PDFs or images in, markdown (or text, or JSON) out.
using Indra.Ocr.Cli.Worker;

namespace Indra.Ocr.Cli;

internal static class Program
{
    private static int Main(string[] args)
        // A worker is this same program, started by the pool (step 190); it speaks the pool's
        // protocol on stdin/stdout instead of reading a command line.
        => WorkerHost.IsWorker ? WorkerHost.Run() : Cli.Run(args);
}
