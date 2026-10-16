namespace Indra.Ocr.Cli.PythonInterop;

/// <summary>
/// Python's text-mode streams: a <c>\n</c> written to one becomes <c>os.linesep</c>, so on Windows
/// the reference's stdout, stderr and <c>Path.write_text</c> files all hold CRLF. The port writes
/// the same bytes.
/// </summary>
public static class PythonText
{
    /// <summary><c>text</c> as Python's text-mode write would put it on the stream.</summary>
    public static string Translate(string text)
        => Environment.NewLine == "\n" ? text : text.Replace("\n", Environment.NewLine);
}
