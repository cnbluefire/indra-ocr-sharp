using System.Text;
using System.Text.RegularExpressions;
using Indra.Ocr.Py;

namespace Indra.Ocr.Argparse;

/// <summary>
/// A port of the slice of Python's <c>argparse</c> (CPython 3.14) that the indra-ocr command line
/// uses: <c>--help</c> and <c>--version</c>, options with one value, a <c>nargs='+'</c> positional,
/// choices, types, argument groups, and the errors.
/// </summary>
/// <remarks>
/// The command line is a parity target, so its argument handling is ported rather than imitated:
/// the usage line, the abbreviation of long options (<c>--pre</c> for <c>--prefix</c>), <c>--opt=v</c>,
/// the <c>--</c> separator, the "unrecognized arguments" list, the wording of every error and the
/// exit codes (0 for help and version, 2 for an argument error) all come from argparse.
/// <para>
/// The port keeps argparse's names and its shape (<c>_parse_known_args</c>,
/// <c>_get_values</c>, <c>_parse_optional</c>, …) so the two can be read side by side. What the
/// reference's parser does not use — subparsers, mutually exclusive groups, <c>nargs</c> other
/// than <c>None</c>/<c>0</c>/<c>'+'</c>, <c>fromfile_prefix_chars</c>, deprecated arguments — is
/// not ported.
/// </para>
/// </remarks>
public sealed class ArgumentParser
{
    // argparse's own patterns, spelled the same way.
    private static readonly Regex NegativeNumber = new(@"^-\d+$|^-\d*\.\d+$", RegexOptions.Compiled);

    private readonly string _prog;
    private readonly string? _description;
    private readonly int? _width;
    private readonly List<Argument> _actions = new();
    private readonly List<string> _optionStrings = new();
    private readonly Dictionary<string, Argument> _optionStringActions = new();
    private readonly List<ArgumentGroup> _actionGroups = new();
    private readonly ArgumentGroup _positionals;
    private readonly ArgumentGroup _optionals;
    private bool _hasNegativeNumberOptionals;

    public ArgumentParser(string prog, string? description = null, int? width = null)
    {
        _prog = prog;
        _description = description;
        _width = width;

        _positionals = new ArgumentGroup(this, "positional arguments");
        _actionGroups.Add(_positionals);
        _optionals = new ArgumentGroup(this, "options");
        _actionGroups.Add(_optionals);

        // argparse adds -h/--help itself; --version is the caller's.
        AddArgument(
            ["-h", "--help"],
            dest: "help",
            action: ArgAction.Help,
            help: "show this help message and exit");
    }

    /// <summary>The program name, as it appears in the usage line and in errors.</summary>
    public string Prog => _prog;

    // ===================================
    // Building the parser
    // ===================================

    /// <summary><c>add_argument</c> for an option (<c>-h</c>, <c>--out</c>, <c>--cores/--threads</c>).</summary>
    public Argument AddArgument(
        string[] optionStrings,
        string? dest = null,
        Nargs nargs = Nargs.None,
        ArgType type = ArgType.Str,
        string[]? choices = null,
        object? @default = null,
        ArgAction action = ArgAction.Store,
        string? help = null,
        string? version = null,
        string? metavar = null)
        => AddArgument(optionStrings, dest, nargs, type, choices, @default, action, help, version,
                       metavar, null);

    /// <summary><c>add_argument</c> for a positional (<c>inputs</c>).</summary>
    public Argument AddPositional(
        string dest,
        Nargs nargs = Nargs.None,
        ArgType type = ArgType.Str,
        string[]? choices = null,
        object? @default = null,
        string? help = null,
        string? metavar = null)
    {
        var argument = new Argument([], dest, nargs, ArgAction.Store, type, choices, @default, help,
                                    null, metavar)
        {
            // argparse: a positional is required unless its nargs can match nothing. The CLI's
            // only positional takes one or more values, so it is always required.
            Required = true,
        };
        return Register(argument, _positionals);
    }

