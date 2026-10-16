using System.Globalization;

namespace Indra.Ocr.Py;

/// <summary>
/// Python's <c>int()</c> and <c>float()</c> as argparse's <c>type=</c> uses them: what the command
/// line accepts (and what it rejects, with which wording) is part of the reference's surface.
/// </summary>
/// <remarks>
/// The differences that matter are the ones argparse reports: <c>--max-pages 3.0</c> is an error
/// (Python's <c>int("3.0")</c> raises) and so is <c>--dpi abc</c>. Underscores between digits are
/// accepted (<c>int("1_0")</c> is 10) and so are <c>inf</c>/<c>nan</c> for a float. Python's ints
/// are unbounded, so a value past <see cref="long"/> is clamped: for the two int options the
/// clamped value picks the same pages or the same pool size as the huge one does.
/// </remarks>
public static class PyNumber
{
    /// <summary><c>int(s)</c>, clamped to <see cref="long"/>.</summary>
    public static long ParseInt(string value)
    {
        string text = value.Trim();
        int i = 0;
        bool negative = false;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }

        if (i >= text.Length)
        {
            throw new FormatException($"invalid literal for int() with base 10: {text}");
        }

        long result = 0;
        bool any = false;
        bool underscore = false;
        bool overflow = false;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '_')
            {
                if (!any || underscore || i + 1 >= text.Length)
                {
                    throw new FormatException($"invalid literal for int() with base 10: {text}");
                }

                underscore = true;
                continue;
            }

            if (c is < '0' or > '9')
            {
                throw new FormatException($"invalid literal for int() with base 10: {text}");
            }

            any = true;
            underscore = false;
            if (!overflow)
            {
                result = (result * 10) + (c - '0');
                if (result < 0)
                {
                    overflow = true; // past long.MaxValue: clamp at the end
                }
            }
        }

        if (!any || underscore)
        {
            throw new FormatException($"invalid literal for int() with base 10: {text}");
        }

        if (overflow)
        {
            return negative ? long.MinValue : long.MaxValue;
        }

        return negative ? -result : result;
    }

    /// <summary><c>float(s)</c>.</summary>
    public static double ParseFloat(string value)
    {
        string text = value.Trim();
        if (text.Length == 0)
        {
            throw new FormatException("could not convert string to float");
        }

        string sign = "";
        string body = text;
        if (body[0] is '+' or '-')
        {
            sign = body[..1];
            body = body[1..];
        }

        switch (body.ToLowerInvariant())
        {
            case "inf":
            case "infinity":
                return sign == "-" ? double.NegativeInfinity : double.PositiveInfinity;
            case "nan":
                return double.NaN;
        }

        // Python allows underscores between digits.
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '_' && (i == 0 || i + 1 >= body.Length ||
                                   !char.IsAsciiDigit(body[i - 1]) || !char.IsAsciiDigit(body[i + 1])))
            {
                throw new FormatException($"could not convert string to float: {value}");
            }
        }

        string cleaned = body.Replace("_", "");
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture,
                             out double result))
        {
            throw new FormatException($"could not convert string to float: {value}");
        }

        return sign == "-" ? -result : result;
    }
}
