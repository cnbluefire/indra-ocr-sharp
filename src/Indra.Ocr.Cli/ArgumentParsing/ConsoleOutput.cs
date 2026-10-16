using System.Globalization;
using System.Text;
using Indra.Ocr.Onnx;

namespace Indra.Ocr.ArgumentParsing;

/// <summary>
/// Everything a host needs to write the way Python writes: the two streams, Python's text-mode
/// newline translation, Python's <c>repr()</c> of a float and Python's <c>json.dumps</c> spelling —
/// and the lines a model download reports, which are the port's own (step 310).
/// </summary>
/// <remarks>
/// Both hosts write the console the same way (the viewer references the CLI, as
/// <c>indra_ocr.viewer</c> imports from <c>indra_ocr.cli</c>), and the command line's output is a
/// parity target, so all of it lives here rather than in several one-method classes.
/// <para>
/// Python's console streams are Unicode and its redirected ones take the locale encoding (GBK on a
/// Chinese Windows, which cannot hold every character OCR produces); the port writes UTF-8 in both
/// cases, so the same text always comes out. The console is asked for its UTF-8 code page when
/// there is one, so those bytes render as written instead of as mojibake.
/// </para>
/// <para>
/// Python and .NET both print the shortest decimal that round-trips, so the digits agree; the
/// notation does not. Python keeps a fixed form while the decimal exponent is in <c>[-4, 16)</c>
/// and always leaves a <c>.0</c> on a whole number, where .NET switches to scientific notation far
/// earlier (<c>1.234567890123456E+15</c>). So the digits come from .NET and the layout from
/// Python. <c>format(x, '.0f')</c> rounds half to even, as Python does; .NET's own "F" format
/// rounds halves away from zero. The JSON goes through here rather than <c>System.Text.Json</c>
/// because it is a parity target too: Python leaves non-ASCII characters as they are, escapes only
/// the control characters (with <c>\b \f \n \r \t</c> spelled out) and writes floats with
/// <c>float.__repr__</c>. The shapes are built by hand, so no serializer can disagree.
/// </para>
/// </remarks>
public static class ConsoleOutput
{
    // ------------------------------------------------------------------ the two streams

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextWriter Out =
        Create(Console.OpenStandardOutput(), !Console.IsOutputRedirected);

    private static readonly TextWriter Err =
        Create(Console.OpenStandardError(), !Console.IsErrorRedirected);

    /// <summary>Write to stdout (Python's <c>sys.stdout.write</c>).</summary>
    public static void Write(string text) => Out.Write(Translate(text));

    /// <summary>Write to stderr (Python's <c>print(..., file=sys.stderr)</c>).</summary>
    public static void WriteError(string text) => Err.Write(Translate(text));

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

    // ------------------------------------------------------------------ the model download's progress

    /// <summary>
    /// A model download report as the lines the hosts print on stderr (step 310): the file being
    /// fetched, and where it comes from and goes, in the report that opens it; then how far it is.
    /// </summary>
    /// <remarks>
    /// The reference's <c>snapshot_download</c> draws a <c>tqdm</c> bar for each file it fetches
    /// (its description is the file name, its unit scale the byte count). This is the same
    /// information without the animation, so a redirected stream holds lines rather than a trail of
    /// carriage returns. The newlines are Python's (<c>\n</c>), as everywhere here:
    /// <see cref="WriteError"/> turns them into the platform's.
    /// </remarks>
    public static string DownloadProgress(ModelDownloadProgress p)
    {
        if (p.Downloaded == 0)
        {
            string size = p.Total > 0 ? $" ({Size(p.Total, p.Total)})" : string.Empty;
            return $"downloading {p.File}{size}\n  from {p.Url}\n  to {p.Path}\n";
        }

        if (p.Total <= 0)
        {
            return $"{p.File}: {Size(p.Downloaded, p.Downloaded)}\n";
        }

        return $"{p.File}: {Size(p.Downloaded, p.Total)} / {Size(p.Total, p.Total)} " +
               $"({Percent(p.Downloaded, p.Total)}%)\n";
    }

    /// <summary>A model download report on stderr, one report at a time.</summary>
    public static void WriteDownloadProgress(ModelDownloadProgress p)
        => WriteError(DownloadProgress(p));

    /// <summary>
    /// What a host hands <c>ModelFolder.Resolve</c> as its <c>downloadProgress</c>: the lines below
    /// — or nothing, when <c>HF_HUB_DISABLE_PROGRESS_BARS</c> asks for quiet, the variable
    /// <c>huggingface_hub</c> reads for its own bars (<c>1</c>, <c>on</c>, <c>yes</c>, <c>true</c>,
    /// in any case).
    /// </summary>
    public static Action<ModelDownloadProgress>? DownloadProgressCallback()
        => ProgressBarsDisabled() ? null : WriteDownloadProgress;

