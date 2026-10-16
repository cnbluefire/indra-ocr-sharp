namespace Indra.Ocr.Cli.Py;

/// <summary>
/// Python's <c>pathlib.Path</c> as far as the command line shows it: <c>str()</c> (the parsed,
/// normalized form — the reference puts it in messages and in the JSON's <c>file</c> field) and
/// <c>.stem</c> (the <c>--out</c> file name), plus <c>~/</c> expansion for the model folder.
/// </summary>
/// <remarks>
/// <c>--out</c>, <c>--model-dir</c> and the inputs are all <c>type=Path</c> in the reference, so a
/// path typed with forward slashes comes back with backslashes on Windows and a trailing slash
/// disappears; the port normalizes the same way. The rules are <c>pathlib</c>'s Windows flavour:
/// <c>altsep</c> to <c>sep</c>, empty and <c>.</c> components dropped, <c>..</c> kept, a drive or
/// a root kept in front.
/// </remarks>
public readonly struct PyPath : IEquatable<PyPath>
{
    private readonly string? _path;

    private PyPath(string path) => _path = path;

    /// <summary>The normalized path (<c>str(Path(s))</c>; <c>"."</c> for the empty string).</summary>
    public override string ToString() => _path ?? ".";

    public bool Equals(PyPath other) => ToString() == other.ToString();

    public override bool Equals(object? obj) => obj is PyPath other && Equals(other);

    public override int GetHashCode() => ToString().GetHashCode();

    /// <summary><c>Path(s)</c>.</summary>
    public static PyPath From(string path) => new(Normalize(path));

    /// <summary><c>Path(...) / name</c> (the right side wins when it has a drive or a root).</summary>
    public PyPath Join(string name)
    {
        string right = Normalize(name);
        (string drive, string root, _) = Parse(right);
        if (drive.Length > 0 || root.Length > 0)
        {
            return new PyPath(right);
        }

        return new PyPath(Normalize(ToString() + "\\" + name));
    }

    /// <summary>The path's own string, or <paramref name="fallback"/> for the default <c>None</c>.</summary>
    public string Or(string fallback) => _path ?? fallback;

    /// <summary>
    /// <c>Path.stem</c>: the last component without its last suffix. A name that is all suffix
    /// (<c>.gitignore</c>) or ends in a dot keeps it, as pathlib does.
    /// </summary>
    public string Stem
    {
        get
        {
            string name = Name;
            int dot = name.LastIndexOf('.');
            return 0 < dot && dot < name.Length - 1 ? name[..dot] : name;
        }
    }

    /// <summary>The last component (<c>Path.name</c>; empty when the path is only a drive).</summary>
    public string Name
    {
        get
        {
            string path = ToString();
            int start = PrefixLength(path);
            int end = path.Length;
            while (end > start && IsSeparator(path[end - 1]))
            {
                end--;
            }

            int begin = end;
            while (begin > start && !IsSeparator(path[begin - 1]))
            {
                begin--;
            }

            return path[begin..end];
        }
    }

    /// <summary>
    /// <c>Path.expanduser()</c> on Windows: <c>~</c> becomes <c>%USERPROFILE%</c>; <c>~user</c>
    /// is left alone (ntpath.expanduser does not look users up).
    /// </summary>
    public PyPath ExpandUser()
    {
        string path = ToString();
        if (!path.StartsWith('~'))
        {
            return this;
        }

        int separator = path.IndexOf('\\', 1);
        if (separator < 0)
        {
            separator = path.Length;
        }

        if (separator != 1)
        {
            return this;
        }

        string? home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(home))
        {
            return this;
        }

        return new PyPath(Normalize(home + path[1..]));
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static bool IsDriveLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static int PrefixLength(string path)
    {
        (string drive, string root, _) = Parse(path);
        return drive.Length + root.Length;
    }

    /// <summary>pathlib's <c>_Flavour.parse_parts</c> for a single string, on Windows.</summary>
    private static string Normalize(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        (string drive, string root, List<string> parts) = Parse(path);
        string result = drive + root + string.Join("\\", parts);
        return result.Length == 0 ? "." : result;
    }

    private static (string Drive, string Root, List<string> Parts) Parse(string path)
    {
        string text = path.Replace('/', '\\');
        string drive = "";
        string root = "";
        int i = 0;
        if (text.StartsWith(@"\\"))
        {
            // A UNC path: \\server\share is the drive, the separator after it the root.
            int server = text.IndexOf('\\', 2);
            if (server > 2)
            {
                int share = text.IndexOf('\\', server + 1);
                int end = share < 0 ? text.Length : share;
                drive = text[..end];
                root = share < 0 ? "" : "\\";
                i = end;
            }
            else
            {
                root = "\\";
            }
        }
        else if (text.Length >= 2 && text[1] == ':' && IsDriveLetter(text[0]))
        {
            drive = text[..2];
            i = 2;
            if (i < text.Length && text[i] == '\\')
            {
                root = "\\";
                i++;
            }
        }
        else if (text[0] == '\\')
        {
            root = "\\";
            i = 1;
        }

        var parts = new List<string>();
        foreach (string part in text[i..].Split('\\'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            parts.Add(part);
        }

        return (drive, root, parts);
    }
}
