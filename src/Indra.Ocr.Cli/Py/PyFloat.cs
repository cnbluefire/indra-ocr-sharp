using System.Globalization;

namespace Indra.Ocr.Cli.Py;

/// <summary>
/// Python's <c>repr()</c> of a float — what <c>json.dumps</c> writes for a number and what an
/// f-string with no format spec writes — and <c>format(x, '.Nf')</c>, which the reference uses for
/// the timings.
/// </summary>
/// <remarks>
/// Python and .NET both print the shortest decimal that round-trips, so the digits agree; the
/// notation does not. Python keeps a fixed form while the decimal exponent is in <c>[-4, 16)</c>
/// and always leaves a <c>.0</c> on a whole number, where .NET switches to scientific notation far
/// earlier (<c>1.234567890123456E+15</c>). So the digits come from .NET and the layout from
/// Python. <c>format(x, '.0f')</c> rounds half to even, as Python does; .NET's own "F" format
/// rounds halves away from zero.
/// </remarks>
public static class PyFloat
{
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
}
