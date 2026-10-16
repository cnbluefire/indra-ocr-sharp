using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Indra.Ocr.Cli;
using Indra.Ocr.Cli.Worker;
using Indra.Ocr.Images;
using Indra.Ocr.Onnx;
using Indra.Ocr.Pdf;
using Indra.Ocr.Pipeline;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Indra.Ocr.Tests;

/// <summary>
/// The reference's own <c>tests/test_inputs.py</c>, ported case for case: which inputs are accepted,
/// what a page is, and the model folder's policy.
/// </summary>
/// <remarks>
/// The reference drives <c>document.Converter</c> with a <c>StubOCR</c> — a callable that counts
/// pages and raises on an all black one — and monkeypatches
/// <c>huggingface_hub.snapshot_download</c>. The port's <see cref="OrtOcr"/> owns its ONNX sessions
/// and its download is one self-contained operation, so the cases that needed those are written
/// against the seams the port does have: <see cref="SafePage.Read"/> for a failing page, and a
/// local <c>HF_ENDPOINT</c> for the download policy (the reference's
/// <c>huggingface_hub.constants.ENDPOINT</c> reads the same variable). The sample PDFs come from
/// <c>fixtures/document.json</c> instead of being built with pypdfium2 — including the
/// password-protected one, whose password is <c>secret</c> — and the images the reference makes with
/// OpenCV are made here with ImageSharp, which is the codec the port already has.
/// <para>
/// The class writes environment variables, so it is serialized with the PDFium tests and restores
/// every variable it touched. <c>test_version_flag</c> is not here: it is a command-line surface
/// (one line on stdout, exit 0) and is covered by <c>ArgparseTests</c>, <c>ProductSmokeTests</c> and
/// the <c>version</c> case of <c>cli_parity.py</c>.
/// </para>
/// </remarks>
[Collection("pdfium")]
public class ReferenceInputsTests
{
    private static readonly JsonElement Fx = Fixtures.Load("document.json");

    // ---------------------------------------------------------------- the reference's own cases

    /// <summary><c>test_file_type_comes_from_the_bytes</c>: the bytes, never the name, decide.</summary>
    [Fact]
    public void File_type_comes_from_the_bytes()
    {
        string dir = TempDir();
        string pdf = WriteSample(dir, "no_extension", "text");   // a PDF that is not called .pdf
        string png = WritePng(Path.Combine(dir, "image_named.pdf"), 40, 50, 255);

        Assert.True(OrtOcr.IsPdf(pdf));
        Assert.False(OrtOcr.IsPdf(png));

        if (TestModels.Directory is null)
        {
            return;
        }

        using var ocr = new OrtOcr(TestModels.Directory, threads: 4);
        var converter = new PdfConverter(ocr, mode: "ocr");
        Assert.Equal(new[] { "ocr" },
                     converter.ConvertPages(pdf, null, [0]).Select(p => p.Page.Source).ToArray());
        Assert.Single(new ImageConverter(ocr).ConvertFrames(png));
    }

