namespace Indra.Ocr.Text;

/// <summary>One character of a text-layer run: the character and its box (l, b, r, t).</summary>
public readonly record struct TextChar(string Character, double[] Bbox);

/// <summary>One text-layer run: its box, its text, and its characters with boxes.</summary>
public sealed class TextLine
{
    public TextLine(double[] bbox, string text, IReadOnlyList<TextChar> chars)
    {
        Bbox = bbox;
        Text = text;
        Chars = chars;
    }

    /// <summary>The box (left, top, right, bottom) in the rendered frame, from the ink characters.</summary>
    public double[] Bbox { get; }

    public string Text { get; }

    /// <summary>The characters in PDF order (whitespace included).</summary>
    public IReadOnlyList<TextChar> Chars { get; }
}

/// <summary>
/// What <c>extract_runs</c> and <c>read_text_layer</c> counted: the reference's <c>stats</c>
/// dictionary. <see cref="OffPage"/> is only filled in by the PDF backend's <c>extract_runs</c>,
/// <see cref="NoInk"/> only by <see cref="TextLayerReader.ReadTextLayer"/>.
/// </summary>
public sealed record TextLayerStats
{
    /// <summary>Non-whitespace characters seen.</summary>
    public int Chars { get; init; }

    /// <summary>Characters dropped because they are not upright on the rendered page.</summary>
    public int Rotated { get; init; }

    /// <summary>Characters whose code is not text (replacement, private use, control, ...).</summary>
    public int Bad { get; init; }

    /// <summary>Characters whose centre falls outside the rendered frame.</summary>
    public int OffPage { get; init; }

    /// <summary>Share of runs with no ink under them, rounded to 4 decimals (Python's <c>round</c>).</summary>
    public double? NoInk { get; init; }
}

/// <summary>The text layer of one page and the verdict on it.</summary>
public sealed class TextLayer
{
    public TextLayer(
        IReadOnlyList<TextLine> lines,
        (double Width, double Height) size,
        bool trusted,
        string reason,
        TextLayerStats stats)
    {
        Lines = lines;
        Size = size;
        Trusted = trusted;
        Reason = reason;
        Stats = stats;
    }

    /// <summary>The runs in PDF content order.</summary>
    public IReadOnlyList<TextLine> Lines { get; }

    /// <summary>Page width and height in points, as rendered.</summary>
    public (double Width, double Height) Size { get; }

    public bool Trusted { get; }

    /// <summary>Why the layer is not trusted (empty when it is).</summary>
    public string Reason { get; }

    public TextLayerStats Stats { get; }
}
