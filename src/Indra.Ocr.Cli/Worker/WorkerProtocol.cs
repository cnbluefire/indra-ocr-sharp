using Indra.Ocr.Layout;
using Indra.Ocr.Pipeline;

namespace Indra.Ocr.Cli.Worker;

/// <summary>One unit of work: a page of a PDF, or a whole image file.</summary>
/// <remarks>
/// The reference's tuple: <c>(path, page index, password)</c> for a PDF and
/// <c>(path, None, max pages)</c> for an image file, so <see cref="Extra"/> is the password
/// (<c>null</c> for none) in the first case and the page cap in the second.
/// </remarks>
internal readonly record struct WorkUnit(string Path, int? PageIndex, object? Extra);

/// <summary>
/// The options a worker process needs to build its converter — the reference's
/// <c>load_kwargs</c> (what <c>indra_ocr.load</c> takes).
/// </summary>
internal sealed record LoadOptions(
    string ModelDirectory,
    double Dpi,
    bool Furniture,
    bool Math,
    string Mode,
    bool PdfOrder,
    bool ImageOcr);

/// <summary>
/// One unit's outcome: the pages it produced, or the <see cref="InputError"/> that stopped
/// the file (the reference's <c>_run</c> returns either a list of <c>(index, Page)</c> or the
/// exception).
/// </summary>
internal sealed class WorkResult
{
    public WorkResult(List<(int Index, OcrPage Page)> pages) => Pages = pages;

    public WorkResult(InputError error) => Error = error;

    /// <summary>The error that stopped the file, or <c>null</c>.</summary>
    public InputError? Error { get; }

    /// <summary>The pages, in page order (empty when <see cref="Error"/> is set).</summary>
    public List<(int Index, OcrPage Page)> Pages { get; } = new();
}

/// <summary>
/// The parent ↔ worker wire format.
/// </summary>
/// <remarks>
/// The reference pickles <c>(index, Page)</c> through <c>ProcessPoolExecutor</c>; the port
/// speaks a small length-prefixed binary format over the worker's stdin/stdout instead
/// (<see cref="BinaryWriter"/>'s UTF-8 strings and little-endian doubles), which round-trips
/// every value — text of any script, and the exact double bits of the boxes, confidences,
/// skew and timings — without any escaping. The format is private to the two ends, so no
/// compatibility is owed to the reference's pickles.
/// <para>
/// <b>The reader must consume exactly what the writer wrote.</b> Where a field is optional — a
/// PDF's password, a page's markdown — the writer <em>always</em> writes the value and the
/// reader <em>always</em> reads it; the flag beside it only says whether the value is
/// meaningful. Writing a value the reader skips when the flag is false leaves the stream a byte
/// out, and every later read is shifted (both bugs this format had behaved that way).
/// <c>WorkerProtocolTests</c> round-trips streams of units and results and asserts the reader
/// consumed the whole stream.
/// </para>
/// </remarks>
internal static class WorkerProtocol
{
    /// <summary>The environment variable that turns a process into a worker.</summary>
    internal const string WorkerVariable = "INDRA_OCR_WORKER";

    // ------------------------------------------------------------------ config

    internal static void WriteConfig(BinaryWriter writer, LoadOptions options, int threads)
    {
        writer.Write(threads);
        writer.Write(options.ModelDirectory);
        writer.Write(options.Dpi);
        writer.Write(options.Furniture);
        writer.Write(options.Math);
        writer.Write(options.Mode);
        writer.Write(options.PdfOrder);
        writer.Write(options.ImageOcr);
        writer.Flush();
    }

