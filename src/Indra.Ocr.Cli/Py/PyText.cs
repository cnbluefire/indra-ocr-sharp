using System.Text;

namespace Indra.Ocr.Cli.Py;

/// <summary>
/// Python's text-mode streams: a <c>\n</c> written to one becomes <c>os.linesep</c>, so on Windows
/// the reference's stdout, stderr and <c>Path.write_text</c> files all hold CRLF. The port writes
/// the same bytes.
/// </summary>
public static class PyText
{
    /// <summary><c>text</c> as Python's text-mode write would put it on the stream.</summary>
    public static string Translate(string text)
        => Environment.NewLine == "\n" ? text : text.Replace("\n", Environment.NewLine);
}

/// <summary>
/// The command line's two streams, written the way Python's are.
/// </summary>
/// <remarks>
/// Python's console streams are Unicode and its redirected ones take the locale encoding (GBK on a
/// Chinese Windows, which cannot hold every character OCR produces); the port writes UTF-8 in both
/// cases, so the same text always comes out. The console is asked for its UTF-8 code page when
/// there is one, so those bytes render as written instead of as mojibake.
/// </remarks>
public static class PyConsole
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextWriter Out =
        Create(Console.OpenStandardOutput(), !Console.IsOutputRedirected);

    private static readonly TextWriter Err =
        Create(Console.OpenStandardError(), !Console.IsErrorRedirected);

    /// <summary>Write to stdout (Python's <c>sys.stdout.write</c>).</summary>
    public static void Write(string text) => Out.Write(PyText.Translate(text));

    /// <summary>Write to stderr (Python's <c>print(..., file=sys.stderr)</c>).</summary>
    public static void WriteError(string text) => Err.Write(PyText.Translate(text));

    private static TextWriter Create(Stream stream, bool console)
    {
        if (console)
        {
            try
            {
                Console.OutputEncoding = Utf8;
            }
            catch (IOException)
            {
                // No console to ask after all.
            }
        }

        return new StreamWriter(stream, Utf8) { AutoFlush = true };
    }
}
