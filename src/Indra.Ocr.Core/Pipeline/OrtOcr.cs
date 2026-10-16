using System.Diagnostics;
using Indra.Ocr.Det;
using Indra.Ocr.Imaging;
using Indra.Ocr.Layout;
using Indra.Ocr.Maths;
using Indra.Ocr.Onnx;
using Indra.Ocr.Order;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;
using Microsoft.ML.OnnxRuntime;
// The step classes are named after the methods the reference reads from self (detect,
// orientation, reading_order), so inside this class those names are the methods; the aliases
// keep the types reachable.
using Detector = Indra.Ocr.Det.Detect;
using Orient = Indra.Ocr.Pipeline.Orientation;

namespace Indra.Ocr.Pipeline;

/// <summary>
/// Port of <c>pipeline.OrtOCR</c>: the whole page pipeline over a model folder — turn the page
/// upright, detect its visual lines, recognize them, and assemble the reading order, the layout
/// blocks and the markdown.
/// </summary>
/// <remarks>
/// The steps themselves live in the classes the earlier steps built (<see cref="Detect"/>,
/// <see cref="Recognizer"/>, <see cref="ReadingOrder"/>, <see cref="Assembler"/>,
/// <see cref="Orientation"/>); this class owns the model folder and the flags the reference reads
/// from <c>self</c>, and sequences them exactly as <c>__call__</c> does.
/// <para>
/// <c>math=True</c> loads <see cref="MathReader"/> from the same folder (the reference's
/// <c>MathReader.load</c>) and hands it to the recognizer; the reference raises
/// <c>FileNotFoundError</c> when the folder has no math models, which is what the port throws
/// too.
/// </para>
/// </remarks>
public sealed class OrtOcr : IDisposable
{
    private readonly SessionOptions _options;
    private readonly OcrModels _models;
    private readonly OnnxDetModel _det;
    private readonly OnnxOrderModel? _order;
    private readonly OnnxOrientModel? _orient;
    private readonly Recognizer _recognizer;
    private readonly Assembler _assembler;
    private readonly MathReader? _math;
    private readonly int _threads;
    private bool _disposed;

    /// <param name="onnxDirectory">The model folder (<c>det.onnx</c>, <c>rec.onnx</c>,
    /// <c>meta.json</c>, and optionally <c>mask</c>/<c>order</c>/<c>orient</c>/<c>script</c> +
    /// <c>rec_cjk</c>).</param>
    /// <param name="threads">Intra-op threads (<c>inter_op_num_threads</c> stays 1).</param>
    /// <param name="recInt8"><c>rec.int8.onnx</c> instead of <c>rec.onnx</c>; <c>null</c> picks it
    /// when the folder has it.</param>
    /// <param name="adaptive">Run the second detection pass when the lines are small.</param>
    /// <param name="tableHeads">Build tables from the order model's row/column/cell heads.</param>
    /// <param name="providers">Execution providers; <c>null</c> means CPU.</param>
    /// <param name="furniture">Keep page headers and footers in the markdown.</param>
    /// <param name="math">Read math lines as LaTeX; needs the math models in the folder.</param>
    public OrtOcr(
        string onnxDirectory,
        int threads = 4,
        bool? recInt8 = null,
        bool adaptive = true,
        bool tableHeads = false,
        IReadOnlyList<string>? providers = null,
        bool furniture = false,
        bool math = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(onnxDirectory);

        // The same number the reference gives OpenCV (cv2.setNumThreads) is the deskew search's.
        _threads = threads;

        // One SessionOptions for every session in the object — the OCR ones and the math ones —
        // exactly as the reference builds one instance and hands it to each InferenceSession.
        _options = OnnxSessions.CreateOptions(threads, providers);
        try
        {
            _models = OcrModels.Load(onnxDirectory, _options, recInt8);
        }
        catch
        {
            _options.Dispose();
            throw;
        }

        MathReader? mathReader = null;
        try
        {
            _det = new OnnxDetModel(_models.Det);
            _order = _models.Order is null ? null : new OnnxOrderModel(_models.Order);
            _orient = _models.Orient is null ? null : new OnnxOrientModel(_models.Orient);
            mathReader = math ? MathModels.Load(onnxDirectory, _options) : null;
            if (math && mathReader is null)
                throw new FileNotFoundException($"math=True needs the math models in {onnxDirectory}");
            _math = mathReader;
            _recognizer = new Recognizer(
                new OnnxRecModel(_models.Rec),
                _models.Vocab,
                _models.Mask is null ? null : new OnnxMaskModel(_models.Mask),
                _models.Script is null ? null : new OnnxScriptModel(_models.Script),
                _models.RecCjk is null ? null : new OnnxRecModel(_models.RecCjk),
                _models.VocabCjk,
                _models.RecHeight,
                math: mathReader);
            _assembler = new Assembler(_order, tableHeads, _models.Vocab, furniture);
        }
        catch
        {
            mathReader?.Dispose();
            _models.Dispose();
            _options.Dispose();
            throw;
        }

        Directory = onnxDirectory;
        Adaptive = adaptive;
        TableHeads = tableHeads;
        Furniture = furniture;
    }

