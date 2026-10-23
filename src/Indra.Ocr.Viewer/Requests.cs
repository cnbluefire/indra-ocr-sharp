using System.Text.Json.Serialization;

namespace Indra.Ocr.Viewer;

/// <summary>
/// The options a run starts with — the reference's <c>defaults</c> dict, which the page's own
/// controls override per request and which <c>GET /info</c> reports back so the page can restore
/// them.
/// </summary>
/// <remarks>
/// Three keys are snake_case in the reference (<c>pdf_order</c>, <c>image_ocr</c>,
/// <c>max_pages</c>), so they carry an explicit name; the rest follow the camelCase policy the
/// JSON context sets.
/// </remarks>
public sealed record ViewerDefaults(
    string Mode,
    [property: JsonPropertyName("image_ocr")] bool ImageOcr,
    bool Math,
    bool Furniture,
    double Dpi,
    [property: JsonPropertyName("max_pages")] int MaxPages);

/// <summary>
/// The options of one page request — the reference's <c>opts</c> dict. Every field is nullable so
/// the defaults are applied where the reference applies them (<c>opts.get(...)</c>): an absent
/// <c>dpi</c>, and a <c>0</c>, both mean 200.
/// </summary>
public sealed record PageOptions(
    string? Mode = null,
    bool Math = false,
    bool Furniture = false,
    double? Dpi = null,
    [property: JsonPropertyName("image_ocr")] bool? ImageOcr = null,
    bool Tesseract = false);

/// <summary>
/// The body of <c>POST /page</c>: which upload, which page, and the options. <c>id</c> and
/// <c>n</c> are required in the reference (<c>q["id"]</c>, <c>int(q["n"])</c>), so they are
/// nullable here and the handler reports the missing one the way Python's <c>KeyError</c> reads.
/// </summary>
public sealed record PageRequest(string? Id, int? N, PageOptions? Opts);
