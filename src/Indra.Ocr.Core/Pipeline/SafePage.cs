namespace Indra.Ocr.Pipeline;

/// <summary>
/// <c>document._safe</c>: one bad page must not end the document — it becomes an empty page that
/// says why. Both document converters use it, and the note is the reference's
/// <c>f"{type(e).__name__}: {e}"</c>.
/// </summary>
/// <remarks>
/// The failing exception's <b>type name</b> is part of the output. The PDF backend's type is
/// <c>PdfiumError</c>, which is what the reference's <c>pypdfium2.PdfiumError</c> prints, so the
/// names line up; any other type prints its own name (the port does not rename .NET exceptions to
/// Python's).
/// </remarks>
public static class SafePage
{
    /// <summary>Runs a page builder, turning a failure into an error page.</summary>
    public static OcrPage Read(Func<OcrPage> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) // one bad page must not end the document
        {
            return From(e);
        }
    }

    /// <summary>The empty page a failure becomes.</summary>
    public static OcrPage From(Exception e) => new(
        [],
        new Dictionary<string, double> { ["total_ms"] = 0.0 },
        markdown: "",
        source: "error",
        note: $"{e.GetType().Name}: {e.Message}");
}