    /// <summary><c>add_argument_group</c>: a titled section of the help.</summary>
    public ArgumentGroup AddArgumentGroup(string title)
    {
        var group = new ArgumentGroup(this, title);
        _actionGroups.Add(group);
        return group;
    }

    internal Argument AddArgumentIn(ArgumentGroup group, string[] optionStrings, string? dest,
                                    Nargs nargs, ArgType type, string[]? choices, object? @default,
                                    ArgAction action, string? help, string? version,
                                    string? metavar)
        => AddArgument(optionStrings, dest, nargs, type, choices, @default, action, help, version,
                       metavar, group);

    private Argument AddArgument(
        string[] optionStrings,
        string? dest,
        Nargs nargs,
        ArgType type,
        string[]? choices,
        object? @default,
        ArgAction action,
        string? help,
        string? version,
        string? metavar,
        ArgumentGroup? group)
    {
        if (optionStrings.Length == 0)
        {
            throw new ArgumentException("an optional argument needs at least one option string");
        }

        // The actions the CLI uses that take no value, and their defaults.
        if (action is ArgAction.StoreTrue or ArgAction.Version or ArgAction.Help)
        {
            nargs = Nargs.Zero;
            if (action == ArgAction.StoreTrue && @default is null)
            {
                @default = false;
            }

            // argparse's _VersionAction carries this help of its own.
            if (action == ArgAction.Version && help is null)
            {
                help = "show program's version number and exit";
            }
        }

        var argument = new Argument(optionStrings, dest ?? DeriveDest(optionStrings), nargs, action,
                                    type, choices, @default, help, version, metavar)
        {
            Required = false,
            SuppressDest = action is ArgAction.Help or ArgAction.Version,
        };
        return Register(argument, group ?? _optionals);
    }

    private Argument Register(Argument action, ArgumentGroup group)
    {
        foreach (string optionString in action.OptionStrings)
        {
            if (_optionStringActions.ContainsKey(optionString))
            {
                throw new ArgumentException($"conflicting option string: {optionString}");
            }

            _optionStringActions[optionString] = action;
            _optionStrings.Add(optionString);
            if (NegativeNumber.IsMatch(optionString))
            {
                _hasNegativeNumberOptionals = true;
            }
        }

        _actions.Add(action);
        group.Add(action);
        return action;
    }

    /// <summary>argparse's <c>_get_optional_kwargs</c>: the dest from the long option string.</summary>
    private static string DeriveDest(string[] optionStrings)
    {
        foreach (string optionString in optionStrings)
        {
            if (optionString.Length > 2 && optionString[0] == '-' && optionString[1] == '-')
            {
                int equals = optionString.IndexOf('=');
                string prefix = equals < 0 ? optionString : optionString[..equals];
                return prefix.TrimStart('-').Replace('-', '_');
            }
        }

        string first = optionStrings[0].TrimStart('-').Replace('-', '_');
        if (first.Length == 0)
        {
            throw new ArgumentException($"dest= is required for options like '{optionStrings[0]}'");
        }

        return first;
    }

    // ===================================
    // Help and errors
    // ===================================

    /// <summary><c>format_help</c>: the usage, the description and the sections.</summary>
    public string FormatHelp()
    {
        HelpFormatter formatter = NewFormatter();
        formatter.AddUsage(null, _actions);
        formatter.AddText(_description);
        foreach (ArgumentGroup group in _actionGroups)
        {
            formatter.StartSection(group.Title);
            formatter.AddArguments(group.Actions);
            formatter.EndSection();
        }

        return formatter.FormatHelp();
    }

    /// <summary><c>format_usage</c>: only the usage line (what an error prints first).</summary>
    public string FormatUsage()
    {
        HelpFormatter formatter = NewFormatter();
        formatter.AddUsage(null, _actions);
        return formatter.FormatHelp();
    }

