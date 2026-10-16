namespace Indra.Ocr;

/// <summary>
/// Python's <c>pathlib.Path</c> as far as the port shows it: the normalized string
/// (<c>str(Path(s))</c> — the reference puts it in messages and in the JSON's <c>file</c> field),
/// <c>.stem</c> (the CLI's <c>--out</c> file name), <c>Path(...) / name</c> and
/// <c>expanduser()</c>.
/// </summary>
/// <remarks>
/// A path is a plain <see cref="string"/> in the port; this is the one place that knows the Python
/// spelling of one. <c>--out</c>, <c>--model-dir</c> and the inputs are all <c>type=Path</c> in the
/// reference, so a path typed with forward slashes comes back with backslashes on Windows and a
/// trailing slash disappears; the port normalizes the same way. The rules are <c>pathlib</c>'s
/// Windows flavour: <c>altsep</c> to <c>sep</c>, empty and <c>.</c> components dropped, <c>..</c>
/// kept, a drive or a root kept in front.
/// </remarks>
public static class PathHelper
{
    /// <summary>The normalized path (<c>str(Path(s))</c>; <c>"."</c> for the empty string).</summary>
    public static string Normalize(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        (string drive, string root, List<string> parts) = Parse(path);
        string result = drive + root + string.Join("\\", parts);
        return result.Length == 0 ? "." : result;
    }

    /// <summary>
    /// <c>Path(s).expanduser()</c> on Windows: <c>~</c> becomes <c>%USERPROFILE%</c>; <c>~user</c>
    /// is left alone (ntpath.expanduser does not look users up). The result is normalized.
    /// </summary>
    public static string ExpandUser(string path)
    {
        string text = Normalize(path);
        if (!text.StartsWith('~'))
        {
            return text;
        }

        int separator = text.IndexOf('\\', 1);
        if (separator < 0)
        {
            separator = text.Length;
        }

        if (separator != 1)
        {
            return text;
        }

        string? home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(home))
        {
            return text;
        }

        return Normalize(home + text[1..]);
    }

    /// <summary>
    /// <c>Path.stem</c>: the last component without its last suffix. A name that is all suffix
    /// (<c>.gitignore</c>) or ends in a dot keeps it, as pathlib does.
    /// </summary>
    public static string Stem(string path)
    {
        string name = Name(path);
        int dot = name.LastIndexOf('.');
        return 0 < dot && dot < name.Length - 1 ? name[..dot] : name;
    }

    /// <summary><c>Path(left) / right</c> (the right side wins when it has a drive or a root).</summary>
    public static string Join(string left, string right)
    {
        string normalized = Normalize(right);
        if (PrefixLength(normalized) > 0)
        {
            return normalized;
        }

        return Normalize(left + "\\" + right);
    }

    /// <summary>The last component (<c>Path.name</c>; empty when the path is only a drive).</summary>
    private static string Name(string path)
    {
        string text = Normalize(path);
        int start = PrefixLength(text);
        int end = text.Length;
        while (end > start && IsSeparator(text[end - 1]))
        {
            end--;
        }

        int begin = end;
        while (begin > start && !IsSeparator(text[begin - 1]))
        {
            begin--;
        }

        return text[begin..end];
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static bool IsDriveLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static int PrefixLength(string path)
    {
        (string drive, string root, _) = Parse(path);
        return drive.Length + root.Length;
    }

    /// <summary>pathlib's <c>_Flavour.parse_parts</c> for a single string, on Windows.</summary>
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
