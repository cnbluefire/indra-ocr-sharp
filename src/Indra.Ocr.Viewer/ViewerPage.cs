namespace Indra.Ocr.Viewer;

/// <summary>
/// <c>viewer.PAGE</c> and <c>viewer.LOGO</c>: the page the browser loads and the Datalab mark in
/// its header.
/// </summary>
/// <remarks>
/// Both are taken from the reference module itself by
/// <c>tools/scripts/test/gen_viewer_page.py</c> and live in <see cref="ViewerPageAssets"/> — 22 kB
/// of HTML/CSS/JS and an 8 kB base64 PNG are not something to retype.
/// <para>
/// The reference serves its page exactly as the string is spelled in its own source file, and
/// CPython normalises the line endings inside string literals, so the answer always has LF
/// endings; the port keeps them (any CRLF a checkout left in the generated file is folded back
/// first). The logo goes where <c>__LOGO__</c> is, as a
/// <c>data:image/png;base64,…</c> source in the header.
/// </para>
/// </remarks>
internal static class ViewerPage
{
    /// <summary><c>viewer.PAGE.replace("__LOGO__", LOGO)</c>.</summary>
    internal static string Render() => Page.Replace("__LOGO__", ViewerPageAssets.Logo);

    /// <summary>The page on LF endings, whatever the checkout did to the generated file.</summary>
    private static string Page => ViewerPageAssets.Page.Replace("\r\n", "\n");
}
