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

    /// <summary>Python's <c>str.isspace()</c> for the single-character tokens the regex emits.</summary>
    public static bool IsSpace(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (var c in CodePoints(s))
            if (!char.IsWhiteSpace(c, 0))
                return false;
        return true;
    }

    /// <summary>
    /// Canonicalizes a label: NFC, the look-alike table, collapsed doubled quotes and
    /// leader runs.
    /// </summary>
    public static string Canonical(string text)
    {
        text = NormalizeNfc(text);
        text = TranslateCanonTable(text);
        text = ReplaceDoubleSingle(text);
        return CollapseRuns(text);
    }

    private static string NormalizeNfc(string text)
    {
        if (text.IsNormalized(NormalizationForm.FormC))
            return text;
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // .NET refuses to normalize unpaired surrogates; Python passes them through.
            return text;
        }
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
