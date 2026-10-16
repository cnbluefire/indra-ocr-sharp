using System.Text;
using System.Text.Json;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Unicode Normalization Form C against CPython's own answers
/// (<c>tools/scripts/test/gen_nfc_table.py</c>).
/// </summary>
/// <remarks>
/// The reference normalizes a label with <c>unicodedata.normalize("NFC", text)</c>
/// (<c>indra_ocr/vocab.py</c>), and .NET cannot answer that for it: with invariant
/// globalization — which the AOT builds set, so the native binary needs no ICU —
/// <c>string.Normalize</c> is a no-op. <see cref="Nfc"/> implements UAX #15 over tables
/// generated from CPython's Unicode 16.0.0, and this fixture is the whole check: every
/// canonical decomposition, every primary composite, every Hangul syllable, random combining
/// sequences, random code points and the edges (empty, a lone mark, blocked compositions,
/// unpaired surrogates).
/// </remarks>
public class NfcTests
{
    [Fact]
    public void Nfc_matches_the_reference()
    {
        var root = Fixtures.Load("nfc.json");
        int cases = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            Check(cases, c[0].GetString()!, c[1].GetString()!);
            cases++;
        }
        // Lone surrogates cannot travel as JSON strings (.NET refuses to read them back), so
        // the generator stores those cases as code points.
        foreach (var c in root.GetProperty("surrogates").EnumerateArray())
        {
            Check(cases, FromCodePoints(c[0]), FromCodePoints(c[1]));
            cases++;
        }
        Assert.True(cases > 20000, $"only {cases} cases");
    }

    private static void Check(int index, string input, string expected)
    {
        string actual = Nfc.Normalize(input);
        Assert.True(expected == actual,
            $"case {index}: {CodePoints(input)} -> {CodePoints(actual)}, expected {CodePoints(expected)}");
    }

    private static string CodePoints(string text) =>
        text.Length == 0 ? "(empty)" : string.Join(" ", text.Select(ch => $"U+{(int)ch:X4}"));

    /// <summary>
    /// Builds the text of a case that carries a lone surrogate, which is stored as code points
    /// rather than as a string.
    /// </summary>
    private static string FromCodePoints(JsonElement element)
    {
        var sb = new StringBuilder();
        foreach (var value in element.EnumerateArray())
        {
            int cp = value.GetInt32();
            if (cp is >= 0xD800 and <= 0xDFFF) sb.Append((char)cp);  // a lone surrogate
            else sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    /// <summary>NFC is idempotent, and its own answers are already normalized.</summary>
    [Fact]
    public void Nfc_is_idempotent()
    {
        var root = Fixtures.Load("nfc.json");
        int cases = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            string expected = c[1].GetString()!;
            Assert.True(expected == Nfc.Normalize(expected),
                $"case {cases}: {CodePoints(expected)}");
            cases++;
        }
        foreach (var c in root.GetProperty("surrogates").EnumerateArray())
        {
            string expected = FromCodePoints(c[1]);
            Assert.True(expected == Nfc.Normalize(expected),
                $"case {cases}: {CodePoints(expected)}");
            cases++;
        }
    }

    /// <summary>The label path composes too: this is the case that invariant globalization broke.</summary>
    [Fact]
    public void Canonical_composes_combining_marks()
    {
        Assert.Equal("\u00e9", UnicodeText.Canonical("e\u0301"));
        Assert.Equal("\u00e9", UnicodeText.Canonical("\u0065\u0301"));
        Assert.Equal("\u1e14", UnicodeText.Canonical("E\u0304\u0300"));
        Assert.Equal("\uac01", UnicodeText.Canonical("\u1100\u1161\u11a8"));
        // The cedilla is ordered before the acute (class 202 < 230) and blocks nothing: the
        // acute still composes with the A, and the cedilla stays where it is.
        Assert.Equal("\u00c1\u0327", UnicodeText.Canonical("A\u0301\u0327"));
        Assert.Equal("\u00c1\u0327", UnicodeText.Canonical("A\u0327\u0301"));
    }
}
