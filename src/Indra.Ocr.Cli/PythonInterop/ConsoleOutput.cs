using System.Text;

namespace Indra.Ocr.Cli.PythonInterop;

/// <summary>
/// The hosts' two streams, written the way Python's are.
/// </summary>
/// <remarks>
/// Python's console streams are Unicode and its redirected ones take the locale encoding (GBK on a
/// Chinese Windows, which cannot hold every character OCR produces); the port writes UTF-8 in both
/// cases, so the same text always comes out. The console is asked for its UTF-8 code page when
/// there is one, so those bytes render as written instead of as mojibake.
/// <para>
/// It lives in the CLI because the viewer references the CLI (<c>indra_ocr.viewer</c> imports the
/// same pieces from <c>indra_ocr.cli</c>), and both hosts write their console the same way.
/// </para>
/// </remarks>
public static class ConsoleOutput
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextWriter Out =
        Create(Console.OpenStandardOutput(), !Console.IsOutputRedirected);

    private static readonly TextWriter Err =
        Create(Console.OpenStandardError(), !Console.IsErrorRedirected);

    /// <summary>Write to stdout (Python's <c>sys.stdout.write</c>).</summary>
    public static void Write(string text) => Out.Write(PythonText.Translate(text));

    /// <summary>Write to stderr (Python's <c>print(..., file=sys.stderr)</c>).</summary>
    public static void WriteError(string text) => Err.Write(PythonText.Translate(text));

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
