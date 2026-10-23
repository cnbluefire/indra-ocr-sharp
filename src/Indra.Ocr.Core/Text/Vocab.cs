using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Indra.Ocr.Text;

/// <summary>
/// Token vocabulary for the line recognizer, ported from <c>indra_ocr/vocab.py</c>.
/// </summary>
/// <remarks>
/// Class 0 is the CTC blank and class <c>i + 1</c> is <see cref="Characters"/>[i]. A token is one
/// character or one LaTeX command (<c>\frac</c>, <c>\alpha</c>) inside math.
/// <para>
/// The OCR pipeline never builds a vocabulary in code: it reads
/// <c>meta.json["vocab"]</c> / <c>["vocab_cjk"]</c> through <see cref="FromJson"/>, so those
/// strings are the authoritative token order. <see cref="Latin"/>/<see cref="Multi"/> reproduce
/// the training-side builders and are checked against the reference in the tests.
/// </para>
/// </remarks>
public sealed class Vocab
{
    private readonly string[] _chars;
    private readonly Dictionary<string, int> _index;
    private readonly bool _jamo;

    public Vocab(IEnumerable<string> chars)
    {
        _chars = chars as string[] ?? chars.ToArray();
        _index = new Dictionary<string, int>(_chars.Length, StringComparer.Ordinal);
        for (int i = 0; i < _chars.Length; i++)
            _index[_chars[i]] = i + 1;
        HasCommands = Array.Exists(_chars, c => UnicodeText.CodePointCount(c) > 1);
        _jamo = _index.ContainsKey("ᄀ");
    }

    /// <summary>The tokens, in class order: token <c>i</c> is class <c>i + 1</c>.</summary>
    public string[] Characters => _chars;

    /// <summary>Token -> class id (1-based).</summary>
    public IReadOnlyDictionary<string, int> Index => _index;

    /// <summary>Whether the vocabulary contains multi-character (LaTeX) tokens.</summary>
    public bool HasCommands { get; }

    /// <summary>Number of classes the recognizer emits, including the blank.</summary>
    public int ClassCount => _chars.Length + 1;

    public static Vocab Latin() => new(BuildLatinChars());

    public static Vocab Multi() => new(BuildMultiTokens());

    public static Vocab Named(string name) => name switch
    {
        "latin" => Latin(),
        "multi" => Multi(),
        _ => throw new ArgumentException($"unknown vocab {name}"),
    };

