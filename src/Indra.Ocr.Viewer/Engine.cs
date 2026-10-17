using System.Diagnostics;
using Indra.Ocr.ArgumentParsing;
using Indra.Ocr.Imaging;
using Indra.Ocr.Images;
using Indra.Ocr.Layout;
using Indra.Ocr.Onnx;
using Indra.Ocr.Pdf;
using Indra.Ocr.Pipeline;

namespace Indra.Ocr.Viewer;

/// <summary>
/// The port of <c>viewer.Engine</c>: holds uploaded documents and reads one page per request, with
/// the options the page sends, so the browser shows each page as it finishes.
/// </summary>
/// <remarks>
/// One recognizer per (math, furniture) pair, built on first use and warmed up so that the first
/// request is not the one that pays for the allocations. Every page — the PDFium render, the
/// pipeline and the preview — runs under one lock: PDFium is not thread-safe, and the reference
/// serialises the same way.
/// <para>
/// The preview is the page at the converter's own scale, turned upright, shrunk so its longest
/// side is <see cref="MaxSide"/>, and JPEG-encoded; the boxes are scaled by the same factor, so
/// they land on the picture. The pixels themselves cannot match the reference's byte for byte —
/// Python encodes with OpenCV's libjpeg, the port with ImageSharp — so the JPEG is the one part of
/// the response that is compared by eye, not by bytes.
/// </para>
/// </remarks>
public sealed class Engine : IDisposable
{
    /// <summary>The longest side of the page image sent to the browser.</summary>
    public const int MaxSide = 1600;

    /// <summary>The timing keys the response always carries (zero-filled when a step did not run).</summary>
    public static readonly string[] TimingKeys =
        ["text_ms", "det_ms", "rec_ms", "ocr_ms", "math_ms", "order_ms", "total_ms"];

    /// <summary>The modes a page request may ask for.</summary>
    public static readonly string[] Modes = ["auto", "ocr", "text"];

    /// <summary>How many uploads are kept (the rest are dropped, oldest first).</summary>
    private const int MaxUploads = 5;

    private readonly string? _modelPath;
    private readonly int _threads;
    private readonly object _lock = new();
    private readonly Dictionary<(bool Math, bool Furniture), OrtOcr> _ocrs = [];
    private readonly OrderedDictionary<string, byte[]> _docs = [];
    private readonly string _temp;
    private bool _disposed;

