using System.Text.Json;
using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

/// <summary>
/// Step 145: <see cref="UnicodeText.BidiClass"/> against <c>fixtures/bidi.json</c> — the ranges
/// Python's <c>unicodedata.bidirectional</c> returned when
/// <c>tools/scripts/test/gen_bidi_table.py</c> generated the table (Unicode 16.0.0, the version
/// of the CPython the reference runs on).
/// </summary>
public class BidiClassTests
{
    private static readonly JsonElement Fx = Fixtures.Load("bidi.json");

    /// <summary>The reference's class for every code point, expanded from the recorded ranges.</summary>
    private static string[] ReferenceClasses()
    {
        var classes = new string[0x110000];
        int start = 0;
        foreach (var r in Fx.GetProperty("ranges").EnumerateArray())
        {
            int end = r[0].GetInt32();
            string cls = r[1].GetString()!;
            for (int cp = start; cp <= end; cp++)
            {
                classes[cp] = cls;
            }

            start = end + 1;
        }

        Assert.True(start == 0x110000, "the recorded ranges must cover every code point");
        return classes;
    }

    /// <summary>
    /// The table is the whole implementation, so it is checked at every one of the 1 114 112
    /// code points, not sampled.
    /// </summary>
    [Fact]
    public void Bidi_class_matches_unicodedata_everywhere()
    {
        string[] want = ReferenceClasses();
        int bad = -1;
        string expected = "", actual = "";
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            actual = UnicodeText.BidiClass(cp);
            if (actual != want[cp])
            {
                bad = cp;
                expected = want[cp];
                break;
            }
        }

        Assert.True(bad < 0, $"U+{bad:X4}: got '{actual}', expected '{expected}'");
    }

    /// <summary>
    /// The three classes <c>document.right_to_left</c> counts: <c>R</c> and <c>AL</c> as
    /// right-to-left, <c>L</c> as left-to-right, everything else as neither.
    /// </summary>
    [Fact]
    public void Strong_classes_are_the_ones_right_to_left_counts()
    {
        Assert.Equal("L", UnicodeText.BidiClass('A'));
        Assert.Equal("L", UnicodeText.BidiClass(0x4E00)); // CJK
        Assert.Equal("R", UnicodeText.BidiClass(0x05D0)); // Hebrew alef
        Assert.Equal("AL", UnicodeText.BidiClass(0x0627)); // Arabic alef
        Assert.Equal("", UnicodeText.BidiClass(0x0378)); // unassigned
        Assert.Equal("ON", UnicodeText.BidiClass(0x1F600)); // emoji
        Assert.Equal("EN", UnicodeText.BidiClass('7'));
        Assert.Equal("WS", UnicodeText.BidiClass(' '));
        Assert.Equal("BN", UnicodeText.BidiClass(0x200B));
    }

    /// <summary>Python's <c>unicodedata.bidirectional(s)</c> takes one <i>code point</i>.</summary>
    [Fact]
    public void String_overload_takes_one_code_point()
    {
        Assert.Equal("L", UnicodeText.BidiClass("A"));
        Assert.Equal("R", UnicodeText.BidiClass("\u05D0"));
        Assert.Equal("ON", UnicodeText.BidiClass("\U0001F600")); // one code point, two chars
        Assert.Throws<ArgumentException>(() => UnicodeText.BidiClass(""));
        Assert.Throws<ArgumentException>(() => UnicodeText.BidiClass("AB"));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeText.BidiClass(0x110000));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeText.BidiClass(-1));
    }
}