    internal static (LoadOptions Options, int Threads) ReadConfig(BinaryReader reader)
    {
        int threads = reader.ReadInt32();
        var options = new LoadOptions(
            reader.ReadString(),
            reader.ReadDouble(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadString(),
            reader.ReadBoolean(),
            reader.ReadBoolean());
        return (options, threads);
    }

    // ------------------------------------------------------------------ units

    internal static void WriteUnit(BinaryWriter writer, WorkUnit unit)
    {
        writer.Write(unit.PageIndex is not null);
        writer.Write(unit.Path);
        if (unit.PageIndex is int index)
        {
            writer.Write(index);
            writer.Write(unit.Extra is string);
            writer.Write(unit.Extra as string ?? "");
        }
        else
        {
            writer.Write(unit.Extra is int pages ? pages : 0);
        }

        writer.Flush();
    }

    internal static WorkUnit ReadUnit(BinaryReader reader)
    {
        bool isPdf = reader.ReadBoolean();
        string path = reader.ReadString();
        if (!isPdf)
        {
            return new WorkUnit(path, null, reader.ReadInt32());
        }

        int index = reader.ReadInt32();
        bool hasPassword = reader.ReadBoolean();
        string password = reader.ReadString();
        return new WorkUnit(path, index, hasPassword ? password : null);
    }

    // ------------------------------------------------------------------ results

    internal static void WriteResult(BinaryWriter writer, WorkResult result)
    {
        writer.Write(result.Error is null);
        if (result.Error is InputError error)
        {
            writer.Write(error.Message);
        }
        else
        {
            writer.Write(result.Pages.Count);
            foreach ((int index, OcrPage page) in result.Pages)
            {
                WritePage(writer, index, page);
            }
        }

        writer.Flush();
    }

    internal static WorkResult ReadResult(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return new WorkResult(new InputError(reader.ReadString()));
        }

        int count = reader.ReadInt32();
        var pages = new List<(int Index, OcrPage Page)>(count);
        for (int i = 0; i < count; i++)
        {
            pages.Add(ReadPage(reader));
        }

        return new WorkResult(pages);
    }

    // ------------------------------------------------------------------ a page

    private static void WritePage(BinaryWriter writer, int index, OcrPage page)
    {
        writer.Write(index);
        writer.Write(page.Source);
        writer.Write(page.Note);
        writer.Write(page.Markdown is not null);
        writer.Write(page.Markdown ?? "");
        writer.Write(page.Rotation);
        writer.Write(page.Skew);

        writer.Write(page.Lines.Count);
        foreach (OcrLine line in page.Lines)
        {
            writer.Write(line.Box.Count);
            foreach (double value in line.Box)
            {
                writer.Write(value);
            }

            writer.Write(line.Text);
            writer.Write(line.Confidence);
        }

        writer.Write(page.Blocks.Count);
        foreach (LayoutBlock block in page.Blocks)
        {
            writer.Write(block.Label);
            writer.Write(block.Bbox.Length);
            foreach (double value in block.Bbox)
            {
                writer.Write(value);
            }

            writer.Write(block.Text);
        }

        writer.Write(page.Timings.Count);
        foreach ((string key, double value) in page.Timings)
        {
            writer.Write(key);
            writer.Write(value);
        }
    }

    private static (int Index, OcrPage Page) ReadPage(BinaryReader reader)
    {
        int index = reader.ReadInt32();
        string source = reader.ReadString();
        string note = reader.ReadString();
        bool hasMarkdown = reader.ReadBoolean();
        string markdownText = reader.ReadString();
        int rotation = reader.ReadInt32();
        double skew = reader.ReadDouble();

        int lineCount = reader.ReadInt32();
        var lines = new List<OcrLine>(lineCount);
        for (int i = 0; i < lineCount; i++)
        {
            int boxCount = reader.ReadInt32();
            var box = new List<double>(boxCount);
            for (int j = 0; j < boxCount; j++)
            {
                box.Add(reader.ReadDouble());
            }

            lines.Add(new OcrLine(box, reader.ReadString(), reader.ReadDouble()));
        }

        int blockCount = reader.ReadInt32();
        var blocks = new List<LayoutBlock>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            string label = reader.ReadString();
            int bboxCount = reader.ReadInt32();
            var bbox = new double[bboxCount];
            for (int j = 0; j < bboxCount; j++)
            {
                bbox[j] = reader.ReadDouble();
            }

            blocks.Add(new LayoutBlock(label, bbox, reader.ReadString()));
        }

        int timingCount = reader.ReadInt32();
        var timings = new Dictionary<string, double>(timingCount);
        for (int i = 0; i < timingCount; i++)
        {
            string key = reader.ReadString();
            timings[key] = reader.ReadDouble();
        }

        var page = new OcrPage(lines, timings, hasMarkdown ? markdownText : null, source, note,
                               blocks, rotation, skew);
        return (index, page);
    }
}
