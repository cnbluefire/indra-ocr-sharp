using Indra.Ocr.ArgumentParsing;
using Indra.Ocr.Cli;
using Indra.Ocr.Viewer;

namespace Indra.Ocr.Tests;

public class ProductSmokeTests
{
    [Fact]
    public void Product_version_matches_the_python_package()
    {
        // The reference's `__version__` (indra_ocr/__init__.py:22) is what both hosts print
        // (cli.py:72, viewer.py:609) and what the viewer's /info reports (viewer.py:262).  It
        // lives in the assembly now: Directory.Build.props stamps `Product` and `Version` on
        // every project, and the hosts read them back (ProductAttributes).
        Assert.Equal("indra-ocr 1.1.2", ProductAttributes.VersionLine(typeof(CliParser).Assembly));
        Assert.Equal("indra-ocr 1.1.2",
                     ProductAttributes.VersionLine(typeof(ViewerParser).Assembly));
        Assert.Equal("1.1.2", ProductAttributes.Version(typeof(ViewerParser).Assembly));
    }
}
