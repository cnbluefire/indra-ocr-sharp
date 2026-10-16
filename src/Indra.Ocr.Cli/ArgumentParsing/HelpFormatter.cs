using System.Text.RegularExpressions;

namespace Indra.Ocr.ArgumentParsing;

/// <summary>
/// A port of <c>argparse.HelpFormatter</c> (CPython 3.14) with
/// <c>RawDescriptionHelpFormatter</c>'s <c>_fill_text</c>, which is the pair the reference's
/// parser uses: the usage line, the column layout, the wrapping and the section order are
/// argparse's own, so <c>--help</c> comes out as the reference's, byte for byte.
/// </summary>
/// <remarks>
/// argparse colors its output when it writes to a terminal and takes the width from the terminal;
/// this port leaves the colors out (they are an escape sequence around text that is otherwise
/// identical) and takes the width from <c>COLUMNS</c>, then the console, then 80 — the fallbacks
/// <c>shutil.get_terminal_size</c> uses.
/// <para>
/// The methods keep argparse's names (<c>_format_action</c> → <see cref="FormatAction"/>) so the
/// two can be read side by side.
/// </para>
/// </remarks>
internal sealed class HelpFormatter
{
    private static readonly Regex LongBreak = new(@"\n\n\n+", RegexOptions.Compiled);

    // re.ASCII's \s: Python's _whitespace_matcher is compiled with re.ASCII, so only these count.
    private static readonly Regex Whitespace = new(@"[ \t\n\r\f\v]+", RegexOptions.Compiled);

    private readonly string _prog;
    private readonly int _indentIncrement;
    private readonly int _maxHelpPosition;
    private readonly int _width;
    private readonly Section _rootSection;
    private Section _currentSection;
    private int _currentIndent;
    private int _actionMaxLength;

    public HelpFormatter(string prog, int indentIncrement = 2, int maxHelpPosition = 24,
                         int? width = null)
    {
        int w = width ?? TerminalColumns() - 2;
        _prog = prog;
        _indentIncrement = indentIncrement;
        _maxHelpPosition = Math.Min(maxHelpPosition, Math.Max(w - 20, indentIncrement * 2));
        _width = w;
        _rootSection = new Section(this, null, null);
        _currentSection = _rootSection;
    }

    /// <summary>The formatter's width (the terminal's, minus 2).</summary>
    public int Width => _width;

    // ==============================
    // Section and indentation
    // ==============================

    private void Indent() => _currentIndent += _indentIncrement;

    private void Dedent() => _currentIndent -= _indentIncrement;

    public void StartSection(string heading)
    {
        Indent();
        var section = new Section(this, _currentSection, heading);
        _currentSection.Add(() => section.FormatHelp());
        _currentSection = section;
    }

    public void EndSection()
    {
        _currentSection = _currentSection.Parent!;
        Dedent();
    }

    // ========================
    // Message building
    // ========================

    public void AddText(string? text)
    {
        if (text is not null)
        {
            _currentSection.Add(() => FormatText(text));
        }
    }

    public void AddUsage(string? usage, IReadOnlyList<Argument> actions)
        => _currentSection.Add(() => FormatUsage(usage, actions));

    public void AddArgument(Argument action)
    {
        // _action_max_length counts the invocation plus the indent in force, and the items are
        // formatted later — by then every action has been counted, so the help column is the same
        // for all of them.
        int length = FormatActionInvocation(action).Length + _currentIndent;
        _actionMaxLength = Math.Max(_actionMaxLength, length);
        _currentSection.Add(() => FormatAction(action));
    }

    public void AddArguments(IEnumerable<Argument> actions)
    {
        foreach (Argument action in actions)
        {
            AddArgument(action);
        }
    }

    public string FormatHelp()
    {
        string help = _rootSection.FormatHelp();
        if (help.Length > 0)
        {
            help = LongBreak.Replace(help, "\n\n");
            help = help.Trim('\n') + "\n";
        }

        return help;
    }

    // ========================
    // Help-formatting methods
    // ========================