    /// <summary>The model folder the sessions were loaded from.</summary>
    public string Directory { get; }

    /// <summary><c>det_long_side</c> from <c>meta.json</c>.</summary>
    public int LongSide => _models.DetLongSide;

    /// <summary><c>rec_height</c> from <c>meta.json</c>.</summary>
    public int Height => _models.RecHeight;

    /// <summary>Whether the adaptive second detection pass is on.</summary>
    public bool Adaptive { get; }

    /// <summary>Whether tables come from the order model's heads instead of geometry.</summary>
    public bool TableHeads { get; }

    /// <summary>Whether headers and footers are kept in the markdown.</summary>
    public bool Furniture { get; }

    /// <summary>The line recognizer's vocabulary.</summary>
    public Vocab Vocab => _models.Vocab;

    /// <summary>The CJK recognizer's vocabulary, or <c>null</c> without the CJK models.</summary>
    public Vocab? VocabCjk => _models.VocabCjk;

    /// <summary>Whether the CJK recognizer is in use.</summary>
    public bool HasCjk => _models.HasCjk;

    /// <summary>
    /// <c>self.math</c>: the math reader, or <c>null</c> when <c>math</c> is off — the reference
    /// tests it with <c>getattr(self.ocr, "math", None) is not None</c>.
    /// </summary>
    public MathReader? Math => _math;

    // ------------------------------------------------------------------ the steps

    /// <summary>
    /// <c>is_pdf</c>: the file's own bytes say PDF (the header may follow a little junk).
    /// </summary>
    /// <remarks>
    /// The reference's <c>is_pdf</c> is a module function of <c>pipeline.py</c>; it lives here
    /// because this class is that module's other half. A file that cannot be read at all throws
    /// (the caller checks it exists first).
    /// </remarks>
    public static bool IsPdf(string path)
    {
        using FileStream stream = File.OpenRead(path);
        var head = new byte[1024];
        int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head.AsSpan(0, n).IndexOf("%PDF-"u8) >= 0;
    }

    /// <summary><c>detect</c>: the page's visual lines, <c>[N, 5]</c> (<c>x0 y0 x1 y1 score</c>)
    /// in page pixels.</summary>
    public float[] Detect(GrayImage gray) => Detector.Run(_det, gray, LongSide, Adaptive);

    /// <summary><c>recognize</c>: one line per box, in the boxes' order.</summary>
    public List<RecognizedLine> Recognize(GrayImage gray, ReadOnlySpan<float> boxes)
        => _recognizer.Run(gray, boxes);

    /// <summary><c>hanzi_lines</c>: which of the lines hold Chinese characters.</summary>
    public bool[] HanziLines(GrayImage gray, ReadOnlySpan<float> boxes)
        => _recognizer.HanziLines(gray, boxes);

    /// <summary><c>orientation</c>: the quarter turns to undo (0 without <c>orient.onnx</c>).</summary>
    public int Orientation(GrayImage gray) => _orient is null ? 0 : Orient.Turns(_orient, gray);

    /// <summary>
    /// <c>upright</c>: the page turned upright (orientation model) and straightened (skew search).
    /// </summary>
    /// <returns>
    /// The upright page — the caller's own view when the page did not change (the reference
    /// returns the same array object then) — the quarter turns undone, and the degrees of skew
    /// undone. A changed page is backed by the buffer the deskew/rotation produced, which the
    /// result keeps alive.
    /// </returns>
    public UprightPage Upright(GrayImage gray, bool turn = true)
    {
        byte[]? page = Orient.Upright(turn ? _orient : null, gray,
                                      out int width, out int height, out int turns, out double skew,
                                      _threads);
        return new UprightPage(page is null ? gray : new GrayImage(width, height, page), turns, skew);
    }

    /// <summary><c>assemble</c>: lines split at wide gaps, reading order, markdown.</summary>
    /// <remarks>
    /// <paramref name="boxes"/> is <c>[n, 4]</c>: the reference passes the detector's <c>[N, 5]</c>
    /// array on and only ever reads its first four columns, and the C# assembler takes the four
    /// (<see cref="Assembler.Run"/>). <see cref="Quads"/> does the conversion.
    /// </remarks>
    public OcrPage Assemble(double pageWidth, double pageHeight, ReadOnlySpan<float> boxes, int n,
                            IReadOnlyList<RecognizedLine> rec,
                            IDictionary<string, double>? timings = null, bool contentOrder = false)
        => _assembler.Run(pageWidth, pageHeight, boxes, n, rec, timings, contentOrder);

    /// <summary>
    /// The detector's <c>[N, 5]</c> boxes (<c>x0 y0 x1 y1 score</c>) as the <c>[n, 4]</c> the
    /// assembler reads.
    /// </summary>
    public static float[] Quads(ReadOnlySpan<float> boxes)
    {
        int n = boxes.Length / 5;
        var quads = new float[n * 4];
        for (int i = 0; i < n; i++)
            boxes.Slice(i * 5, 4).CopyTo(quads.AsSpan(i * 4));
        return quads;
    }