    /// <summary>The values <c>huggingface_hub.utils._runtime._is_true</c> accepts.</summary>
    private static readonly string[] TrueValues = ["1", "ON", "YES", "TRUE"];

    /// <summary><c>HF_HUB_DISABLE_PROGRESS_BARS</c>, with the reference's truthiness.</summary>
    private static bool ProgressBarsDisabled()
    {
        string? value = Environment.GetEnvironmentVariable("HF_HUB_DISABLE_PROGRESS_BARS");
        return value is not null && System.Array.IndexOf(TrueValues, value.ToUpperInvariant()) >= 0;
    }

    /// <summary>
    /// A byte count in the unit that fits it, one decimal (<c>16.4 MB</c>) — the scale a progress
    /// bar shows. <paramref name="whole"/> is the size the unit is chosen from, so every line about
    /// one file reads in the same unit.
    /// </summary>
    private static string Size(long bytes, long whole)
    {
        (double scale, string unit) = whole switch
        {
            >= 1_000_000_000 => (1e9, "GB"),
            >= 1_000_000 => (1e6, "MB"),
            >= 1_000 => (1e3, "KB"),
            _ => (1d, "B"),
        };
        return unit == "B"
            ? $"{bytes} B"
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / scale:F1} {unit}");
    }

    /// <summary>The share of a file that has arrived, in whole percent.</summary>
    private static int Percent(long downloaded, long total) => (int)(downloaded * 100 / total);

    // ------------------------------------------------------------------ Python's text streams

    /// <summary>
    /// <c>text</c> as Python's text-mode write would put it on the stream: a <c>\n</c> written to
    /// one becomes <c>os.linesep</c>, so on Windows the reference's stdout, stderr and
    /// <c>Path.write_text</c> files all hold CRLF. The port writes the same bytes.
    /// </summary>
    public static string Translate(string text)
        => Environment.NewLine == "\n" ? text : text.Replace("\n", Environment.NewLine);

    // ------------------------------------------------------------------ Python's float spelling

    /// <summary><c>repr(value)</c>.</summary>
    public static string Repr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        bool negative = double.IsNegative(value);
        if (value == 0.0)
        {
            return negative ? "-0.0" : "0.0";
        }

        string shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        int e = shortest.IndexOfAny(['E', 'e']);
        string mantissa = e < 0 ? shortest : shortest[..e];
        int exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], CultureInfo.InvariantCulture);

        int point = mantissa.IndexOf('.');
        string digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        int decpt = (point < 0 ? mantissa.Length : point) + exponent;

        // Keep the significant digits only; decpt holds the magnitude ("100" -> "1", decpt 3).
        int start = 0;
        while (start < digits.Length - 1 && digits[start] == '0')
        {
            start++;
            decpt--;
        }

        int end = digits.Length;
        while (end > start + 1 && digits[end - 1] == '0')
        {
            end--;
        }

        digits = digits[start..end];

        string body = Layout(digits, decpt);
        return negative ? "-" + body : body;
    }

    /// <summary><c>format(value, ".{decimals}f")</c> (round half to even).</summary>
    public static string Fixed(double value, int decimals)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        double rounded = decimals is >= 0 and <= 15
            ? Math.Round(value, decimals, MidpointRounding.ToEven)
            : value;
        return rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                                 CultureInfo.InvariantCulture);
    }

    /// <summary>Python's fixed-or-exponential layout for <c>0.digits × 10^decpt</c>.</summary>
    private static string Layout(string digits, int decpt)
    {
        int exponent = decpt - 1;
        if (exponent is >= -4 and < 16)
        {
            if (decpt <= 0)
            {
                return "0." + new string('0', -decpt) + digits;
            }

            if (decpt >= digits.Length)
            {
                return digits + new string('0', decpt - digits.Length) + ".0";
            }

            return digits[..decpt] + "." + digits[decpt..];
        }

        string mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        string power = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        return mantissa + "e" + (exponent < 0 ? "-" : "+") + power;
    }

    // ------------------------------------------------------------------ Python's json.dumps spelling

    /// <summary><c>json.dumps(s)</c> for a string.</summary>
    public static string Str(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary><c>json.dumps(f)</c> for a float: <c>Infinity</c> and <c>NaN</c> included.</summary>
    public static string Num(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        return Repr(value);
    }

    /// <summary><c>json.dumps(i)</c> for an integer.</summary>
    public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Null() => "null";

    /// <summary><c>json.dumps(list)</c> over already-encoded items.</summary>
    public static string Array(IEnumerable<string> encoded) => "[" + string.Join(", ", encoded) + "]";

    /// <summary><c>json.dumps(dict)</c> over already-encoded values, keys in the given order.</summary>
    public static string Object(IEnumerable<(string Key, string Value)> fields)
        => "{" + string.Join(", ", fields.Select(f => Str(f.Key) + ": " + f.Value)) + "}";
}
