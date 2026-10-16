using Indra.Ocr.Cli.Worker;
using Indra.Ocr.Layout;
using Indra.Ocr.Pipeline;

namespace Indra.Ocr.Tests;

/// <summary>
/// The parent ↔ worker wire format: every value comes back as it went in, and a stream of
/// units keeps its place.
/// </summary>
/// <remarks>
/// The stream test is the important one. The first version of <see cref="WorkerProtocol.WriteUnit"/>
/// wrote the password string even when the flag said there was none, while
/// <see cref="WorkerProtocol.ReadUnit"/> only read it when the flag was set: after a worker's
/// first PDF page the stream was one byte out, so its second page read garbage (and the pool
/// hung on the page after that). One unit per worker never noticed.
/// </remarks>
public class WorkerProtocolTests
{
    private static LoadOptions Options
        => new("resources\\models", 200.0, Furniture: true, Math: false, "auto", PdfOrder: true,
               ImageOcr: false);

    private static (BinaryWriter Writer, MemoryStream Stream) Sink()
    {
        var stream = new MemoryStream();
        return (new BinaryWriter(stream), stream);
    }

    /// <summary>
    /// The reader must consume exactly what the writer wrote: a field the writer always writes
    /// but the reader skips when a flag is false shifts every later read (which is how both of
    /// this format's bugs behaved).
    /// </summary>
    private static void AssertConsumed(MemoryStream stream)
    {
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void Config_round_trips()
    {
        (BinaryWriter writer, MemoryStream stream) = Sink();
        WorkerProtocol.WriteConfig(writer, Options, 1);
        writer.Flush();

        stream.Position = 0;
        (LoadOptions options, int threads) = WorkerProtocol.ReadConfig(new BinaryReader(stream));

        Assert.Equal(1, threads);
        Assert.Equal(Options, options);
    }

    [Fact]
    public void Units_round_trip_in_a_stream()
    {
        // A password, none, none again, an image file, another image file, an empty password:
        // if the empty-password byte is written but not read, everything after it shifts.
        var units = new List<WorkUnit>
        {
            new("a.pdf", 0, "secret"),
            new("a.pdf", 1, null),
            new("a.pdf", 2, null),
            new("b.png", null, 0),
            new("c.png", null, 2),
            new("a.pdf", 3, ""),
            new("d.png", null, -1),
        };

        (BinaryWriter writer, MemoryStream stream) = Sink();
        foreach (WorkUnit unit in units)
        {
            WorkerProtocol.WriteUnit(writer, unit);
        }

        writer.Flush();
        stream.Position = 0;
        var reader = new BinaryReader(stream);
        foreach (WorkUnit expected in units)
        {
            Assert.Equal(expected, WorkerProtocol.ReadUnit(reader));
        }

        AssertConsumed(stream);
    }

    [Fact]
    public void Pages_round_trip()
    {
        var page = new OcrPage(
            lines: [new OcrLine([1.0, 2.5, 3.0, 4.0], "héllo — 世界", 0.9876)],
            timings: new Dictionary<string, double> { ["total_ms"] = 12.5, ["det_ms"] = 0.125 },
            markdown: "# Title\n\nbody\n",
            source: "hybrid",
            note: "",
            blocks: [new LayoutBlock("Text", [0.0, 1.0, 2.0, 3.0], "héllo")],
            rotation: 90,
            skew: -0.75);
        var result = new WorkResult([(3, page), (4, new OcrPage([], source: "text"))]);

        (BinaryWriter writer, MemoryStream stream) = Sink();
        WorkerProtocol.WriteResult(writer, result);
        writer.Flush();

        stream.Position = 0;
        WorkResult back = WorkerProtocol.ReadResult(new BinaryReader(stream));

        Assert.Null(back.Error);
        Assert.Equal(2, back.Pages.Count);

        (int index, OcrPage first) = back.Pages[0];
        Assert.Equal(3, index);
        Assert.Equal("hybrid", first.Source);
        Assert.Equal("", first.Note);
        Assert.Equal("# Title\n\nbody\n", first.Markdown);
        Assert.Equal(90, first.Rotation);
        Assert.Equal(-0.75, first.Skew);
        Assert.Equal(12.5, first.Timings["total_ms"]);
        Assert.Equal(0.125, first.Timings["det_ms"]);
        OcrLine line = Assert.Single(first.Lines);
        Assert.Equal("héllo — 世界", line.Text);
        Assert.Equal(0.9876, line.Confidence);
        Assert.Equal(new[] { 1.0, 2.5, 3.0, 4.0 }, line.Box);
        LayoutBlock block = Assert.Single(first.Blocks);
        Assert.Equal("Text", block.Label);
        Assert.Equal("héllo", block.Text);
        Assert.Equal(new[] { 0.0, 1.0, 2.0, 3.0 }, block.Bbox);

        (int secondIndex, OcrPage second) = back.Pages[1];
        Assert.Equal(4, secondIndex);
        Assert.Equal("text", second.Source);
        Assert.Null(second.Markdown);
        Assert.Empty(second.Lines);
        Assert.Empty(second.Blocks);
        Assert.Empty(second.Timings);

        AssertConsumed(stream);
    }

    [Fact]
    public void Results_round_trip_in_a_stream()
    {
        var pages = new WorkResult([(0, new OcrPage([], source: "ocr", note: "tilted"))]);
        var failure = new WorkResult(new InputError("a.pdf: no such file"));
        var empty = new WorkResult([]);

        (BinaryWriter writer, MemoryStream stream) = Sink();
        WorkerProtocol.WriteResult(writer, pages);
        WorkerProtocol.WriteResult(writer, failure);
        WorkerProtocol.WriteResult(writer, empty);
        writer.Flush();

        stream.Position = 0;
        var reader = new BinaryReader(stream);

        WorkResult first = WorkerProtocol.ReadResult(reader);
        Assert.Null(first.Error);
        Assert.Equal("tilted", Assert.Single(first.Pages).Page.Note);

        WorkResult second = WorkerProtocol.ReadResult(reader);
        Assert.NotNull(second.Error);
        Assert.Equal("a.pdf: no such file", second.Error.Message);

        WorkResult third = WorkerProtocol.ReadResult(reader);
        Assert.Null(third.Error);
        Assert.Empty(third.Pages);

        AssertConsumed(stream);
    }
}