    /// <summary><c>_decode</c>: the text of a run of text-layer tokens.</summary>
    public string Decode(IReadOnlyList<RecToken> toks) => _assembler.Decode(toks);

    /// <summary><c>reading_order</c>.</summary>
    public ReadingOrderResult ReadingOrder(ReadOnlySpan<float> boxes, int n,
                                           IReadOnlyList<string> texts,
                                           double pageWidth, double pageHeight)
        => Indra.Ocr.Order.ReadingOrder.Run(_order, boxes, n, texts, pageWidth, pageHeight,
                                            TableHeads);

    /// <summary><c>gutter_cuts</c>.</summary>
    public List<double[]> GutterCuts(ReadOnlySpan<float> boxes, int n,
                                     IReadOnlyList<RecognizedLine> rec)
        => _assembler.GutterCuts(boxes, n, rec);

    /// <summary><c>split_line</c>.</summary>
    public IReadOnlyList<LineSegment> SplitLine(ReadOnlySpan<float> box,
                                                IReadOnlyList<RecToken> toks, double gap,
                                                IReadOnlyList<double>? at = null)
        => _assembler.SplitLine(box, toks, gap, at);

    /// <summary><c>_is_space</c>.</summary>
    public bool IsSpace(RecToken tok) => _assembler.IsSpace(tok);

    // ------------------------------------------------------------------ the whole page

    /// <summary>
    /// <c>__call__</c>: OCR of one page image. <paramref name="turn"/> = <c>false</c> means the
    /// page is known to be upright (<c>orient.onnx</c> is not asked).
    /// </summary>
    public OcrPage Call(GrayImage gray, bool turn = true)
    {
        var timings = new Dictionary<string, double>();
        var clock = Stopwatch.StartNew();
        UprightPage up = Upright(gray, turn);
        timings["orient_ms"] = clock.Elapsed.TotalMilliseconds;
        clock.Restart();

        GrayImage upright = up.Page;
        float[] boxes = Detect(upright);
        timings["det_ms"] = clock.Elapsed.TotalMilliseconds;
        clock.Restart();

        List<RecognizedLine> rec = Recognize(upright, boxes);
        timings["rec_ms"] = clock.Elapsed.TotalMilliseconds;

        OcrPage assembled = Assemble(upright.Width, upright.Height, Quads(boxes), boxes.Length / 5,
                                     rec, timings);

        // Boxes back on the page as turned, before the deskew.
        IReadOnlyList<OcrLine> lines = assembled.Lines;
        IReadOnlyList<LayoutBlock> blocks = assembled.Blocks;
        if (up.Skew != 0)
        {
            Span<double> box = stackalloc double[4];

            var unrotatedLines = new List<OcrLine>(lines.Count);
            foreach (OcrLine line in lines)
            {
                Orient.UnrotateBox(line.Box.ToArray(), up.Skew, upright.Width, upright.Height, box);
                unrotatedLines.Add(new OcrLine(box.ToArray(), line.Text, line.Confidence));
            }

            var unrotatedBlocks = new List<LayoutBlock>(blocks.Count);
            foreach (LayoutBlock block in blocks)
            {
                Orient.UnrotateBox(block.Bbox, up.Skew, upright.Width, upright.Height, box);
                unrotatedBlocks.Add(new LayoutBlock(block.Label, box.ToArray(), block.Text));
            }

            lines = unrotatedLines;
            blocks = unrotatedBlocks;
        }

        return new OcrPage(lines, assembled.Timings, assembled.Markdown, assembled.Source,
                           assembled.Note, blocks, 90 * up.Turns,
                           Numerics.RoundHalfEven(up.Skew, 2));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _math?.Dispose();
        _models.Dispose();
        _options.Dispose();
    }
}

/// <summary>
/// What <see cref="OrtOcr.Upright"/> produced: the page as it should be read, the quarter turns
/// undone and the degrees of skew undone.
/// </summary>
/// <remarks>
/// The reference returns the tuple <c>(gray, k, skew)</c>, where <c>gray</c> is the very array it
/// was given when nothing changed. A tuple cannot carry a <see cref="GrayImage"/>
/// (<c>ref struct</c>s are not valid type arguments), hence this type; <see cref="Page"/> is the
/// caller's own view in the unchanged case and a memory-backed image over the new buffer
/// otherwise.
/// </remarks>
public readonly ref struct UprightPage
{
    public UprightPage(GrayImage page, int turns, double skew)
    {
        Page = page;
        Turns = turns;
        Skew = skew;
    }

    /// <summary>The page to detect and recognize: upright and straightened.</summary>
    public GrayImage Page { get; }

    /// <summary>Quarter turns undone (<c>Page.rotation</c> is <c>90 * Turns</c>).</summary>
    public int Turns { get; }

    /// <summary>Degrees of skew undone (the boxes are mapped back through it).</summary>
    public double Skew { get; }
}
