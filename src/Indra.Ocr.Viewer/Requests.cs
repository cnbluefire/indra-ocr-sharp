namespace Indra.Ocr.Viewer;

/// <summary>
/// The options a run starts with — the reference's <c>defaults</c> dict, which the page's own
/// controls override per request and which <c>GET /info</c> reports back so the page can restore
/// them.
/// </summary>
public sealed record ViewerDefaults(
    string Mode,
    bool PdfOrder,
    bool ImageOcr,
    bool Math,
    bool Furniture,
    double Dpi,
    int MaxPages);

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
    bool ModelOrder = false,
    bool? ImageOcr = null,
    bool Tesseract = false);

/// <summary>The body of <c>POST /page</c>: which upload, which page, and the options.</summary>
public sealed record PageRequest(string? Id, int N, PageOptions? Opts);