    /// <summary>Parses the <c>{"blank": 0, "chars": [...]}</c> JSON stored in <c>meta.json</c>.</summary>
    public static Vocab FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("blank", out var blank) && blank.GetInt32() != 0)
            throw new ArgumentException("only blank=0 vocabularies are supported");
        var chars = new List<string>();
        foreach (var e in root.GetProperty("chars").EnumerateArray())
            chars.Add(e.GetString()!);
        return new Vocab(chars);
    }

    /// <summary>Serializes back to the exact <c>meta.json</c> form (<c>ensure_ascii=False</c>).</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"blank\": 0, \"chars\": [");
        for (int i = 0; i < _chars.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            AppendJsonString(sb, _chars[i]);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>NFC plus the recognizer's look-alike canonicalization.</summary>
    public string Normalize(string text) => UnicodeText.Canonical(text);

    /// <summary>
    /// Splits text into tokens: characters, except that a LaTeX command is one token when the
    /// vocabulary has it. Inside <c>$...$</c> math spans spaces are dropped (invisible in
    /// rendered math); <see cref="Decode"/> puts back the one space LaTeX needs.
    /// </summary>
    public IReadOnlyList<string> Tokenize(string text)
    {
        if (!HasCommands)
            return UnicodeText.CodePoints(text).ToList();

        var outList = new List<string>();
        int pos = 0;
        foreach (var (start, end) in MathSpans(text))
        {
            AddTokens(outList, text[pos..start], math: false);
            AddTokens(outList, text[start..end], math: true);
            pos = end;
        }
        AddTokens(outList, text[pos..], math: false);
        return outList;
    }

    /// <summary>Token ids for <paramref name="text"/>, or <c>null</c> if it holds an unknown token.</summary>
    public int[]? Encode(string text)
    {
        var tokens = Tokenize(Normalize(text));
        if (tokens.Count == 0)
            return null;
        var ids = new int[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!_index.TryGetValue(tokens[i], out ids[i]))
                return null;
        }
        return ids;
    }

    /// <summary>The tokens of <paramref name="text"/> that the vocabulary does not know.</summary>
    public HashSet<string> Oov(string text)
    {
        var oov = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Tokenize(Normalize(text)))
            if (!_index.ContainsKey(t))
                oov.Add(t);
        return oov;
    }

    /// <summary>Decodes class ids back to text, inserting the spaces LaTeX commands need.</summary>
    public string Decode(ReadOnlySpan<int> ids)
    {
        var toks = new List<string>(ids.Length);
        foreach (int id in ids)
            if (id > 0 && id <= _chars.Length)
                toks.Add(_chars[id - 1]);

        if (!HasCommands)
        {
            string text = string.Concat(toks);
            return _jamo ? Nfc.Normalize(text) : text;
        }

        var sb = new StringBuilder();
        for (int k = 0; k < toks.Count; k++)
        {
            string t = toks[k];
            sb.Append(t);
            // "\alpha" + "x" must not read back as "\alphax"
            string next = k + 1 < toks.Count ? toks[k + 1] : "";
            if (UnicodeText.CodePointCount(t) > 2 && t[0] == '\\' && UnicodeText.IsAlpha(t[1..]) &&
                next.Length > 0 && UnicodeText.IsAlpha(NextCodePoint(next)))
                sb.Append(' ');
        }
        return sb.ToString();
    }

    private static string NextCodePoint(string s) =>
        char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1]) ? s[..2] : s[..1];

    private void AddTokens(List<string> outList, string text, bool math)
    {
        foreach (var t in TokenRegexTokens(text))
        {
            if (math && UnicodeText.IsSpace(t))
                continue;
            if (_index.ContainsKey(t) || UnicodeText.CodePointCount(t) == 1)
                outList.Add(t);
            else
                outList.AddRange(UnicodeText.CodePoints(t));
        }
    }

    // ------------------------------------------------------------------ the Python regexes

    /// <summary><c>\$[^$]*[\\^_{][^$]*\$</c>: a <c>$...$</c> pair whose body holds LaTeX.</summary>
    private static IEnumerable<(int Start, int End)> MathSpans(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] != '$')
            {
                i++;
                continue;
            }
            int close = s.IndexOf('$', i + 1);
            if (close < 0)
                break;
            bool latex = false;
            for (int k = i + 1; k < close; k++)
            {
                if (s[k] is '\\' or '^' or '_' or '{')
                {
                    latex = true;
                    break;
                }
            }
            if (latex)
            {
                yield return (i, close + 1);
                i = close + 1;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary><c>\\[A-Za-z]+|\\[{}|,;! ]|.</c> with <c>re.S</c>, over code points.</summary>
    private static IEnumerable<string> TokenRegexTokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\')
            {
                int j = i + 1;
                if (j < s.Length && IsAsciiLetter(s[j]))
                {
                    while (j < s.Length && IsAsciiLetter(s[j]))
                        j++;
                    yield return s[i..j];
                    i = j;
                }
                else if (j < s.Length && "{}|,;! ".IndexOf(s[j]) >= 0)
                {
                    yield return s[i..(j + 1)];
                    i = j + 1;
                }
                else
                {
                    yield return "\\";
                    i++;
                }
            }
            else
            {
                int len = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                yield return s.Substring(i, len);
                i += len;
            }
        }
    }

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    // ------------------------------------------------------------------ vocabulary builders

    /// <summary>ASCII, Latin-1, Latin Extended-A/B/Additional, basic Greek and common symbols.</summary>
    public static string[] BuildLatinChars()
    {
        var chars = new List<string>();
        AddRange(chars, 0x20, 0x7E);    // ASCII printable, with space
        AddRange(chars, 0x00A1, 0x00FF); // Latin-1 Supplement
        AddRange(chars, 0x0100, 0x017F); // Latin Extended-A
        AddRange(chars, 0x0180, 0x024F); // Latin Extended-B (Ș ș Ț ț, Vietnamese, pinyin)
        AddRange(chars, 0x1E00, 0x1EFF); // Latin Extended Additional
        AddRange(chars, 0x0391, 0x03A9); // basic Greek
        AddRange(chars, 0x03B1, 0x03C9);
        chars.AddRange(Tokens("\u2010\u2011\u2012\u2013\u2014\u2015\u2016\u2018\u2019\u201a\u201b\u201c\u201d\u201e\u201f\u2020\u2021\u2022\u2023\u2026\u2030\u2032\u2033\u2039\u203a\u2044\u203b"));
        chars.AddRange(Tokens("\u2070\u2074\u2075\u2076\u2077\u2078\u2079\u207a\u207b\u207d\u207e\u207f\u2080\u2081\u2082\u2083\u2084\u2085\u2086\u2087\u2088\u2089"));
        chars.AddRange(Tokens("\u20ac\u20b9\u20bd\u20a9\u20ba\u20aa\u20b1\u20ab\u20b4\u20a6\u20a1\u20b8"));
        chars.AddRange(Tokens("\u2122\u2103\u2109\u2116\u2113\u212e\u2153\u2154\u215b"));
        chars.AddRange(Tokens("\u2190\u2191\u2192\u2193\u2194\u21d2\u21d4\u21b5"));
        chars.AddRange(Tokens("\u2212\u2213\u2217\u2218\u2219\u221a\u221e\u221d\u2220\u2227\u2228\u2229\u222a\u222b\u2248\u2260\u2261\u2264\u2265\u2282\u2283\u2286\u2287\u2295\u2297\u22a5\u22c5\u2202\u2206\u2207\u2208\u2209\u220b\u220f\u2211\u2200\u2203\u2205"));
        chars.AddRange(Tokens("\u25a0\u25a1\u25aa\u25ab\u25b2\u25b3\u25b6\u25ba\u25bc\u25bd\u25c0\u25c4\u25c6\u25c7\u25cb\u25e6\u25cf\u2605\u2606\u2713\u2714\u2717\u2718\u2756\u27a2\u27a4"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var c in chars)
        {
            if (!Printable(c) || c == "\u00ad") // soft hyphen is invisible
                continue;
            if (seen.Add(c))
                result.Add(c);
        }
        return result.ToArray();
    }

    /// <summary>Latin plus Cyrillic, full Greek, Devanagari, Bengali and the LaTeX commands.</summary>
    public static string[] BuildMultiTokens()
    {
        var result = new List<string>(BuildLatinChars());
        var seen = new HashSet<string>(result, StringComparer.Ordinal);
        var extra = new List<string>();
        extra.AddRange(ScriptChars(0x0370, 0x03FF)); // Greek and Coptic (accented Greek)
        extra.AddRange(ScriptChars(0x0400, 0x045F)); // Cyrillic (Russian, Ukrainian, Serbian)
        extra.AddRange(ScriptChars(0x0490, 0x04FF)); // Cyrillic extended letters
        extra.AddRange(ScriptChars(0x0900, 0x097F)); // Devanagari (Hindi, Marathi, Nepali)
        extra.AddRange(ScriptChars(0x0980, 0x09FF)); // Bengali
        extra.AddRange(Tokens("\u0964\u0965\u20b9")); // ।॥₹
        extra.AddRange(LatexTokens);
        foreach (var t in extra)
            if (seen.Add(t))
                result.Add(t);
        return result.ToArray();
    }

    private static void AddRange(List<string> chars, int a, int b)
    {
        for (int c = a; c <= b; c++)
            chars.Add(((char)c).ToString());
    }

    private static IEnumerable<string> Tokens(string s) => UnicodeText.CodePoints(s);

    /// <summary>Control, format, unassigned, private-use and separators (except space) drop out.</summary>
    private static bool Printable(string c)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(c, 0);
        if (IsControlOrSeparator(cat) && c != " ")
            return false;
        if (cat == UnicodeCategory.NonSpacingMark)
            return false; // labels are NFC, so marks come precomposed
        return true;
    }

    /// <summary>Assigned letters, marks and digits of a script block (marks kept).</summary>
    private static IEnumerable<string> ScriptChars(int a, int b)
    {
        for (int cp = a; cp <= b; cp++)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory((char)cp);
            if (cat == UnicodeCategory.OtherNotAssigned || IsControlOrSeparator(cat))
                continue;
            yield return ((char)cp).ToString();
        }
    }

    private static bool IsControlOrSeparator(UnicodeCategory c) => c is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or
        UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or
        UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator;

    private static readonly string[] LatexCommands = (
        "alpha beta gamma delta epsilon varepsilon zeta eta theta vartheta iota kappa lambda mu nu xi " +
        "pi varpi rho varrho sigma varsigma tau upsilon phi varphi chi psi omega " +
        "Gamma Delta Theta Lambda Xi Pi Sigma Upsilon Phi Psi Omega " +
        "frac dfrac tfrac sqrt sum prod int iint oint lim limsup liminf sup inf max min arg det " +
        "sin cos tan cot sec csc arcsin arccos arctan sinh cosh tanh log ln exp " +
        "left right big Big bigg Bigg langle rangle lfloor rfloor lceil rceil " +
        "cdot cdots ldots dots vdots ddots times div pm mp ast star circ bullet " +
        "leq geq neq le ge ne ll gg approx sim simeq cong equiv propto " +
        "in notin ni subset supset subseteq supseteq cup cap cap setminus emptyset varnothing " +
        "forall exists neg land lor wedge vee oplus otimes " +
        "to rightarrow leftarrow Rightarrow Leftarrow leftrightarrow Leftrightarrow mapsto " +
        "implies iff uparrow downarrow longrightarrow " +
        "infty partial nabla hbar ell prime perp parallel mid top angle " +
        "hat bar tilde vec dot ddot widehat widetilde overline underline " +
        "mathbb mathcal mathbf mathrm mathit mathsf mathfrak boldsymbol operatorname text " +
        "quad qquad "
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static readonly string[] LatexTokens = BuildLatexTokens();

    private static string[] BuildLatexTokens()
    {
        var tokens = new List<string>(LatexCommands.Length + 7);
        foreach (var c in LatexCommands)
            tokens.Add("\\" + c);
        tokens.AddRange(new[] { "\\{", "\\}", "\\|", "\\,", "\\;", "\\!", "\\ " });
        return tokens.ToArray();
    }

    /// <summary>Python's <c>json.dumps(..., ensure_ascii=False)</c> string escaping.</summary>
    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
