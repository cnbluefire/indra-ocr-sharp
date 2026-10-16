using System.Diagnostics;
using Indra.Ocr.Det;
using Indra.Ocr.Imaging;
using Indra.Ocr.Layout;
using Indra.Ocr.Onnx;
using Indra.Ocr.Order;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;
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
/// <c>math=True</c> is not ported yet (step 200, the last step of the plan). The reference only
/// reaches its <c>FileNotFoundError</c> when the folder has no math models, which the shipped
/// model set does not, so throwing there is faithful for every model set we have.
/// </para>
/// </remarks>
public sealed class OrtOcr : IDisposable
{
    private readonly OcrModels _models;
    private readonly OnnxDetModel _det;
    private readonly OnnxOrderModel? _order;
    private readonly OnnxOrientModel? _orient;
    private readonly Recognizer _recognizer;
    private readonly Assembler _assembler;
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
    /// <param name="math">Read math lines as LaTeX; needs the math models (not ported).</param>
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
        if (math)
            throw new FileNotFoundException($"math=True needs the math models in {onnxDirectory}");

        _models = OcrModels.Load(onnxDirectory, threads, recInt8, providers);
        try
        {
            _det = new OnnxDetModel(_models.Det);
            _order = _models.Order is null ? null : new OnnxOrderModel(_models.Order);
            _orient = _models.Orient is null ? null : new OnnxOrientModel(_models.Orient);
            _recognizer = new Recognizer(
                new OnnxRecModel(_models.Rec),
                _models.Vocab,
                _models.Mask is null ? null : new OnnxMaskModel(_models.Mask),
                _models.Script is null ? null : new OnnxScriptModel(_models.Script),
                _models.RecCjk is null ? null : new OnnxRecModel(_models.RecCjk),
                _models.VocabCjk,
                _models.RecHeight);
            _assembler = new Assembler(_order, tableHeads, _models.Vocab, furniture);
        }
        catch
        {
            _models.Dispose();
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

    // ------------------------------------------------------------------ the steps

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
    /// The upright page — or <c>null</c> when the page did not change, in which case the caller's
    /// buffer is still the page (the reference returns the same array object then) — the quarter
    /// turns undone, and the degrees of skew undone.
    /// </returns>
    public (GrayPixels? Page, int Turns, double Skew) Upright(GrayImage gray, bool turn = true)
    {
        byte[]? page = Orient.Upright(turn ? _orient : null, gray,
                                      out int width, out int height, out int turns, out double skew);
        return (page is null ? null : new GrayPixels(width, height, page), turns, skew);
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
        (GrayPixels? uprightPage, int turns, double skew) = Upright(gray, turn);
        timings["orient_ms"] = clock.Elapsed.TotalMilliseconds;
        clock.Restart();

        GrayImage upright = uprightPage is null ? gray : uprightPage.View;
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
        if (skew != 0)
        {
            Span<double> box = stackalloc double[4];

            var unrotatedLines = new List<OcrLine>(lines.Count);
            foreach (OcrLine line in lines)
            {
                Orient.UnrotateBox(line.Box.ToArray(), skew, upright.Width, upright.Height, box);
                unrotatedLines.Add(new OcrLine(box.ToArray(), line.Text, line.Confidence));
            }

            var unrotatedBlocks = new List<LayoutBlock>(blocks.Count);
            foreach (LayoutBlock block in blocks)
            {
                Orient.UnrotateBox(block.Bbox, skew, upright.Width, upright.Height, box);
                unrotatedBlocks.Add(new LayoutBlock(block.Label, box.ToArray(), block.Text));
            }

            lines = unrotatedLines;
            blocks = unrotatedBlocks;
        }

        return new OcrPage(lines, assembled.Timings, assembled.Markdown, assembled.Source,
                           assembled.Note, blocks, 90 * turns, Numerics.RoundHalfEven(skew, 2));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _models.Dispose();
    }
}
