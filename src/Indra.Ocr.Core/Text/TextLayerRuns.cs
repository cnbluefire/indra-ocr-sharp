using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;

namespace Indra.Ocr.Text;

/// <summary>
/// The port of the text-layer algorithms of <c>indra_ocr/document.py</c>: which runs the
/// recognizer can read, their base direction, and how they land on the detected lines.
/// </summary>
/// <remarks>
/// All three work on runs, boxes and tokens — recognition-result data — so they live with the
/// text layer in Core rather than with either decoder.
/// </remarks>
public static class TextLayerRuns
{
    /// <summary>Share of the layer's letters the recognizer must know.</summary>
    public const double ReadableMin = 0.9;

    /// <summary>
    /// <c>readable</c>: whether the recognizer can read the script of these text-layer runs.
    /// </summary>
    public static bool Readable(OrtOcr ocr, IReadOnlyList<TextLine> runs)
    {
        IReadOnlyDictionary<string, int>? cjk = ocr.VocabCjk?.Index;
        int letters = 0, known = 0;
        foreach (TextLine run in runs)
        {
            foreach (string c in UnicodeText.CodePoints(run.Text))
            {
                if (!UnicodeText.IsAlpha(c))
                {
                    continue;
                }

                letters++;
                if (ocr.Vocab.Index.ContainsKey(c) || (cjk is not null && cjk.ContainsKey(c)))
                {
                    known++;
                }
            }
        }

        return letters == 0 || known >= ReadableMin * letters;
    }

    /// <summary>
    /// <c>right_to_left</c>: the Unicode bidi base direction — most strong characters are
    /// right-to-left.
    /// </summary>
    public static bool RightToLeft(IEnumerable<RecToken> tokens)
    {
        int rtl = 0, ltr = 0;
        foreach (RecToken t in tokens)
        {
            string? c = t.Character;
            // The reference asks for the bidi class only of a one-character string.
            string b = c is not null && UnicodeText.CodePointCount(c) == 1
                ? UnicodeText.BidiClass(c)
                : "";
            if (b is "R" or "AL")
            {
                rtl++;
            }
            else if (b == "L")
            {
                ltr++;
            }
        }

        return rtl > ltr;
    }

    /// <summary>
    /// <c>assign_runs</c>: tokens (char, x, content index) per detected line.
    /// </summary>
    /// <remarks>
    /// A run is one text-layer line piece (<see cref="TextLine"/>), in content order. Each
    /// character goes to the smallest detected box that contains its centre (a space follows the
    /// character before it), so a run that spans two table cells splits between them. Inside a
    /// detected line, each run's part keeps the PDF character order, and the parts are ordered by
    /// x — right to left in a right-to-left line. <paramref name="boxes"/> are pixels (the
    /// detector's <c>[N, 4]</c>), <paramref name="k"/> pixels per point. Returns the tokens per
    /// detected line and the number of characters outside every box.
    /// <para>
    /// The containment test is a <b>float32</b> comparison, because the reference compares a
    /// float32 array against a Python float and numpy casts the scalar to the array's dtype
    /// (NEP 50): a centre a hair below a box edge still lands inside when it rounds onto it.
    /// </para>
    /// </remarks>
    public static (List<List<RecToken>> Tokens, int Lost) AssignRuns(
        IReadOnlyList<TextLine> runs, ReadOnlySpan<float> boxes, double k)
    {
        int n = boxes.Length / 4;
        var toks = new List<List<RecToken>>(n);
        int lost = 0;
        if (n == 0)
        {
            foreach (TextLine run in runs)
            {
                foreach (TextChar ch in run.Chars)
                {
                    if (!UnicodeText.IsSpace(ch.Character))
                    {
                        lost++;
                    }
                }
            }

            return (toks, lost);
        }

        // Per detected line, the run parts in the order the runs were first seen (a Python dict
        // iterates in insertion order, and the sorts below are stable).
        var order = new List<int>[n];
        var parts = new Dictionary<int, List<RecToken>>[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = [];
            parts[i] = [];
        }

        var area = new float[n];
        for (int i = 0; i < n; i++)
        {
            area[i] = (boxes[i * 4 + 2] - boxes[i * 4 + 0])
                      * (boxes[i * 4 + 3] - boxes[i * 4 + 1]);
        }

        for (int idx = 0; idx < runs.Count; idx++)
        {
            int j = -1;
            foreach (TextChar ch in runs[idx].Chars)
            {
                if (!UnicodeText.IsSpace(ch.Character))
                {
                    double cx = (ch.Bbox[0] + ch.Bbox[2]) / 2 * k;
                    double cy = (ch.Bbox[1] + ch.Bbox[3]) / 2 * k;
                    float cxf = (float)cx, cyf = (float)cy;
                    int best = -1;
                    float bestArea = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        if (boxes[i * 4 + 0] <= cxf && cxf <= boxes[i * 4 + 2] &&
                            boxes[i * 4 + 1] <= cyf && cyf <= boxes[i * 4 + 3] &&
                            (best < 0 || area[i] < bestArea))
                        {
                            best = i; // the first minimum, as numpy's argmin returns
                            bestArea = area[i];
                        }
                    }

                    if (best < 0)
                    {
                        lost++;
                        j = -1;
                        continue;
                    }

                    j = best;
                    if (!parts[j].TryGetValue(idx, out List<RecToken>? list))
                    {
                        list = [];
                        parts[j][idx] = list;
                        order[j].Add(idx);
                    }

                    list.Add(new RecToken(0, ch.Character, cx, idx));
                }
                else if (j >= 0)
                {
                    List<RecToken> list = parts[j][idx];
                    list.Add(new RecToken(0, ch.Character, list[^1].X, idx));
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            var all = new List<RecToken>();
            foreach (int idx in order[i])
            {
                all.AddRange(parts[i][idx]);
            }

            bool rtl = RightToLeft(all);
            var sorted = new List<int>(order[i]);
            sorted.Sort((a, b) => rtl
                ? (-MaxX(parts[i][a])).CompareTo(-MaxX(parts[i][b]))
                : MinX(parts[i][a]).CompareTo(MinX(parts[i][b])));
            var tk = new List<RecToken>();
            foreach (int idx in sorted)
            {
                if (tk.Count > 0)
                {
                    tk.Add(new RecToken(0, " ", tk[^1].X, idx)); // parts of different runs
                }

                tk.AddRange(parts[i][idx]);
            }

            toks.Add(tk);
        }

        return (toks, lost);
    }

    private static double MinX(List<RecToken> toks)
    {
        double min = double.PositiveInfinity;
        foreach (RecToken t in toks)
        {
            min = Math.Min(min, t.X);
        }

        return min;
    }

    private static double MaxX(List<RecToken> toks)
    {
        double max = double.NegativeInfinity;
        foreach (RecToken t in toks)
        {
            max = Math.Max(max, t.X);
        }

        return max;
    }
}