    private string FormatUsage(string? usage, IReadOnlyList<Argument> actions)
    {
        const string prefix = "usage: ";

        if (usage is not null)
        {
            usage = usage.Replace("%(prog)s", _prog);
        }
        else if (actions.Count == 0)
        {
            usage = _prog;
        }
        else
        {
            (List<string> parts, int posStart) = GetActionsUsageParts(actions);
            usage = string.Join(" ", new[] { _prog }.Concat(parts).Where(p => p.Length > 0));

            // Wrap the usage parts if it is too long.
            int textWidth = _width - _currentIndent;
            if (prefix.Length + usage.Length > textWidth)
            {
                List<string> optParts = parts.Take(posStart).ToList();
                List<string> posParts = parts.Skip(posStart).ToList();
                List<string> lines;
                if (prefix.Length + _prog.Length <= 0.75 * textWidth)
                {
                    // A short prog: follow it with the optionals, then the positionals.
                    string indent = new(' ', prefix.Length + _prog.Length + 1);
                    if (optParts.Count > 0)
                    {
                        lines = GetLines(new[] { _prog }.Concat(optParts).ToList(), indent, prefix,
                                         textWidth);
                        lines.AddRange(GetLines(posParts, indent, null, textWidth));
                    }
                    else if (posParts.Count > 0)
                    {
                        lines = GetLines(new[] { _prog }.Concat(posParts).ToList(), indent, prefix,
                                         textWidth);
                    }
                    else
                    {
                        lines = new List<string> { _prog };
                    }
                }
                else
                {
                    // A long prog: put it on its own line.
                    string indent = new(' ', prefix.Length);
                    var all = new List<string>(optParts);
                    all.AddRange(posParts);
                    lines = GetLines(all, indent, null, textWidth);
                    if (lines.Count > 1)
                    {
                        lines = new List<string>();
                        lines.AddRange(GetLines(optParts, indent, null, textWidth));
                        lines.AddRange(GetLines(posParts, indent, null, textWidth));
                    }

                    lines.Insert(0, _prog);
                }

                usage = string.Join("\n", lines);
            }

            usage = usage[_prog.Length..];
            usage = _prog + usage;
        }

        return prefix + usage + "\n\n";
    }

    /// <summary>
    /// argparse's <c>_get_actions_usage_parts</c>: the usage pieces, with the index where the
    /// positionals begin. The reference has no mutually exclusive group, so every piece is one
    /// action and the optionals come first.
    /// </summary>
    private (List<string> Parts, int PosStart) GetActionsUsageParts(IReadOnlyList<Argument> actions)
    {
        var optionals = new List<Argument>();
        var positionals = new List<Argument>();
        foreach (Argument action in actions)
        {
            (action.IsOptional ? optionals : positionals).Add(action);
        }

        var ordered = new List<Argument>(optionals);
        ordered.AddRange(positionals);

        var parts = new List<string>();
        int posStart = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i == optionals.Count)
            {
                posStart = parts.Count;
            }

            Argument action = ordered[i];
            if (!action.IsOptional)
            {
                parts.Add(FormatArgs(action, action.Dest));
                continue;
            }

