using System.Text;

namespace Indra.Ocr.Text;

/// <summary>
/// Unicode Normalization Form C, the way CPython's <c>unicodedata.normalize("NFC", text)</c>
/// does it — the reference's <c>vocab.normalize</c>.
/// </summary>
/// <remarks>
/// <para>
/// The port cannot use <see cref="string.Normalize(NormalizationForm)"/> for this: with
/// invariant globalization — which the AOT builds set, so the native binary needs no ICU — .NET
/// performs no normalization at all, and a label would silently keep the combining marks the
/// reference composes away.
/// </para>
/// <para>
/// So UAX #15's algorithm runs here over <see cref="NfcTables"/> (generated from CPython's own
/// Unicode 16.0.0): canonical decomposition, canonical ordering, canonical composition. The
/// answer therefore does not depend on the machine's ICU version, and code points with no entry
/// — unpaired surrogates included, which .NET's <c>Normalize</c> refuses outright — pass
/// through exactly as Python lets them.
/// </para>
/// </remarks>
internal static class Nfc
{
    // Hangul syllables decompose and compose arithmetically (UAX #15 section 3.12), which is
    // why the tables leave them out.
    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
    private const int LCount = 19, VCount = 21, TCount = 28;
    private const int NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>The text in NFC.</summary>
    public static string Normalize(string text)
    {
        if (text.Length == 0) return text;

        var codePoints = new List<int>(text.Length);
        var classes = new List<int>(text.Length);
        Decompose(text, codePoints, classes);
        Reorder(codePoints, classes);
        return ToString(Compose(codePoints, classes));
    }

    /// <summary>Canonical decomposition, recursively, code point by code point.</summary>
    private static void Decompose(string text, List<int> codePoints, List<int> classes)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            DecomposeCodePoint(cp, codePoints, classes);
        }
    }

    private static void DecomposeCodePoint(int cp, List<int> codePoints, List<int> classes)
    {
        int s = cp - SBase;
        if (s >= 0 && s < SCount)
        {
            codePoints.Add(LBase + s / NCount);
            classes.Add(0);
            codePoints.Add(VBase + s % NCount / TCount);
            classes.Add(0);
            if (s % TCount != 0)
            {
                codePoints.Add(TBase + s % TCount);
                classes.Add(0);
            }
            return;
        }

        if (Decomposition(cp, out int first, out int second))
        {
            DecomposeCodePoint(first, codePoints, classes);
            if (second != 0) DecomposeCodePoint(second, codePoints, classes);
            return;
        }

        codePoints.Add(cp);
        classes.Add(CombiningClass(cp));
    }

    /// <summary>
    /// Canonical ordering: a stable sort by combining class, which only ever moves a mark left
    /// past a mark of a strictly larger class, so equal classes keep their order.
    /// </summary>
    private static void Reorder(List<int> codePoints, List<int> classes)
    {
        for (int i = 1; i < codePoints.Count; i++)
        {
            int cls = classes[i];
            if (cls == 0) continue;
            int j = i;
            while (j > 0 && classes[j - 1] > cls)
            {
                (codePoints[j - 1], codePoints[j]) = (codePoints[j], codePoints[j - 1]);
                (classes[j - 1], classes[j]) = (classes[j], classes[j - 1]);
                j--;
            }
        }
    }

    /// <summary>
    /// Canonical composition: the last starter absorbs every following character it composes
    /// with, as long as nothing of the same or a higher class stands between them.
    /// </summary>
    private static List<int> Compose(List<int> codePoints, List<int> classes)
    {
        var result = new List<int>(codePoints.Count) { codePoints[0] };
        int starterAt = 0;
        int starter = codePoints[0];
        int lastClass = classes[0];
        if (lastClass != 0) lastClass = 256;  // an irregular sequence: nothing composes onto it

        for (int i = 1; i < codePoints.Count; i++)
        {
            int cp = codePoints[i];
            int cls = classes[i];
            int composite = Composite(starter, cp);
            if (composite != 0 && (lastClass < cls || lastClass == 0))
            {
                result[starterAt] = composite;
                starter = composite;
                continue;
            }

            if (cls == 0)
            {
                starterAt = result.Count;
                starter = cp;
            }
            lastClass = cls;
            result.Add(cp);
        }
        return result;
    }

    private static string ToString(List<int> codePoints)
    {
        var sb = new StringBuilder(codePoints.Count);
        foreach (int cp in codePoints)
        {
            if (cp < 0x10000) sb.Append((char)cp);
            else sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    /// <summary>Whether the code point decomposes canonically, and into what.</summary>
    private static bool Decomposition(int cp, out int first, out int second)
    {
        var table = NfcTables.Decompositions;
        int lo = 0, hi = table.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (table[mid].Cp == cp)
            {
                first = table[mid].First;
                second = table[mid].Second;
                return true;
            }
            if (table[mid].Cp < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        first = 0;
        second = 0;
        return false;
    }

    /// <summary>The primary composite of two code points, or 0.</summary>
    private static int Composite(int first, int second)
    {
        // Hangul composes arithmetically too (UAX #15 section 3.12): L + V makes an LV
        // syllable, and an LV syllable + T makes an LVT one.
        int l = first - LBase;
        if (l >= 0 && l < LCount)
        {
            int v = second - VBase;
            if (v >= 0 && v < VCount) return SBase + (l * VCount + v) * TCount;
        }
        int s = first - SBase;
        if (s >= 0 && s < SCount && s % TCount == 0)
        {
            int t = second - TBase;
            if (t > 0 && t < TCount) return first + t;
        }

        var table = NfcTables.Compositions;
        int lo = 0, hi = table.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (table[mid].First == first && table[mid].Second == second)
                return table[mid].Composite;
            if (table[mid].First < first ||
                (table[mid].First == first && table[mid].Second < second))
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return 0;
    }

    /// <summary>The canonical combining class of a code point (0 when it is not in a run).</summary>
    private static int CombiningClass(int cp)
    {
        var table = NfcTables.Combining;
        int lo = 0, hi = table.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < table[mid].First) hi = mid - 1;
            else if (cp > table[mid].Last) lo = mid + 1;
            else return table[mid].Class;
        }
        return 0;
    }
}