    /// <summary><c>test_every_tiff_frame_is_a_page</c>.</summary>
    /// <remarks>
    /// The reference writes the three frames with <c>cv2.imwritemulti</c>; here they are written
    /// with ImageSharp (which is why the frames are asserted by count, size and value rather than
    /// as one exact grayscale blob).
    /// </remarks>
    [Fact]
    public void Every_tiff_frame_is_a_page()
    {
        string path = WriteTiff(Path.Combine(TempDir(), "three.tiff"), 255, 200, 255);

        List<GrayImageData> frames = ImageFiles.ReadImages(path);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f =>
        {
            Assert.Equal(60, f.Width);
            Assert.Equal(80, f.Height);
        });
        Assert.Equal(255, frames[0].Data.Span[0]);
        Assert.Equal(200, frames[1].Data.Span[0]);
        Assert.Equal(255, frames[2].Data.Span[0]);
    }

    /// <summary><c>test_unreadable_files_raise_input_error</c>.</summary>
    /// <remarks>
    /// The reference converts each file with the whole <c>Converter</c>; the port's dispatch is the
    /// host's (<c>Cli.Plan</c> and <c>document.convert</c> both check the file exists first, then
    /// let the bytes pick the reader), so <see cref="Read"/> is that dispatch without a recognizer.
    /// </remarks>
    [Fact]
    public void Unreadable_files_raise_input_error()
    {
        string dir = TempDir();
        (string Name, byte[] Data)[] cases =
        [
            ("garbage.pdf", "%PDF-1.4 broken"u8.ToArray()),   // looks like a PDF, will not open
            ("empty.png", []),                                // no bytes at all
            ("text.txt", "hello"u8.ToArray()),                // bytes, but not an image
        ];

        foreach ((string name, byte[] data) in cases)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            Assert.Throws<InputError>(() => Read(path));
        }

        var missing = Assert.Throws<InputError>(() => Read(Path.Combine(dir, "missing.pdf")));
        Assert.Contains("no such file", missing.Message);
    }

    /// <summary><c>test_password_protected_pdf</c>.</summary>
    /// <remarks>
    /// The reference builds its own locked PDF and uses the password <c>pw</c>; the fixture's locked
    /// sample takes <c>secret</c>, and opening it (rather than converting it with the stub) is the
    /// same fact: without the password it is one clear error, with it the one page is there.
    /// </remarks>
    [Fact]
    public void Password_protected_pdf()
    {
        string locked = WriteSample(TempDir(), "locked.pdf", "locked");

        var ex = Assert.Throws<InputError>(() =>
        {
            using PdfDocument doc = PdfConverter.Open(locked);
        });
        Assert.Contains("password", ex.Message);

        using PdfDocument ok = PdfConverter.Open(locked, "secret");
        Assert.Equal(1, ok.PageCount);
    }

    /// <summary><c>test_a_failing_page_does_not_end_the_document</c>.</summary>
    /// <remarks>
    /// The reference makes page 2 fail by handing the converter a <c>StubOCR</c> that raises on an
    /// all black page. The port cannot swap the recognizer — <see cref="OrtOcr"/> owns its sessions
    /// — but every page goes through <see cref="SafePage.Read"/>, which is the wrapper the test is
    /// about, so that is driven directly: the failing page becomes an error page and the pages
    /// around it still convert.
    /// </remarks>
    [Fact]
    public void A_failing_page_does_not_end_the_document()
    {
        var sources = new List<string>();
        OcrPage? failed = null;
        foreach (int i in Enumerable.Range(0, 3))
        {
            OcrPage page = SafePage.Read(() => i == 1
                ? throw new InvalidOperationException("stub failure")
                : new OcrPage([]));
            sources.Add(page.Source);
            if (i == 1)
            {
                failed = page;
            }
        }

        Assert.Equal(new[] { "ocr", "error", "ocr" }, sources.ToArray());
        Assert.NotNull(failed);
        Assert.Empty(failed.Lines);
        Assert.Equal("", failed.Markdown);
        Assert.Contains("stub failure", failed.Note);
        Assert.Equal(0.0, failed.Timings["total_ms"]);
    }

    /// <summary><c>test_huge_pages_render_below_the_pixel_cap</c>.</summary>
    /// <remarks>
    /// The reference makes a 14400x14400 pt poster and a 612x792 letter page with pypdfium2. The
    /// port has no PDF writer, so the poster is the same arithmetic on the same call and the real
    /// page is the fixture's sample (a small page: it is under the cap, so its scale is the plain
    /// dpi/72). The 612x792 case is pinned by <c>Page_scale_matches_the_reference</c>.
    /// </remarks>
    [Fact]
    public void Huge_pages_render_below_the_pixel_cap()
    {
        double big = PdfConverter.PageScale(14400, 14400, 200.0);
        Assert.True(big < 200.0 / 72.0, "a poster must be scaled below the plain dpi/72");
        Assert.True(Math.Pow(14400 * big, 2) <= PdfConverter.MaxPagePixels * 1.01, $"scale {big}");

        using PdfDocument doc = PdfConverter.Open(WriteSample(TempDir(), "page.pdf", "text"));
        using PdfPage page = doc.LoadPage(0);
        double scale = PdfConverter.PageScale(page.Width, page.Height, 200.0);
        Assert.Equal(200.0 / 72.0, scale, 10);
        Assert.True(Math.Pow(page.Width * scale, 2) <= PdfConverter.MaxPagePixels);
    }

    /// <summary><c>test_parallel_plan_has_one_unit_per_page</c>.</summary>
    /// <remarks>
    /// The reference's <c>five.pdf</c> is made with pypdfium2; the fixture's sample has two pages,
    /// so the <c>max_pages=3</c> case is two units instead of three (and a second call shows the cap
    /// actually capping). The image unit's <c>extra</c> is the page cap and a PDF unit's is the
    /// password, exactly as <c>parallel._plan</c> builds them.
    /// </remarks>
    [Fact]
    public void Parallel_plan_has_one_unit_per_page()
    {
        string dir = TempDir();
        string pdf = WriteSample(dir, "five.pdf", "text");
        string png = WritePng(Path.Combine(dir, "scan.png"), 40, 50, 255);
        string bad = Path.Combine(dir, "bad.pdf");
        File.WriteAllBytes(bad, "%PDF-1.4 broken"u8.ToArray());

        List<object> plan =
            Indra.Ocr.Cli.Cli.Plan([pdf, Path.Combine(dir, "missing.pdf"), bad, png], 3, null);

        var pages = Assert.IsType<List<WorkUnit>>(plan[0]);
        Assert.Equal(new[] { 0, 1 }, pages.Select(u => u.PageIndex!.Value).ToArray());
        Assert.All(pages, u => Assert.Equal(pdf, u.Path));
        Assert.IsType<InputError>(plan[1]);
        Assert.IsType<InputError>(plan[2]);

        var images = Assert.IsType<List<WorkUnit>>(plan[3]);
        Assert.Single(images);
        Assert.Equal(png, images[0].Path);
        Assert.Null(images[0].PageIndex);
        Assert.Equal(3, images[0].Extra);

        // The cap itself: max_pages=1 leaves one unit of the two pages.
        Assert.Single(Assert.IsType<List<WorkUnit>>(Indra.Ocr.Cli.Cli.Plan([pdf], 1, null)[0]));

        Assert.True(CliParser.UsableCores() >= 1);
    }

    /// <summary><c>test_model_errors_are_one_clear_message</c> (the local half).</summary>
    /// <remarks>
    /// The reference monkeypatches <c>snapshot_download</c>; the port has no injection seam, so the
    /// offline branch is a real download against an unreachable <c>HF_ENDPOINT</c> — which is the
    /// same variable <c>huggingface_hub</c> reads. The other half of the reference's test (one line
    /// on stderr, exit 1) is the command line's output and belongs to <c>cli_parity.py</c>.
    /// </remarks>
    [Fact]
    public void Model_errors_are_one_clear_message()
    {
        var incomplete = Assert.Throws<ModelError>(() => ModelFolder.Resolve(TempDir(), math: false));
        Assert.Contains("lacks det.onnx", incomplete.Message);

        using var env = new Env();
        using var dead = new CountingPort();
        env.Clear("INDRA_OCR_MODEL_DIR");
        env.Set("HF_HUB_CACHE", TempDir("no-cache"));
        env.Set("HF_ENDPOINT", dead.Endpoint);

        var offline = Assert.Throws<ModelError>(() => ModelFolder.Resolve(null, math: false));
        Assert.Contains("could not download the models", offline.Message);
        Assert.True(dead.Connections >= 1, "the Hub was never contacted");
    }

    /// <summary><c>test_cached_models_are_checked_once_a_day</c>.</summary>
    /// <remarks>
    /// The reference monkeypatches <c>snapshot_download</c> and counts calls; the port is counted
    /// from the other side — <c>HF_ENDPOINT</c> points at a socket that accepts connections and
    /// closes them at once, so the connection count is exactly "was the Hub checked?". Every
    /// download here fails, which is the point: the cached set is what a run keeps.
    /// </remarks>
    [Fact]
    public void Cached_models_are_checked_once_a_day()
    {
        string cache = TempDir();
        string snapshot = Path.Combine(cache, "models--datalab-to--indra-ocr", "snapshots",
                                       ModelDownload.HfRevision);
        Directory.CreateDirectory(snapshot);
        foreach (string file in ModelDownload.Files)
        {
            File.WriteAllBytes(Path.Combine(snapshot, file), [1]);
        }

        string stamp = Path.Combine(
            cache, $".indra_ocr_checked--datalab-to--indra-ocr--{ModelDownload.HfRevision}");

        using var env = new Env();
        using var hub = new CountingPort();
        env.Clear("INDRA_OCR_MODEL_DIR");
        env.Set("HF_HUB_CACHE", cache);
        env.Set("HF_ENDPOINT", hub.Endpoint);

        // No stamp yet: the Hub is checked -- and offline, the cached set is what comes back.
        Assert.Equal(snapshot, ModelFolder.Resolve(null, math: false));
        Assert.True(hub.Connections >= 1, "the Hub was never checked");
        Assert.False(File.Exists(stamp), "a failed check must not stamp the cache");

        // A download under a day ago left the stamp: the Hub is not touched at all.
        File.WriteAllText(stamp, string.Empty);
        int checkedOnce = hub.Connections;
        Assert.Equal(snapshot, ModelFolder.Resolve(null, math: false));
        Assert.Equal(checkedOnce, hub.Connections);

        // A day later it checks again -- and offline it still keeps the cached set.
        File.SetLastWriteTimeUtc(stamp, DateTime.UtcNow - TimeSpan.FromHours(25));
        Assert.Equal(snapshot, ModelFolder.Resolve(null, math: false));
        Assert.True(hub.Connections > checkedOnce, "the day-old stamp did not trigger a check");

        // Nothing cached and no Hub: the download fails with the one clear message.
        env.Set("HF_HUB_CACHE", TempDir("empty-cache"));
        var ex = Assert.Throws<ModelError>(() => ModelFolder.Resolve(null, math: false));
        Assert.Contains("could not download the models", ex.Message);
    }

    // ---------------------------------------------------------------- the port's own seams

    /// <summary>
    /// <c>document.convert</c>'s dispatch without a recognizer: the file must exist, the bytes pick
    /// the reader, and the reader's <see cref="InputError"/> is the one that comes out.
    /// </summary>
    private static void Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new InputError($"{path}: no such file");
        }

        if (OrtOcr.IsPdf(path))
        {
            using PdfDocument doc = PdfConverter.Open(path);
            return;
        }

        ImageFiles.ReadImages(path);
    }

    /// <summary>A per-test folder under the temp directory, wiped first.</summary>
    private static string TempDir([CallerMemberName] string name = "")
    {
        string dir = Path.Combine(Path.GetTempPath(), "indra-ocr-reference-inputs", name);
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover from an interrupted run; the files written below replace it anyway.
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes one of the fixture's sample PDFs out and returns its path.</summary>
    private static string WriteSample(string dir, string name, string sample)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        string blob = Fx.GetProperty("pdfs").GetProperty(sample).GetString()!;
        File.WriteAllBytes(path, Convert.FromBase64String(blob));
        return path;
    }

    /// <summary>A single-frame grayscale PNG (the reference writes one with <c>cv2.imwrite</c>).</summary>
    private static string WritePng(string path, int width, int height, byte value)
    {
        using var image = Frame(width, height, value);
        image.SaveAsPng(path);
        return path;
    }

    /// <summary>A multi-frame grayscale TIFF (the reference's <c>Image.new("L", (60, 80))</c>).</summary>
    private static string WriteTiff(string path, params byte[] values)
    {
        using var image = Frame(60, 80, values[0]);
        for (int i = 1; i < values.Length; i++)
        {
            using var frame = Frame(60, 80, values[i]);
            image.Frames.AddFrame(frame.Frames.RootFrame);   // AddFrame clones the frame
        }

        image.SaveAsTiff(path);
        return path;
    }

    private static Image<L8> Frame(int width, int height, byte value)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, value);
        return Image.LoadPixelData<L8>(pixels, width, height);
    }

    /// <summary>
    /// The environment variables a test writes, put back when it is done. The test classes are
    /// serialized (<c>[Collection("pdfium")]</c>) because of this.
    /// </summary>
    private sealed class Env : IDisposable
    {
        private readonly List<(string Name, string? Value)> _saved = [];

        public void Set(string name, string value)
        {
            Save(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Clear(string name)
        {
            Save(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        public void Dispose()
        {
            foreach ((string name, string? value) in _saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        private void Save(string name)
        {
            if (!_saved.Exists(s => s.Name == name))
            {
                _saved.Add((name, Environment.GetEnvironmentVariable(name)));
            }
        }
    }

    /// <summary>
    /// A dead Hub that still counts: it listens on a loopback port, accepts a connection and closes
    /// it at once, so a request fails (as it would with no network) and the attempt is visible.
    /// </summary>
    private sealed class CountingPort : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _connections;

        public CountingPort()
        {
            _listener.Start();
            _ = Task.Run(Accept);
        }

        public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public int Connections => Volatile.Read(ref _connections);

        public void Dispose() => _listener.Stop();

        private async Task Accept()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;   // the listener was stopped
                }

                Interlocked.Increment(ref _connections);
                client.Dispose();
            }
        }
    }
}