            // The first way to invoke the option, in brackets: not required, not in a group.
            string part = action.Arity == ArgumentArity.Zero
                ? action.FormatUsage()
                : action.OptionStrings[0] + " " + FormatArgs(action, action.Dest.ToUpperInvariant());
            parts.Add("[" + part + "]");
        }

        if (posStart < 0)
        {
            posStart = parts.Count;
        }

        return (parts, posStart);
    }

    /// <summary>argparse's <c>get_lines</c>: the wrapping helper of the usage line.</summary>
    private static List<string> GetLines(List<string> parts, string indent, string? prefix,
                                         int textWidth)
    {
        var lines = new List<string>();
        var line = new List<string>();
        int indentLength = indent.Length;
        int lineLen = prefix is not null ? prefix.Length - 1 : indentLength - 1;
        foreach (string part in parts)
        {
            if (lineLen + 1 + part.Length > textWidth && line.Count > 0)
            {
                lines.Add(indent + string.Join(" ", line));
                line = new List<string>();
                lineLen = indentLength - 1;
            }

            line.Add(part);
            lineLen += part.Length + 1;
        }

        if (line.Count > 0)
        {
            lines.Add(indent + string.Join(" ", line));
        }

        if (prefix is not null && lines.Count > 0)
        {
            lines[0] = lines[0][indentLength..];
        }

        return lines;
    }

    /// <summary>argparse's <c>_format_text</c>: the description, kept line for line.</summary>
    private static string FormatText(string text) => text + "\n\n";

    /// <summary>argparse's <c>_format_action</c>: one entry of a section.</summary>
    private string FormatAction(Argument action)
    {
        int helpPosition = Math.Min(_actionMaxLength + 2, _maxHelpPosition);
        int helpWidth = Math.Max(_width - helpPosition, 11);
        int actionWidth = helpPosition - _currentIndent - 2;
        string header = FormatActionInvocation(action);
        int indentFirst;

        if (action.Help is null)
        {
            // No help: the header on its own line.
            header = new string(' ', _currentIndent) + header + "\n";
            indentFirst = 0;
        }
        else if (header.Length <= actionWidth)
        {
            // A short name: the help starts on the same line, two spaces after the column.
            header = new string(' ', _currentIndent) + header.PadRight(actionWidth) + "  ";
            indentFirst = 0;
        }
        else
        {
            // A long name: the help starts on the next line.
            header = new string(' ', _currentIndent) + header + "\n";
            indentFirst = helpPosition;
        }

        var parts = new List<string> { header };
        if (!string.IsNullOrEmpty(action.Help) && action.Help.Trim().Length > 0)
        {
            string helpText = ExpandHelp(action);
            if (helpText.Length > 0)
            {
                List<string> helpLines = SplitLines(helpText, helpWidth);
                parts.Add(new string(' ', indentFirst) + helpLines[0] + "\n");
                for (int i = 1; i < helpLines.Count; i++)
                {
                    parts.Add(new string(' ', helpPosition) + helpLines[i] + "\n");
                }
            }
        }
        else if (!header.EndsWith('\n'))
        {
            parts.Add("\n");
        }

        return string.Concat(parts);
    }

    /// <summary>argparse's <c>_format_action_invocation</c>.</summary>
    private string FormatActionInvocation(Argument action)
    {
        if (!action.IsOptional)
        {
            return string.Join(" ", MetavarFormatter(action, action.Dest)(1));
        }

        if (action.Arity == ArgumentArity.Zero)
        {
            return string.Join(", ", action.OptionStrings);
        }

        return string.Join(", ", action.OptionStrings) + " " +
               FormatArgs(action, action.Dest.ToUpperInvariant());
    }

    /// <summary>argparse's <c>_metavar_formatter</c>: the metavar, or the choices in braces.</summary>
    private static Func<int, string[]> MetavarFormatter(Argument action, string defaultMetavar)
    {
        string result = action.Metavar
            ?? (action.Choices is not null
                ? "{" + string.Join(",", action.Choices) + "}"
                : defaultMetavar);
        return size =>
        {
            var metavars = new string[size];
            Array.Fill(metavars, result);
            return metavars;
        };
    }

    /// <summary>argparse's <c>_format_args</c> (the nargs values the CLI uses).</summary>
    private string FormatArgs(Argument action, string defaultMetavar)
    {
        Func<int, string[]> getMetavar = MetavarFormatter(action, defaultMetavar);
        return action.Arity switch
        {
            ArgumentArity.None => getMetavar(1)[0],
            ArgumentArity.OneOrMore => FormatOneOrMore(getMetavar(2)),
            ArgumentArity.Zero => "",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static string FormatOneOrMore(string[] metavar) => $"{metavar[0]} [{metavar[1]} ...]";

    /// <summary>argparse's <c>_expand_help</c>: the help string with its <c>%</c> fields filled.</summary>
    private static string ExpandHelp(Argument action)
    {
        string help = action.Help!;
        if (!help.Contains('%'))
        {
            return help;
        }

        // The reference's help strings hold no %-fields (they interpolate before the parser sees
        // them), and a stray % would be a ValueError in Python too: fail loudly rather than print
        // a mangled line.
        throw new InvalidOperationException(
            $"the help of {action.Dest} has a % but no fields to fill: {help}");
    }

    /// <summary>argparse's <c>_split_lines</c>: collapse the whitespace, then wrap.</summary>
    private static List<string> SplitLines(string text, int width)
        => TextWrap.Wrap(Whitespace.Replace(text, " ").Trim(), width);

    /// <summary>The width the help is wrapped to: <c>shutil.get_terminal_size().columns</c>.</summary>
    private static int TerminalColumns()
    {
        string? columns = Environment.GetEnvironmentVariable("COLUMNS");
        if (columns is not null && int.TryParse(columns, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        try
        {
            if (!Console.IsOutputRedirected)
            {
                return Console.WindowWidth;
            }
        }
        catch (IOException)
        {
            // No console to ask.
        }

        return 80;
    }

    private sealed class Section
    {
        private readonly HelpFormatter _formatter;
        private readonly List<Func<string>> _items = new();

        public Section(HelpFormatter formatter, Section? parent, string? heading)
        {
            _formatter = formatter;
            Parent = parent;
            Heading = heading;
        }

        public Section? Parent { get; }

        private string? Heading { get; }

        public void Add(Func<string> item) => _items.Add(item);

        public string FormatHelp()
        {
            if (Parent is not null)
            {
                _formatter.Indent();
            }

            string itemHelp = string.Concat(_items.Select(item => item()).Where(text => text.Length > 0));
            if (Parent is not null)
            {
                _formatter.Dedent();
            }

            if (itemHelp.Length == 0)
            {
                return "";
            }

            string heading = Heading is null
                ? ""
                : new string(' ', _formatter._currentIndent) + Heading + ":\n";
            return "\n" + heading + itemHelp + "\n";
        }
    }

    /// <summary>
    /// A port of <c>textwrap</c> with <c>TextWrapper</c>'s defaults, which is what argparse's
    /// <c>_split_lines</c> calls: a greedy fill, with the two ways a word too long for the line
    /// can be broken (at a hyphen, or hard).
    /// </summary>
    private static class TextWrap
    {
        // textwrap's wordsep_re with break_on_hyphens on (its default): whitespace, an em-dash
        // between words, or a word that may be split at a hyphen. Python's \s, \w and \d are
        // spelled out for the ones it means.
        private static readonly Regex WordSep = new(
            "(" +
            @"[ \t\n\v\f\r]+" +
            @"|(?<=[\w!""'&.,?])-{2,}(?=\w)" +
            @"|[^ \t\n\v\f\r]+?(?:" +
            @"-(?:(?<=[^\d\W]{2}-)|(?<=[^\d\W]-[^\d\W]-))(?=[^\d\W]-?[^\d\W])" +
            @"|(?= |\z)" +
            @"|(?<=[\w!""'&.,?])(?=-{2,}\w)" +
            "))",
            RegexOptions.Compiled);

        public static List<string> Wrap(string text, int width)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "invalid width (must be > 0)");
            }

            var chunks = Split(text);
            chunks.Reverse();
            var lines = new List<string>();
            while (chunks.Count > 0)
            {
                var line = new List<string>();
                int length = 0;

                // The initial and subsequent indents are both empty, so the width is the width.
                if (lines.Count > 0 && chunks[^1].Trim().Length == 0)
                {
                    chunks.RemoveAt(chunks.Count - 1);
                }

                while (chunks.Count > 0)
                {
                    int chunk = chunks[^1].Length;
                    if (length + chunk <= width)
                    {
                        line.Add(chunks[^1]);
                        chunks.RemoveAt(chunks.Count - 1);
                        length += chunk;
                    }
                    else
                    {
                        break;
                    }
                }

                if (chunks.Count > 0 && chunks[^1].Length > width)
                {
                    HandleLongWord(chunks, line, length, width);
                    length = line.Sum(part => part.Length);
                }

                if (line.Count > 0 && line[^1].Trim().Length == 0)
                {
                    length -= line[^1].Length;
                    line.RemoveAt(line.Count - 1);
                }

                if (line.Count > 0)
                {
                    lines.Add(string.Concat(line));
                }
            }

            return lines;
        }

        /// <summary>textwrap's <c>_split</c>: the words, and the whitespace between them.</summary>
        private static List<string> Split(string text)
        {
            var chunks = new List<string>();
            foreach (string chunk in WordSep.Split(text))
            {
                if (chunk.Length > 0)
                {
                    chunks.Add(chunk);
                }
            }

            return chunks;
        }

        /// <summary>textwrap's <c>_handle_long_word</c> (break_long_words is on).</summary>
        private static void HandleLongWord(List<string> chunks, List<string> line, int length,
                                           int width)
        {
            int spaceLeft = width < 1 ? 1 : width - length;
            if (spaceLeft > 0)
            {
                int end = spaceLeft;
                string chunk = chunks[^1];
                if (chunk.Length > spaceLeft)
                {
                    // Break after the last hyphen, but only if there are non-hyphens before it.
                    int hyphen = chunk.LastIndexOf('-', Math.Min(spaceLeft, chunk.Length) - 1);
                    if (hyphen > 0 && chunk[..hyphen].Any(c => c != '-'))
                    {
                        end = hyphen + 1;
                    }
                }

                line.Add(chunk[..end]);
                chunks[^1] = chunk[end..];
            }
            else if (line.Count == 0)
            {
                line.Add(chunks[^1]);
                chunks.RemoveAt(chunks.Count - 1);
            }
        }
    }
}
