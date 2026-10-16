// The argument model of the argparse port (see ArgumentParser.cs for why this exists).
//
// The names follow CPython's Lib/argparse.py — Argument, Namespace, ArgumentError — so the port
// can be read against the reference.

using Indra.Ocr.Py;

namespace Indra.Ocr.Cli.Argparse;

/// <summary>argparse's <c>nargs</c> (the three the CLI uses).</summary>
public enum Nargs
{
    /// <summary><c>None</c>: exactly one value.</summary>
    None,

    /// <summary><c>0</c>: no value (<c>store_true</c>, <c>version</c>, <c>help</c>).</summary>
    Zero,

    /// <summary><c>'+'</c>: one or more values (the positionals).</summary>
    OneOrMore,
}

/// <summary>argparse's <c>action=</c> (the four the CLI uses).</summary>
public enum ArgAction
{
    Store,
    StoreTrue,
    Version,
    Help,
}

/// <summary>argparse's <c>type=</c> (the four the CLI uses).</summary>
public enum ArgType
{
    Str,
    Int,
    Float,
    Path,
}

/// <summary>One command-line argument: argparse's <c>Action</c>.</summary>
public sealed class Argument
{
    internal Argument(
        IReadOnlyList<string> optionStrings,
        string dest,
        Nargs nargs,
        ArgAction action,
        ArgType type,
        IReadOnlyList<string>? choices,
        object? @default,
        string? help,
        string? version,
        string? metavar)
    {
        OptionStrings = optionStrings;
        Dest = dest;
        Nargs = nargs;
        Action = action;
        Type = type;
        Choices = choices;
        Default = @default;
        Help = help;
        Version = version;
        Metavar = metavar;
    }

    /// <summary>argparse's <c>option_strings</c>: empty for a positional.</summary>
    public IReadOnlyList<string> OptionStrings { get; }

    /// <summary>argparse's <c>dest</c>: where the value lands in the namespace.</summary>
    public string Dest { get; }

    public Nargs Nargs { get; }

    public ArgAction Action { get; }

    public ArgType Type { get; }

    /// <summary>The allowed values, or <c>null</c>.</summary>
    public IReadOnlyList<string>? Choices { get; }

    /// <summary>The value when the argument is absent (<c>null</c> is Python's <c>None</c>).</summary>
    public object? Default { get; }

    public string? Help { get; }

    /// <summary>The text <c>action="version"</c> prints.</summary>
    public string? Version { get; }

    public string? Metavar { get; }

    /// <summary>Positionals are required unless <c>nargs</c> allows none.</summary>
    public bool Required { get; internal set; }

    /// <summary><c>dest=SUPPRESS</c>: <c>--help</c> and <c>--version</c> leave nothing behind.</summary>
    public bool SuppressDest { get; internal set; }

    public bool IsOptional => OptionStrings.Count > 0;

    /// <summary>argparse's <c>Action.format_usage</c>: the first way to spell the option.</summary>
    public string FormatUsage() => OptionStrings[0];

    /// <summary>argparse's <c>_get_action_name</c>: how the argument is named in messages.</summary>
    public string Name => IsOptional ? string.Join("/", OptionStrings) : Metavar ?? Dest;
}

/// <summary>
/// argparse's <c>Namespace</c>: what the parse produced, read back by name.
/// </summary>
public sealed class Namespace
{
    private readonly Dictionary<string, object?> _values = new();

    internal bool Has(string dest) => _values.ContainsKey(dest);

    internal void Set(string dest, object? value) => _values[dest] = value;

    /// <summary>The value, or <c>null</c> (Python's <c>None</c>) when it was never set.</summary>
    public object? Get(string dest) => _values.TryGetValue(dest, out object? value) ? value : null;

    public string? Str(string dest) => Get(dest) as string;

    public bool Flag(string dest) => Get(dest) is true;

    /// <summary>An <c>int</c> argument, saturating at <see cref="int"/> (Python's ints are unbounded).</summary>
    public int Int(string dest)
    {
        long value = Long(dest);
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }

    /// <summary>An <c>int</c> argument (clamped to <see cref="long"/> while parsing).</summary>
    public long Long(string dest) => Get(dest) is long value ? value : 0L;

    public double Float(string dest) => Get(dest) is double value ? value : 0.0;

    /// <summary>The value of a <c>type=Path</c> argument, or <c>null</c>.</summary>
    public PyPath? PathOrNull(string dest) => Get(dest) as PyPath?;

    /// <summary>The values of a <c>type=Path</c>, <c>nargs='+'</c> argument.</summary>
    public List<PyPath> PathList(string dest)
    {
        var paths = new List<PyPath>();
        if (Get(dest) is List<object?> values)
        {
            foreach (object? value in values)
            {
                if (value is PyPath path)
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }
}

/// <summary>
/// argparse's <c>ArgumentError</c>: an error the parser turns into
/// <c>usage: …</c> + <c>prog: error: …</c> and exit code 2.
/// </summary>
public sealed class ArgumentError : Exception
{
    public ArgumentError(Argument? action, string message) : base(message) => Action = action;

    /// <summary>The argument the message is about, when it is about one.</summary>
    public Argument? Action { get; }
}

/// <summary>
/// argparse's <c>exit()</c>: <c>--help</c> and <c>--version</c> leave with 0 after writing to
/// stdout, and an error leaves with 2 after writing the usage and the message to stderr. The
/// caller writes the text (so it goes through the CLI's own encoding and newline handling).
/// </summary>
public sealed class ArgparseExit : Exception
{
    public ArgparseExit(int status, string stdout = "", string stderr = "")
        : base(stderr.Length > 0 ? stderr : stdout)
    {
        Status = status;
        Stdout = stdout;
        Stderr = stderr;
    }

    public int Status { get; }

    /// <summary>What argparse wrote to stdout (the help or the version), if anything.</summary>
    public string Stdout { get; }

    /// <summary>What argparse wrote to stderr (the usage and the error), if anything.</summary>
    public string Stderr { get; }
}
