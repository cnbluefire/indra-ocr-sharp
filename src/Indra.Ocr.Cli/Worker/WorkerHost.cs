using System.Text;
using Indra.Ocr.Images;
using Indra.Ocr.Pdf;
using Indra.Ocr.Pipeline;

namespace Indra.Ocr.Cli.Worker;

/// <summary>
/// The worker half of <c>convert_many</c>: the reference's <c>_init</c> and <c>_run</c>.
/// </summary>
/// <remarks>
/// A worker is this same program started with <see cref="WorkerProtocol.WorkerVariable"/> set
/// (the port's stand-in for the reference's <c>ProcessPoolExecutor</c> with the spawn context
/// and <c>initializer=_init</c>). It reads its <c>load_kwargs</c> once, builds the converter,
/// and then runs one unit per request, keeping the last PDF open across them — exactly like the
/// reference's module-global <c>_doc</c> — because pdfium is not thread-safe and opening a
/// document per page would dominate the cost.
/// </remarks>
internal static class WorkerHost
{
    /// <summary>Whether this process was started to be a worker.</summary>
    internal static bool IsWorker
        => Environment.GetEnvironmentVariable(WorkerProtocol.WorkerVariable) == "1";

    /// <summary>The worker loop: config, then one result per unit until stdin ends.</summary>
    internal static int Run()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var reader = new BinaryReader(input, Encoding.UTF8);
        using var writer = new BinaryWriter(output, Encoding.UTF8);

        (LoadOptions options, int threads) = WorkerProtocol.ReadConfig(reader);
        using var ocr = new OrtOcr(options.ModelDirectory, threads: threads, providers: null,
                                   furniture: options.Furniture, math: options.Math);
        var pdf = new PdfConverter(ocr, options.Dpi, options.Mode, options.PdfOrder,
                                   options.ImageOcr);
        var images = new ImageConverter(ocr);

        PdfDocument? document = null;
        string? documentPath = null;
        try
        {
            while (true)
            {
                WorkUnit unit;
                try
                {
                    unit = WorkerProtocol.ReadUnit(reader);
                }
                catch (EndOfStreamException)
                {
                    break; // the parent is done (it closes our stdin)
                }

                WorkerProtocol.WriteResult(writer, RunUnit(unit, pdf, images, ref document,
                                                           ref documentPath));
            }
        }
        finally
        {
            document?.Dispose();
        }

        return 0;
    }

    /// <summary>The reference's <c>_run</c>: the pages of one unit, or the InputError.</summary>
    private static WorkResult RunUnit(WorkUnit unit, PdfConverter pdf, ImageConverter images,
                                      ref PdfDocument? document, ref string? documentPath)
    {
        try
        {
            if (unit.PageIndex is null)
            {
                int maxPages = unit.Extra is int cap ? cap : 0;
                return new WorkResult(images.ConvertFrames(unit.Path, maxPages).ToList());
            }

            if (document is null || documentPath != unit.Path)
            {
                document?.Dispose();
                document = PdfConverter.Open(unit.Path, unit.Extra as string);
                documentPath = unit.Path;
            }

            int index = unit.PageIndex.Value;
            PdfDocument doc = document;
            OcrPage page = SafePage.Read(() => pdf.ConvertPage(doc, index));
            return new WorkResult([(index, page)]);
        }
        catch (InputError e)
        {
            return new WorkResult(e);
        }
    }
}
