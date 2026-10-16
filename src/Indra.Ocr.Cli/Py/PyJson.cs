using System.Globalization;
using System.Text;

namespace Indra.Ocr.Cli.Py;

/// <summary>
/// Python's <c>json.dumps</c> with <c>ensure_ascii=False</c> — the spelling of strings and numbers
/// the reference's <c>--format json</c> output has.
/// </summary>
/// <remarks>
/// The CLI's JSON goes through this rather than <c>System.Text.Json</c> because the output is a
/// parity target: Python leaves non-ASCII characters as they are, escapes only the control
/// characters (with <c>\b \f \n \r \t</c> spelled out) and writes floats with
/// <c>float.__repr__</c>. The shapes are built by hand, so no serializer can disagree.
/// </remarks>
public static class PyJson
{
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

        return PyFloat.Repr(value);
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
