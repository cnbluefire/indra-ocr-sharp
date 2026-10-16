namespace Indra.Ocr.Tests;

/// <summary>
/// The test classes that drive PDFium (<see cref="PdfTests"/>, <see cref="TextLayerTests"/>,
/// <see cref="DocumentTests"/>) share one xUnit collection, so they run one after another.
/// </summary>
/// <remarks>
/// PDFium is not thread-safe — it is exactly why the command line spreads pages over worker
/// <em>processes</em> and not threads — while xUnit runs test classes in parallel by default.
/// Two of these classes rendering at the same time made
/// <c>PdfTests.Renders_match_pypdfium2</c> and
/// <c>DocumentTests.Convert_over_sample_pdfs_matches_the_reference</c> fail once in a while.
/// Everything else still runs in parallel; only PDFium is serialised.
/// </remarks>
[CollectionDefinition("pdfium")]
public class PdfiumCollection
{
}