    /// <summary>argparse's <c>error()</c>: the usage, the message, exit code 2.</summary>
    public ArgparseExit Error(string message)
        => new(2, stderr: FormatUsage() + _prog + ": error: " + message + "\n");

    private HelpFormatter NewFormatter() => new(_prog, width: _width);

    private string FormatVersion(Argument action)
    {
        HelpFormatter formatter = NewFormatter();
        formatter.AddText(action.Version);
        return formatter.FormatHelp();
    }

    // ===================================
    // Parsing
    // ===================================

    /// <summary><c>parse_args</c>: the namespace, or an <see cref="ArgparseExit"/>.</summary>
    public Namespace ParseArgs(IReadOnlyList<string> argv)
    {
        Namespace ns = ParseKnownArgs(argv, out List<string> extras);
        if (extras.Count > 0)
        {
            throw Error($"unrecognized arguments: {string.Join(" ", extras)}");
        }

        return ns;
    }

    /// <summary><c>_parse_known_args</c> without the intermixing of subparsers.</summary>
    private Namespace ParseKnownArgs(IReadOnlyList<string> argv, out List<string> extras)
    {
        var ns = new Namespace();

        // argparse: the defaults, unless the dest is suppressed.
        foreach (Argument action in _actions)
        {
            if (!action.SuppressDest && !ns.Has(action.Dest))
            {
                ns.Set(action.Dest, action.Default);
            }
        }

        // Build the pattern: 'O' for an option, 'A' for an argument, '-' for the '--' separator.
        var pattern = new StringBuilder();
        var optionIndices = new Dictionary<int, List<OptionTuple>>();
        for (int i = 0; i < argv.Count; i++)
        {
            string argString = argv[i];
            if (argString == "--")
            {
                pattern.Append('-');
                for (int j = i + 1; j < argv.Count; j++)
                {
                    pattern.Append('A');
                }

                break;
            }

            List<OptionTuple>? tuples = ParseOptional(argString);
            if (tuples is null)
            {
                pattern.Append('A');
            }
            else
            {
                optionIndices[i] = tuples;
                pattern.Append('O');
            }
        }

        string argPattern = pattern.ToString();
        var seenActions = new HashSet<Argument>();
        var positionals = _actions.Where(action => !action.IsOptional).ToList();
        var extraStrings = new List<string>();
        int startIndex = 0;
        int maxOptionIndex = optionIndices.Count > 0 ? optionIndices.Keys.Max() : -1;

        while (startIndex <= maxOptionIndex)
        {
            int nextOptionIndex = startIndex;
            while (nextOptionIndex <= maxOptionIndex && !optionIndices.ContainsKey(nextOptionIndex))
            {
                nextOptionIndex++;
            }

            if (startIndex != nextOptionIndex)
            {
                int positionalsEnd = ConsumePositionals(startIndex);
                if (positionalsEnd > startIndex)
                {
                    startIndex = positionalsEnd;
                    continue;
                }

                startIndex = positionalsEnd;
            }

            if (!optionIndices.ContainsKey(startIndex))
            {
                for (int i = startIndex; i < nextOptionIndex; i++)
                {
                    extraStrings.Add(argv[i]);
                }

                startIndex = nextOptionIndex;
            }

            startIndex = ConsumeOptional(startIndex);
        }

        int stopIndex = ConsumePositionals(startIndex);
        for (int i = stopIndex; i < argv.Count; i++)
        {
            extraStrings.Add(argv[i]);
        }

        var required = new List<string>();
        foreach (Argument action in _actions)
        {
            if (!seenActions.Contains(action) && action.Required)
            {
                required.Add(action.Name);
            }
        }

        if (required.Count > 0)
        {
            throw Error($"the following arguments are required: {string.Join(", ", required)}");
        }

        extras = extraStrings;
        return ns;

        // ---------------------------------------------------------------- the inner steps

        int ConsumePositionals(int start)
        {
            List<int> counts = MatchArgumentsPartial(positionals, argPattern[start..]);
            int index = start;
            for (int i = 0; i < counts.Count; i++)
            {
                Argument action = positionals[i];
                int count = counts[i];
                var args = new List<string>();
                for (int j = index; j < index + count; j++)
                {
                    args.Add(argv[j]);
                }

                if (count > 0 && argPattern.IndexOf('-', index, count) >= 0)
                {
                    args.Remove("--");
                }

                index += count;
                TakeAction(action, args, null);
            }

            positionals.RemoveRange(0, counts.Count);
            return index;
        }

        int ConsumeOptional(int start)
        {
            List<OptionTuple> tuples = optionIndices[start];
            if (tuples.Count > 1)
            {
                string options = string.Join(", ", tuples.Select(tuple => tuple.OptionString));
                throw Error($"ambiguous option: {argv[start]} could match {options}");
            }

            OptionTuple tuple = tuples[0];
            Argument? action = tuple.Action;
            string optionString = tuple.OptionString;
            string? sep = tuple.Sep;
            string? explicitArg = tuple.ExplicitArg;
            var actionTuples = new List<(Argument Action, List<string> Args, string OptionString)>();
            int stop;

            while (true)
            {
                if (action is null)
                {
                    extraStrings.Add(argv[start]);
                    return start + 1;
                }

                if (explicitArg is not null)
                {
                    int argCount = MatchArgument(action, "A");
                    if (argCount == 0 && optionString[1] != '-' && explicitArg.Length > 0)
                    {
                        if (sep is not null || explicitArg[0] == '-')
                        {
                            throw Error($"argument {action.Name}: ignored explicit argument " +
                                        $"{Repr(explicitArg)}");
                        }

                        actionTuples.Add((action, new List<string>(), optionString));
                        char c = optionString[0];
                        string candidate = c + explicitArg[..1];
                        if (_optionStringActions.TryGetValue(candidate, out Argument? next))
                        {
                            action = next;
                            optionString = candidate;
                            explicitArg = explicitArg[1..];
                            if (explicitArg.Length == 0)
                            {
                                sep = null;
                                explicitArg = null;
                            }
                            else if (explicitArg[0] == '=')
                            {
                                sep = "=";
                                explicitArg = explicitArg[1..];
                            }
                            else
                            {
                                sep = "";
                            }
                        }
                        else
                        {
                            extraStrings.Add(c + explicitArg);
                            stop = start + 1;
                            break;
                        }
                    }
                    else if (argCount == 1)
                    {
                        stop = start + 1;
                        actionTuples.Add((action, new List<string> { explicitArg }, optionString));
                        break;
                    }
                    else
                    {
                        throw Error($"argument {action.Name}: ignored explicit argument " +
                                    $"{Repr(explicitArg)}");
                    }
                }
                else
                {
                    int afterOption = start + 1;
                    int argCount = MatchArgument(action, argPattern[afterOption..]);
                    stop = afterOption + argCount;
                    var args = new List<string>();
                    for (int j = afterOption; j < stop; j++)
                    {
                        args.Add(argv[j]);
                    }

                    actionTuples.Add((action, args, optionString));
                    break;
                }
            }

            foreach ((Argument a, List<string> args, string optString) in actionTuples)
            {
                TakeAction(a, args, optString);
            }

            return stop;
        }

        void TakeAction(Argument action, List<string> args, string? optionString)
        {
            seenActions.Add(action);
            object? values = GetValues(action, args);
            Apply(action, values);
        }

        void Apply(Argument action, object? values)
        {
            switch (action.Action)
            {
                case ArgAction.Store:
                    ns.Set(action.Dest, values);
                    break;
                case ArgAction.StoreTrue:
                    ns.Set(action.Dest, true);
                    break;
                case ArgAction.Version:
                    throw new ArgparseExit(0, stdout: FormatVersion(action));
                case ArgAction.Help:
                    throw new ArgparseExit(0, stdout: FormatHelp());
                default:
                    throw new InvalidOperationException($"unhandled action {action.Action}");
            }
        }

        object? GetValues(Argument action, List<string> argStrings)
        {
            if (argStrings.Count == 1 && action.Nargs == Nargs.None)
            {
                object? value = GetValue(action, argStrings[0]);
                CheckValue(action, value);
                return value;
            }

            var values = new List<object?>();
            foreach (string argString in argStrings)
            {
                values.Add(GetValue(action, argString));
            }

            foreach (object? value in values)
            {
                CheckValue(action, value);
            }

            return values;
        }

        object? GetValue(Argument action, string argString)
        {
            try
            {
                return action.Type switch
                {
                    ArgType.Int => PyNumber.ParseInt(argString),
                    ArgType.Float => PyNumber.ParseFloat(argString),
                    ArgType.Path => PyPath.From(argString),
                    _ => argString,
                };
            }
            catch (FormatException)
            {
                throw Error($"argument {action.Name}: invalid {TypeName(action.Type)} value: " +
                            $"{Repr(argString)}");
            }
        }

        void CheckValue(Argument action, object? value)
        {
            if (action.Choices is null)
            {
                return;
            }

            string text = value switch
            {
                null => "None",
                string s => s,
                _ => value.ToString() ?? "",
            };
            if (!action.Choices.Contains(text))
            {
                string choices = string.Join(", ", action.Choices.Select(Repr));
                throw Error($"argument {action.Name}: invalid choice: {Repr(text)} " +
                            $"(choose from {choices})");
            }
        }
    }