    /// <param name="modelPath">The <c>--model-dir</c> value, or <c>null</c> to download.</param>
    /// <param name="threads">Threads per recognizer (<c>--threads</c>).</param>
    /// <param name="tesseract">Whether <c>--tesseract</c> was given.</param>
    /// <param name="defaults">The options the page starts with (<c>GET /info</c> reports them).</param>
    /// <exception cref="ViewerError">The model folder is unusable, or tesseract was asked for.</exception>
    public Engine(string? modelPath, int threads, bool tesseract, ViewerDefaults defaults)
    {
        _modelPath = modelPath;
        _threads = threads;
        Defaults = defaults;
        _temp = Path.Combine(Path.GetTempPath(), "indra_ocr_viewer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        if (tesseract)
        {
            // The reference imports pytesseract here — a Python package that drives the tesseract
            // binary. The port has no equivalent, so the option is refused rather than quietly
            // ignored (the reference fails at startup here too, for want of the package).
            throw new ViewerError("tesseract is not supported by this port");
        }

        // Eagerly, like the reference: the models are resolved (and downloaded) before the server
        // starts, so a bad --model-dir is a startup error rather than a broken first request.
        _ = Ocr(defaults.Math, defaults.Furniture);
    }

    /// <summary>The options a page starts with.</summary>
    public ViewerDefaults Defaults { get; }

    /// <summary>The recognizer for these options; the math models download on first use.</summary>
    private OrtOcr Ocr(bool math, bool furniture)
    {
        var key = (math, furniture);
        if (!_ocrs.TryGetValue(key, out OrtOcr? ocr))
        {
            // A first run downloads the models here, and says so on stderr as it goes.
            string onnx = ModelFolder.Resolve(_modelPath, math,
                                              ConsoleOutput.DownloadProgressCallback());
            ocr = new OrtOcr(onnx, threads: _threads, recInt8: false, math: math, furniture: furniture);
            var warmUp = new byte[200 * 200];
            Array.Fill(warmUp, (byte)255);
            _ = ocr.Call(new GrayImage(200, 200, warmUp)); // warm-up: first-call allocation is not timed
            _ocrs[key] = ocr;
        }

        return ocr;
    }

    /// <summary>Registers an upload and returns its id and page count.</summary>
    public UploadResponse Upload(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        bool pdf = StartsWithPdf(bytes);
        string id = Guid.NewGuid().ToString("N")[..12];
        int pages;
        if (pdf)
        {
            string path = Path.Combine(_temp, id + ".pdf");
            File.WriteAllBytes(path, bytes);
            using PdfDocument doc = PdfDocument.Open(path);
            pages = doc.PageCount;
        }
        else
        {
            try
            {
                _ = ImageFiles.ReadImage(bytes); // raises on an unreadable image
            }
            catch (InputError exc)
            {
                throw new ViewerError(exc.Message);
            }

            pages = 1;
        }

        _docs[id] = bytes;
        while (_docs.Count > MaxUploads) // keep the last few uploads only
        {
            string old = _docs.GetAt(0).Key;
            _docs.RemoveAt(0);
            try
            {
                File.Delete(Path.Combine(_temp, old + ".pdf"));
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                // The reference's `except OSError`: an image has no temp file, and a file still
                // held open is not worth failing the upload over.
            }
        }

        return new UploadResponse(id, pages);
    }

    /// <summary>Reads one page of an upload with the options the page sent.</summary>
    /// <exception cref="ViewerError">The upload is unknown, or the mode is not one of the three.</exception>
    public PageResponse Page(string id, int n, PageOptions opts)
    {
        if (!_docs.TryGetValue(id, out byte[]? data))
        {
            throw new ViewerError($"'{id}'"); // the text of Python's KeyError
        }

        string mode = opts.Mode ?? "auto";
        if (!Modes.Contains(mode))
        {
            throw new ViewerError($"mode must be one of {ModeRepr}");
        }

        bool math = opts.Math;
        double dpi = opts.Dpi is double given && given != 0.0 ? given : 200.0;
        bool pdf = StartsWithPdf(data);
        OcrPage page;
        GrayImage gray;
        double renderMs;
        Dictionary<string, double> timings;
        List<ResponseLine> lines;
        List<ResponseBlock> blocks;
        JsonPage json;
        int width;
        int height;
        byte[] jpeg;
        lock (_lock)
        {
            OrtOcr ocr = Ocr(math, opts.Furniture);
            var conv = new PdfConverter(ocr, dpi: dpi, mode: mode, pdfOrder: !opts.ModelOrder,
                                        imageOcr: opts.ImageOcr ?? true);
            if (pdf)
            {
                string path = Path.Combine(_temp, id + ".pdf");
                using PdfDocument doc = PdfDocument.Open(path);
                using PdfPage rendered = doc.LoadPage(n);
                var clock = Stopwatch.StartNew();
                // the converter's own scale, so the boxes land on the picture
                gray = rendered.Render(conv.PageScale(rendered));
                renderMs = clock.Elapsed.TotalMilliseconds;
                page = conv.ConvertPage(doc, n);
            }
            else
            {
                GrayImageData decoded = ImageFiles.ReadImage(data);
                gray = decoded;
                renderMs = 0.0;
                page = ocr.Call(gray);
            }

            if (page.Rotation != 0) // the boxes are in the upright page: show it
            {
                int turns = -(page.Rotation / 90);
                int turnedWidth = turns % 2 == 0 ? gray.Width : gray.Height;
                int turnedHeight = turns % 2 == 0 ? gray.Height : gray.Width;
                var turned = new byte[turnedWidth * turnedHeight];
                Orientation.Rotate90(gray, turned, turns);
                gray = new GrayImage(turnedWidth, turnedHeight, turned);
            }

            int fullWidth = gray.Width;
            int fullHeight = gray.Height;
            double s = Math.Min(1.0, (double)MaxSide / Math.Max(fullHeight, fullWidth));
            if (s < 1)
            {
                int smallWidth = CvMath.Round(fullWidth * s);
                int smallHeight = CvMath.Round(fullHeight * s);
                var small = new byte[smallWidth * smallHeight];
                CvResize.Resize8u(gray, small, smallWidth, smallHeight, Interpolation.Area);
                gray = new GrayImage(smallWidth, smallHeight, small);
            }

            width = gray.Width;
            height = gray.Height;
            jpeg = ImageFiles.EncodeJpeg(gray, 82);

            // render_ms first, then every timing key (zero when the step did not run), then what
            // the pipeline filled in — the reference's dict order, which the JSON keeps.
            timings = new Dictionary<string, double>
            {
                ["render_ms"] = Numerics.RoundHalfEven(renderMs, 1),
            };
            foreach (string key in TimingKeys)
            {
                timings[key] = 0.0;
            }

            foreach (KeyValuePair<string, double> kv in page.Timings)
            {
                timings[kv.Key] = Numerics.RoundHalfEven(kv.Value, 1);
            }

            double[] Scaled(IReadOnlyList<double> box)
            {
                var scaled = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    scaled[i] = Numerics.RoundHalfEven(box[i] * s, 1);
                }

                return scaled;
            }

            lines = new List<ResponseLine>(page.Lines.Count);
            var jsonLines = new List<ResponseLine>(page.Lines.Count);
            foreach (OcrLine line in page.Lines)
            {
                lines.Add(new ResponseLine(Scaled(line.Box), line.Text, line.Confidence));
                jsonLines.Add(new ResponseLine(line.Box, line.Text, line.Confidence));
            }

            blocks = new List<ResponseBlock>(page.Blocks.Count);
            var jsonBlocks = new List<JsonBlock>(page.Blocks.Count);
            foreach (LayoutBlock block in page.Blocks)
            {
                blocks.Add(new ResponseBlock(block.Label, Scaled(block.Bbox), block.Text));
                jsonBlocks.Add(new JsonBlock(block.Label, block.Bbox, block.Text));
            }

            json = new JsonPage(n, page.Source, page.Rotation, page.Skew, page.Note, jsonLines,
                                page.Markdown, jsonBlocks, page.Timings);
        }

        timings.TryGetValue("total_ms", out double total);
        ConsoleOutput.Write(
            $"page {n + 1}: {page.Source}, {page.Lines.Count} lines, {page.Blocks.Count} blocks, " +
            $"{ConsoleOutput.Fixed(total, 0)} ms ({mode}{(math ? ", math" : string.Empty)})\n");

        return new PageResponse(
            Page: n + 1,
            W: width,
            H: height,
            Img: "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
            Lines: lines,
            Blocks: blocks,
            Markdown: page.Markdown ?? page.Text,
            Source: page.Source,
            Note: page.Note,
            Rotation: page.Rotation,
            Skew: page.Skew,
            Json: json,
            Timings: timings,
            Tesseract: null);
    }

    /// <summary>Python's <c>data[:5] == b"%PDF-"</c>: the upload says what it is.</summary>
    private static bool StartsWithPdf(ReadOnlySpan<byte> data)
        => data.Length >= 5 && data[..5].SequenceEqual("%PDF-"u8);

    /// <summary><c>str(MODES)</c>: the tuple Python puts in the error message.</summary>
    private static string ModeRepr
        => "(" + string.Join(", ", Modes.Select(m => $"'{m}'")) + ")";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_lock)
        {
            foreach (OrtOcr ocr in _ocrs.Values)
            {
                ocr.Dispose();
            }

            _ocrs.Clear();
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing shutdown over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
