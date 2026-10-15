namespace Indra.Ocr.Tests;

public class ProductSmokeTests
{
    [Fact]
    public void Product_version_matches_the_python_package()
    {
        Assert.Equal("indra-ocr", ProductInfo.Name);
        Assert.Equal("0.3.2", ProductInfo.Version);
    }
}