    // ===================================
    // Pattern matching
    // ===================================

    /// <summary>argparse's <c>_match_argument</c>.</summary>
    private int MatchArgument(Argument action, string pattern)
    {
        Match match = Regex.Match(pattern, "^" + NargsPattern(action));
        if (!match.Success)
        {
            throw Error($"argument {action.Name}: {NargsError(action.Nargs)}");
        }

        return match.Groups[1].Length;
    }

    /// <summary>argparse's <c>_match_arguments_partial</c> (a run of positionals).</summary>
    private List<int> MatchArgumentsPartial(List<Argument> actions, string pattern)
    {
        for (int i = actions.Count; i > 0; i--)
        {
            string combined = string.Concat(actions.Take(i).Select(NargsPattern));
            Match match = Regex.Match(pattern, "^" + combined);
            if (match.Success)
            {
                var counts = new List<int>();
                for (int g = 1; g < match.Groups.Count; g++)
                {
                    counts.Add(match.Groups[g].Length);
                }

                return counts;
            }
        }

        return new List<int>();
    }

    /// <summary>argparse's <c>_get_nargs_pattern</c> for the nargs values the CLI uses.</summary>
    private static string NargsPattern(Argument action)
    {
        if (action.IsOptional)
        {
            return action.Nargs switch
            {
                Nargs.None => "([A])",
                Nargs.Zero => "()",
                Nargs.OneOrMore => "(A[A]*)",
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }

        return action.Nargs switch
        {
            Nargs.None => "(-*A-*)",
            Nargs.Zero => "(-*)",
            Nargs.OneOrMore => "(-*A[A-]*)",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static string NargsError(Nargs nargs) => nargs switch
    {
        Nargs.None => "expected one argument",
        Nargs.Zero => "expected 0 arguments",
        Nargs.OneOrMore => "expected at least one argument",
        _ => "expected 1 argument",
    };

    private static string TypeName(ArgType type) => type switch
    {
        ArgType.Int => "int",
        ArgType.Float => "float",
        ArgType.Path => "Path",
        _ => "str",
    };

    // ===================================
    // Option recognition
    // ===================================

    /// <summary>argparse's <c>_parse_optional</c>.</summary>
    private List<OptionTuple>? ParseOptional(string argString)
    {
        if (argString.Length == 0)
        {
            return null;
        }

        if (argString[0] != '-')
        {
            return null;
        }

        if (_optionStringActions.TryGetValue(argString, out Argument? exact))
        {
            return [new OptionTuple(exact, argString, null, null)];
        }

        if (argString.Length == 1)
        {
            return null;
        }

        int equals = argString.IndexOf('=');
        if (equals >= 0)
        {
            string before = argString[..equals];
            if (_optionStringActions.TryGetValue(before, out Argument? withEquals))
            {
                return [new OptionTuple(withEquals, before, "=", argString[(equals + 1)..])];
            }
        }

        List<OptionTuple> tuples = GetOptionTuples(argString);
        if (tuples.Count > 0)
        {
            return tuples;
        }

        // A negative number looks like an option only when no option is one.
        if (NegativeNumber.IsMatch(argString) && !_hasNegativeNumberOptionals)
        {
            return null;
        }

        // Anything else that is not an exact match and has no spaces: an unknown option.
        if (argString.Contains(' '))
        {
            return null;
        }

        return [new OptionTuple(null, argString, null, null)];
    }

    /// <summary>argparse's <c>_get_option_tuples</c>: the matches of an abbreviated option.</summary>
    private List<OptionTuple> GetOptionTuples(string optionString)
    {
        var result = new List<OptionTuple>();
        int equals = optionString.IndexOf('=');
        string prefix = equals < 0 ? optionString : optionString[..equals];
        string? sep = equals < 0 ? null : "=";
        string? explicitArg = equals < 0 ? null : optionString[(equals + 1)..];

        if (optionString.Length > 1 && optionString[0] == '-' && optionString[1] == '-')
        {
            foreach (string name in _optionStrings)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result.Add(new OptionTuple(_optionStringActions[name], name, sep, explicitArg));
                }
            }

            return result;
        }

        // A single dash: a cluster of short options, or a short option with its value.
        string shortPrefix = optionString[..2];
        string shortExplicit = optionString[2..];
        foreach (string name in _optionStrings)
        {
            if (name == shortPrefix)
            {
                result.Add(new OptionTuple(_optionStringActions[name], name, "", shortExplicit));
            }
            else if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Add(new OptionTuple(_optionStringActions[name], name, sep, explicitArg));
            }
        }

        return result;
    }

