using Indra.Ocr.Viewer;

namespace Indra.Ocr.Tests;

/// <summary>
/// The page the viewer serves, against the reference's own: <c>fixtures/viewer_page.html</c> is
/// the body <c>GET /</c> gives (the logo already substituted), written by
/// <c>tools/scripts/test/gen_viewer_page.py</c>.
/// </summary>
/// <remarks>
/// The byte-for-byte comparison over the wire lives in <c>viewer_parity.py</c> (step `240`); this
/// keeps the page checkable without Python, the way the help text is.
/// </remarks>
public class ViewerPageTests
{
    private static string Fixture()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "viewer_page.html"));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void Page_matches_the_reference()
    {
        Assert.Equal(Normalize(Fixture()), Normalize(ViewerPage.Render()));
    }

    [Fact]
    public void The_page_keeps_lf_endings_and_has_the_logo_in_place()
    {
        string page = ViewerPage.Render();
        Assert.StartsWith("<!doctype html>", page);
        Assert.EndsWith("</script></body></html>", page);
        Assert.DoesNotContain("__LOGO__", page);
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo", page);
        Assert.DoesNotContain("\r", page); // CPython normalises the reference's string literals too
    }
}
