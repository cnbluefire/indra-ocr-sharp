namespace Indra.Ocr.Viewer;

/// <summary>
/// <c>viewer.PAGE</c> and <c>viewer.LOGO</c>: the page the browser loads and the logo in its
/// header.
/// </summary>
/// <remarks>
/// Step 230 ports the reference's HTML; until then the route answers with a short note, so the
/// JSON API can be aligned on its own (the plan's order: API first, page after).
/// </remarks>
internal static class ViewerPage
{
    /// <summary>The page, with <c>__LOGO__</c> where the header's logo goes.</summary>
    internal static string Html =>
        "<!doctype html>\n<title>indra-ocr-viewer</title>\n<p>Step 230 ports the page.</p>\n";

    /// <summary>The header logo (a data URI), substituted for <c>__LOGO__</c>.</summary>
    internal static string Logo => "";

    /// <summary><c>PAGE.replace("__LOGO__", LOGO)</c>.</summary>
    internal static string Render() => Html.Replace("__LOGO__", Logo);
}