    /// <summary>Python's <c>repr()</c> of a string (the wording of argparse's messages).</summary>
    private static string Repr(string value)
    {
        char quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(quote);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c == quote)
                    {
                        sb.Append('\\').Append(c);
                    }
                    else if (c < 0x20 || c == 0x7f)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append(quote).ToString();
    }

    private sealed record OptionTuple(Argument? Action, string OptionString, string? Sep,
                                      string? ExplicitArg);
}

/// <summary><c>add_argument_group</c>: a titled group of arguments (a help section).</summary>
public sealed class ArgumentGroup
{
    private readonly ArgumentParser _parser;

    internal ArgumentGroup(ArgumentParser parser, string title)
    {
        _parser = parser;
        Title = title;
    }

    public string Title { get; }

    internal List<Argument> Actions { get; } = new();

    internal void Add(Argument action) => Actions.Add(action);

    /// <summary><c>group.add_argument(...)</c>.</summary>
    public Argument AddArgument(
        string[] optionStrings,
        string? dest = null,
        Nargs nargs = Nargs.None,
        ArgType type = ArgType.Str,
        string[]? choices = null,
        object? @default = null,
        ArgAction action = ArgAction.Store,
        string? help = null,
        string? version = null,
        string? metavar = null)
        => _parser.AddArgumentIn(this, optionStrings, dest, nargs, type, choices, @default, action,
                                 help, version, metavar);
}
