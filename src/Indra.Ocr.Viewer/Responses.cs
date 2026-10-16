using System.Text.Json.Serialization;

namespace Indra.Ocr.Viewer;

/// <summary><c>POST /upload</c>'s answer: the id the page requests use, and the page count.</summary>
public sealed record UploadResponse(string Id, int Pages);

/// <summary><c>GET /info</c>'s answer: whether Tesseract is on offer, the version, the defaults.</summary>
public sealed record InfoResponse(bool Tesseract, string Version, ViewerDefaults Defaults);

/// <summary>What the handler answers with when a request fails: <c>{"error": "…"}</c>.</summary>
public sealed record ErrorResponse(string Error);

/// <summary>
/// One line of the response: the box scaled to the image the browser got, its text and its
/// confidence. The same shape serves the CLI-shaped <c>json</c> block, where the box is left in
/// page pixels.
/// </summary>
public sealed record ResponseLine(IReadOnlyList<double> Box, string Text, double Conf);

/// <summary>One layout block of the response: its label, its scaled box and its text.</summary>
public sealed record ResponseBlock(string Label, IReadOnlyList<double> Box, string Text);

/// <summary>One layout block as <c>page.blocks</c> holds it (<c>bbox</c>, not <c>box</c>).</summary>
public sealed record JsonBlock(string Label, IReadOnlyList<double> Bbox, string Text);

/// <summary>
/// The page as <c>--format json</c> writes it (minus the file name): page pixels, the raw
/// Markdown (<c>null</c> when the order model produced none, and written as <c>null</c>) and the
/// unrounded timings.
/// </summary>
public sealed record JsonPage(
    int Page,
    string Source,
    int Rotation,
    double Skew,
    string Note,
    IReadOnlyList<ResponseLine> Lines,
    string? Markdown,
    IReadOnlyList<JsonBlock> Blocks,
    IReadOnlyDictionary<string, double> Timings);

/// <summary>
/// One page of a document as the browser gets it: the JPEG preview, the lines and blocks over it,
/// the Markdown, where the text came from, and the CLI-shaped <c>json</c>.
/// </summary>
/// <remarks>
/// <c>tesseract</c> is the one key the reference adds conditionally
/// (<c>if opts.get("tesseract") and self.tesseract</c>), so it is the one key left out when it is
/// absent. The properties are declared in the reference's insertion order.
/// </remarks>
public sealed record PageResponse(
    int Page,
    int W,
    int H,
    string Img,
    IReadOnlyList<ResponseLine> Lines,
    IReadOnlyList<ResponseBlock> Blocks,
    string Markdown,
    string Source,
    string Note,
    int Rotation,
    double Skew,
    JsonPage Json,
    IReadOnlyDictionary<string, double> Timings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tesseract);
