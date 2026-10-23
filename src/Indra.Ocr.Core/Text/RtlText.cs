using System.Globalization;
using System.Text;

namespace Indra.Ocr.Text;

/// <summary>The reference's small, involutive subset of Unicode bidi for recognizer labels.</summary>
public static class RtlText
{
    private const int Tatweel = 0x640;

    public static bool IsRtl(int codePoint) => UnicodeText.BidiClass(codePoint) is "R" or "AL";

    public static bool HasRtl(string text) => UnicodeText.CodePointValues(text).Any(IsRtl);

    public static bool RtlBase(string text)
    {
        int rtl = 0, ltr = 0;
        foreach (int cp in UnicodeText.CodePointValues(text))
        {
            string bidi = UnicodeText.BidiClass(cp);
            if (bidi is "R" or "AL") rtl++;
            else if (bidi == "L") ltr++;
        }
        return rtl > ltr;
    }

    /// <summary>Presentation forms to base letters, no tatweel, no leading combining mark.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (int cp in UnicodeText.CodePointValues(text))
        {
            if (cp == Tatweel) continue;
            string mapped = Compatibility(cp);
            foreach (string part in UnicodeText.CodePoints(mapped))
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(part, 0);
                if (sb.Length > 0 || category is not UnicodeCategory.NonSpacingMark
                                                and not UnicodeCategory.EnclosingMark)
                    sb.Append(part);
            }
        }
        return sb.ToString();
    }

    public static string Flip(string text, bool mirror = true, bool? baseRtl = null)
    {
        if (!HasRtl(text)) return text;
        List<string> clusters = Clusters(text);
        string[] kinds = clusters.Select(Kind).ToArray();
        if (baseRtl ?? RtlBase(text))
        {
            var units = new List<string>();
            List<(int Start, int End)> spans = Runs(kinds, "L", ["R"]);
            int i = 0;
            spans.Add((clusters.Count, clusters.Count));
            foreach ((int a, int b) in spans)
            {
                for (int j = i; j < a; j++) units.Add(mirror ? Mirror(clusters[j]) : clusters[j]);
                if (a < b) units.Add(string.Concat(clusters.GetRange(a, b - a)));
                i = b;
            }
            units.Reverse();
            return string.Concat(units);
        }

        var output = clusters.ToArray();
        foreach ((int a, int b) in Runs(kinds, "R", ["L"]))
            for (int i = a; i < b; i++)
            {
                string value = clusters[b - 1 - (i - a)];
                output[i] = mirror ? Mirror(value) : value;
            }
        return string.Concat(output);
    }

    public static string VisualLabel(string logical, bool? baseRtl = null) =>
        Flip(Normalize(logical), baseRtl: baseRtl);

    public static string LabelFromVisual(string rawVisual, bool shapes = false) =>
        VisualLabel(Flip(rawVisual, mirror: shapes));

    public static bool BracketsAsShapes(IReadOnlyList<string> rawVisualLines)
    {
        int shapes = 0, logical = 0;
        foreach (string text in rawVisualLines)
        {
            if (!RtlBase(text) || UnicodeText.CodePointValues(text)
                                             .Any(cp => UnicodeText.BidiClass(cp) == "L")) continue;
            foreach (int cp in UnicodeText.CodePointValues(text))
            {
                if ("([{".Contains((char)cp)) { shapes++; break; }
                if (")]}".Contains((char)cp)) { logical++; break; }
            }
        }
        return shapes > logical;
    }

    private static string Compatibility(int cp)
    {
        var a = RtlPresentationForms.Mappings;
        int lo = 0, hi = a.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid].CodePoint == cp) return a[mid].Text;
            if (a[mid].CodePoint < cp) lo = mid + 1; else hi = mid - 1;
        }
        return char.ConvertFromUtf32(cp);
    }

    private static List<string> Clusters(string text)
    {
        var result = new List<string>();
        foreach (string cp in UnicodeText.CodePoints(text))
        {
            UnicodeCategory c = CharUnicodeInfo.GetUnicodeCategory(cp, 0);
            if (result.Count > 0 &&
                (c is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark))
                result[^1] += cp;
            else
                result.Add(cp);
        }
        return result;
    }

    private static string Kind(string cluster)
    {
        string bidi = UnicodeText.BidiClass(cluster.Substring(0, char.IsSurrogatePair(cluster, 0) ? 2 : 1));
        return bidi is "R" or "AL" ? "R" : bidi is "L" or "EN" or "AN" ? "L" : "N";
    }

    private static List<(int Start, int End)> Runs(string[] kinds, string strong, HashSet<string> stop)
    {
        var spans = new List<(int, int)>();
        int start = -1, last = -1;
        for (int i = 0; i <= kinds.Length; i++)
        {
            string kind = i == kinds.Length ? "#" : kinds[i];
            if (kind == strong)
            {
                if (start < 0) start = i;
                last = i;
            }
            else if (kind == "#" || stop.Contains(kind))
            {
                if (start >= 0) spans.Add((start, last + 1));
                start = last = -1;
            }
        }
        return spans;
    }

    private static string Mirror(string cluster)
    {
        char mapped = cluster[0] switch
        {
            '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{',
            '<' => '>', '>' => '<', '«' => '»', '»' => '«', '‹' => '›', '›' => '‹',
            _ => cluster[0],
        };
        return mapped + cluster.Substring(1);
    }
}
