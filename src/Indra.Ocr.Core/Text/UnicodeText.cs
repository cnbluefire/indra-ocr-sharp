using System.Globalization;
using System.Text;

namespace Indra.Ocr.Text;

/// <summary>
/// The Unicode-dependent helpers the recognizer's vocabulary needs, ported from
/// <c>indra_ocr/vocab.py</c>.
/// </summary>
/// <remarks>
/// Python reaches these through <c>unicodedata</c>, .NET through
/// <see cref="CharUnicodeInfo"/>. Both ship the same Unicode character database, but not
/// necessarily the same <i>version</i> of it (Python 3.14 here reports
/// <c>unidata_version 16.0.0</c>); the vocabulary fixtures pin the lists down and
/// <c>docs/050-vocab.md</c> records the check.
/// </remarks>
public static class UnicodeText
{
    /// <summary>Number of Unicode code points in a string (Python's <c>len</c>).</summary>
    public static int CodePointCount(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
            n++;
        }
        return n;
    }

    /// <summary>The code points of a string, one <c>char</c>-pair per astral character.</summary>
    public static IEnumerable<string> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return s.Substring(i, 2);
                i++;
            }
            else
            {
                yield return s[i].ToString();
            }
        }
    }

    /// <summary>
    /// The code point values of a string. An unpaired surrogate yields its own value, which is
    /// what Python's <c>ord</c> does for it (<see cref="CodePoints"/> cannot: a lone surrogate
    /// has no valid <c>char.ConvertToUtf32</c>).
    /// </summary>
    public static IEnumerable<int> CodePointValues(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else
            {
                yield return s[i];
            }
        }
    }

    /// <summary>Python's <c>str.isalpha()</c>: non-empty and every code point a letter.</summary>
    public static bool IsAlpha(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (var c in CodePoints(s))
            if (!char.IsLetter(c, 0))
                return false;
        return true;
    }

    /// <summary>
    /// Python's <c>str.isdigit()</c> for one code point. Python's "digit" is the Unicode
    /// <c>Numeric_Type=Digit</c> property: every decimal digit (<c>Nd</c>) plus a short list of
    /// "other" digits (superscripts, circled and parenthesised numbers, ...), which
    /// <see cref="CharUnicodeInfo"/> reports as <c>No</c>. The port adds exactly those extra
    /// code points (Unicode 16.0.0, what Python 3.14 reports; <c>docs/110-order-post.md</c>
    /// records the exhaustive check).
    /// </summary>
    public static bool IsDigitCodePoint(int codePoint)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF)
            return false;
        return UnicodeCompat.Category(codePoint) == UnicodeCategory.DecimalDigitNumber
            || InRanges(OtherDigits, codePoint);
    }

    /// <summary>Python's <c>str.isdigit()</c>: non-empty and every code point a digit.</summary>
    public static bool IsDigit(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (int cp in CodePointValues(s))
            if (!IsDigitCodePoint(cp))
                return false;
        return true;
    }

    /// <summary>
    /// Python's <c>str.isupper()</c> for one code point: every cased character is uppercase.
    /// That is <c>Lu</c> plus the handful of "Other_Uppercase" code points (Roman numerals,
    /// circled and squared Latin letters), which .NET reports as <c>Nl</c>/<c>So</c>.
    /// </summary>
    /// <remarks>
    /// Only the one-code-point form is needed: the pipeline's single use is
    /// <c>t[0].isupper()</c> in <c>line_features</c>. The multi-character form would have to
    /// count <c>Other_Lowercase</c> code points as cased as well (a whole further table), so it
    /// is not offered rather than offered approximately.
    /// </remarks>
    public static bool IsUpperCodePoint(int codePoint)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF)
            return false;
        return UnicodeCompat.Category(codePoint) == UnicodeCategory.UppercaseLetter
            || InRanges(OtherUppercase, codePoint);
    }

    /// <summary>
    /// Python's <c>str.islower()</c> for one code point: <c>Ll</c> plus the "Other_Lowercase"
    /// code points (modifier letters, Roman numeral lower cases, ...), which .NET reports as
    /// <c>Lm</c>/<c>Nl</c>/<c>Lo</c>. Needed by <c>join_lines</c>'s hyphen test.
    /// </summary>
    public static bool IsLowerCodePoint(int codePoint)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF)
            return false;
        return UnicodeCompat.Category(codePoint) == UnicodeCategory.LowercaseLetter
            || InRanges(OtherLowercase, codePoint);
    }

    /// <summary>Python's <c>str.isalpha()</c> for one code point (any <c>L*</c> category).</summary>
    public static bool IsAlphaCodePoint(int codePoint)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF)
            return false;
        return UnicodeCompat.Category(codePoint) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter;
    }

    /// <summary>
    /// Python's <c>str.strip()</c> with no argument: Python whitespace (not .NET's, which misses
    /// <c>0x1C</c>..<c>0x1F</c>) trimmed from both ends.
    /// </summary>
    public static string Strip(string s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && IsSpaceCodePoint(s[start]))
            start++;
        while (end > start && IsSpaceCodePoint(s[end - 1]))
            end--;
        return start == 0 && end == s.Length ? s : s[start..end];
    }

    /// <summary>
    /// Python's <c>" ".join(s.split())</c>: every run of Python whitespace becomes one space,
    /// and leading/trailing whitespace is dropped.
    /// </summary>
    public static string CollapseSpaces(string s)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && IsSpaceCodePoint(s[i]))
                i++;
            int start = i;
            while (i < s.Length && !IsSpaceCodePoint(s[i]))
                i++;
            if (i > start)
                parts.Add(s[start..i]);
        }
        return string.Join(" ", parts);
    }

    private static bool InRanges((int Lo, int Hi)[] ranges, int codePoint)
    {
        foreach (var (lo, hi) in ranges)
            if (lo <= codePoint && codePoint <= hi)
                return true;
        return false;
    }

    // Numeric_Type=Digit code points that are not Nd, and Other_Uppercase code points that are
    // not Lu (Unicode 16.0.0, the version Python 3.14 reports).
    private static readonly (int Lo, int Hi)[] OtherDigits =
    [
        (0x00B2, 0x00B3), (0x00B9, 0x00B9), (0x1369, 0x1371), (0x19DA, 0x19DA), (0x2070, 0x2070),
        (0x2074, 0x2079), (0x2080, 0x2089), (0x2460, 0x2468), (0x2474, 0x247C), (0x2488, 0x2490),
        (0x24EA, 0x24EA), (0x24F5, 0x24FD), (0x24FF, 0x24FF), (0x2776, 0x277E), (0x2780, 0x2788),
        (0x278A, 0x2792), (0x10A40, 0x10A43), (0x10E60, 0x10E68), (0x11052, 0x1105A),
        (0x1F100, 0x1F10A),
    ];

    private static readonly (int Lo, int Hi)[] OtherUppercase =
    [
        (0x2160, 0x216F), (0x24B6, 0x24CF), (0x1F130, 0x1F149), (0x1F150, 0x1F169),
        (0x1F170, 0x1F189),
    ];

    // Code points with the Unicode "Other_Lowercase" property that are not Ll (Unicode 16.0.0,
    // what Python 3.14 reports): modifier letters and the lower-case Roman numerals.
    private static readonly (int Lo, int Hi)[] OtherLowercase =
    [
        (0x00AA, 0x00AA), (0x00BA, 0x00BA), (0x02B0, 0x02B8), (0x02C0, 0x02C1), (0x02E0, 0x02E4),
        (0x0345, 0x0345), (0x037A, 0x037A), (0x10FC, 0x10FC), (0x1D2C, 0x1D6A), (0x1D78, 0x1D78),
        (0x1D9B, 0x1DBF), (0x2071, 0x2071), (0x207F, 0x207F), (0x2090, 0x209C), (0x2170, 0x217F),
        (0x24D0, 0x24E9), (0x2C7C, 0x2C7D), (0xA69C, 0xA69D), (0xA770, 0xA770), (0xA7F2, 0xA7F4),
        (0xA7F8, 0xA7F9), (0xAB5C, 0xAB5F), (0xAB69, 0xAB69), (0x10780, 0x10780),
        (0x10783, 0x10785), (0x10787, 0x107B0), (0x107B2, 0x107BA), (0x1E030, 0x1E06D),
    ];

    /// <summary>Python's <c>str.isspace()</c> for one code point.</summary>
    /// <remarks>
    /// Python's whitespace set is the Unicode separators (<c>Zs</c>/<c>Zl</c>/<c>Zp</c>) plus the
    /// C0 controls <c>\t</c>..<c>\r</c> and <c>0x1C</c>..<c>0x1F</c> and <c>0x85</c> (NEL).
    /// <see cref="char.IsWhiteSpace(char)"/> covers all of that except <c>0x1C</c>..<c>0x1F</c>,
    /// so the port adds them; the exhaustive check in <c>OrderPostTests</c> pins the set down
    /// against Python's <c>str.isspace()</c> for all 1 114 112 code points.
    /// </remarks>
    public static bool IsSpaceCodePoint(int codePoint)
    {
        if (codePoint is >= 0x1C and <= 0x1F)
            return true;
        if (codePoint < 0 || codePoint > 0x10FFFF)
            return false;
        UnicodeCategory category = UnicodeCompat.Category(codePoint);
        return category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
            || codePoint is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x85;
    }

    /// <summary>Python's <c>str.isspace()</c>: non-empty and every code point whitespace.</summary>
    public static bool IsSpace(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (int cp in CodePointValues(s))
            if (!IsSpaceCodePoint(cp))
                return false;
        return true;
    }

    /// <summary>
    /// Python's <c>unicodedata.bidirectional</c>: the bidirectional class of a code point
    /// (<c>"L"</c>, <c>"R"</c>, <c>"AL"</c>, <c>"EN"</c>, ... — the empty string when the code
    /// point is unassigned). <c>document.right_to_left</c> is the only caller.
    /// </summary>
    /// <remarks>
    /// .NET does not expose bidirectional classes, so the table is generated from the same
    /// CPython the reference runs on (<c>tools/scripts/test/gen_bidi_table.py</c>): it is that
    /// UCD version's answer, not .NET's.
    /// </remarks>
    public static string BidiClass(int codePoint)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF)
            throw new ArgumentOutOfRangeException(
                nameof(codePoint), codePoint, "Not a Unicode code point.");
        var ranges = BidiClassData.Ranges;
        int lo = 0, hi = ranges.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (ranges[mid].End >= codePoint)
                hi = mid;
            else
                lo = mid + 1;
        }
        return ranges[lo].Class;
    }

    /// <summary>
    /// Python's <c>unicodedata.bidirectional(s)</c>, which takes a one-character string.
    /// </summary>
    public static string BidiClass(string s)
    {
        int count = 0, only = 0;
        foreach (int cp in CodePointValues(s))
        {
            if (count == 0)
                only = cp;
            count++;
        }
        if (count != 1)
            throw new ArgumentException(
                "bidirectional() argument must be a single character", nameof(s));
        return BidiClass(only);
    }

    /// <summary>
    /// Canonicalizes a label: NFC, the look-alike table, collapsed doubled quotes and
    /// leader runs.
    /// </summary>
    public static string Canonical(string text)
    {
        text = Nfc.Normalize(text);
        text = TranslateCanonTable(text);
        text = ReplaceDoubleSingle(text);
        return CollapseRuns(text);
    }

    private static string TranslateCanonTable(string text)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char mapped = CanonTable(text[i]);
            if (mapped == '\0')
            {
                sb?.Append(text[i]);
                continue;
            }
            sb ??= new StringBuilder(text.Length).Append(text, 0, i);
            sb.Append(mapped);
        }
        return sb?.ToString() ?? text;
    }

    private static char CanonTable(char c) => c switch
    {
        '\u0218' => '\u015e', // Ș -> Ş
        '\u0219' => '\u015f', // ș -> ş
        '\u021a' => '\u0162', // Ț -> Ţ
        '\u021b' => '\u0163', // ț -> ţ
        '\u25aa' => '\u2022', // ▪
        '\u25cf' => '\u2022', // ●
        '\u2219' => '\u2022', // ∙
        '\u2023' => '\u2022', // ‣
        '\u2043' => '\u2022', // ⁃
        _ => '\0',
    };

    /// <summary><c>''|``</c> -> <c>"</c> (non-overlapping, leftmost).</summary>
    private static string ReplaceDoubleSingle(string text)
    {
        if (!text.Contains("''") && !text.Contains("``"))
            return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length &&
                ((text[i] == '\'' && text[i + 1] == '\'') || (text[i] == '`' && text[i + 1] == '`')))
            {
                sb.Append('"');
                i++;
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary><c>([._\-=·*~])\1{3,}</c> -> exactly three of that character.</summary>
    private static string CollapseRuns(string text)
    {
        StringBuilder? sb = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (IsRunChar(c))
            {
                int j = i;
                while (j < text.Length && text[j] == c)
                    j++;
                int run = j - i;
                if (run >= 4)
                {
                    sb ??= new StringBuilder(text.Length).Append(text, 0, i);
                    sb.Append(c, 3);
                }
                else
                {
                    sb?.Append(c, run);
                }
                i = j;
            }
            else
            {
                sb?.Append(c);
                i++;
            }
        }
        return sb?.ToString() ?? text;
    }

    private static bool IsRunChar(char c) =>
        c is '.' or '_' or '-' or '=' or '\u00b7' or '*' or '~';

    // ------------------------------------------------------------------ Indic syllable checks

    private static readonly (int Lo, int Hi)[] IndicBlocks = [(0x0900, 0x097F), (0x0980, 0x09FF)];

    private static bool IsIndic(char c)
    {
        foreach (var (lo, hi) in IndicBlocks)
            if (lo <= c && c <= hi)
                return true;
        return false;
    }

    /// <summary><c>_indicates</c> for a whole string.</summary>
    private static bool ContainsIndic(string text)
    {
        foreach (char c in text)
            if (IsIndic(c))
                return true;
        return false;
    }

    /// <summary>
    /// True unless a Devanagari or Bengali mark has no base to attach to — the signature of a
    /// broken legacy font mapping.
    /// </summary>
    public static bool IndicValid(string text)
    {
        char prev = '\0';
        foreach (char c in text)
        {
            var cat = Category(c);
            if (IsIndic(c) && cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                bool pOk = prev != '\0' && IsIndic(prev);
                var pCat = prev != '\0' ? Category(prev) : UnicodeCategory.OtherNotAssigned;
                if (Modifiers.Contains(c))
                {
                    // anusvara, candrabindu, visarga: after a letter or a vowel sign
                    if (!pOk)
                        return false;
                }
                else if (Virama.Contains(c) || Nukta.Contains(c))
                {
                    // after a consonant (or nukta)
                    if (!(pOk && (pCat == UnicodeCategory.OtherLetter || Nukta.Contains(prev))))
                        return false;
                }
                else
                {
                    // vowel sign: after a consonant, nukta, or nothing else
                    if (!(pOk && (pCat == UnicodeCategory.OtherLetter || Nukta.Contains(prev))))
                        return false;
                }
            }
            prev = c;
        }
        return true;
    }

    /// <summary>Fraction of Indic lines that may break syllable structure before a page is suspect.</summary>
    public static bool IndicPageOk(IReadOnlyList<string> texts, double maxBad = 0.05)
    {
        int ind = 0;
        int bad = 0;
        foreach (var t in texts)
        {
            if (!ContainsIndic(t))
                continue;
            ind++;
            if (!IndicValid(t))
                bad++;
        }
        if (ind == 0)
            return true;
        return bad <= maxBad * ind;
    }

    private static readonly HashSet<char> Virama = ['\u094d', '\u09cd'];
    private static readonly HashSet<char> Nukta = ['\u093c', '\u09bc'];
    private static readonly HashSet<char> Modifiers =
        ['\u0900', '\u0901', '\u0902', '\u0903', '\u0981', '\u0982', '\u0983'];

    /// <summary>Python's <c>unicodedata.category(c)</c> for a single code unit.</summary>
    public static UnicodeCategory Category(char c) => CharUnicodeInfo.GetUnicodeCategory(c);
}
