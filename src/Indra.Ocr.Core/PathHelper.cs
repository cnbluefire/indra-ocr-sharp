using System.Runtime.InteropServices;

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
/// trailing slash disappears; the port normalizes the same way.
/// <para>
/// Which spelling that is is <c>pathlib</c>'s choice and not the port's: python picks the flavour
/// from <c>os.name</c>, so one and the same reference run prints <c>a\b</c> on Windows and
/// <c>a/b</c> anywhere else — and on POSIX a backslash is an ordinary character in a name.
/// <see cref="PathHelper"/> picks its <see cref="PathFlavour"/> the same way, so the hosts'
/// messages, their output file names and the JSON's <c>file</c> field are spelled the way the
/// reference spells them on the machine the binary runs on.
/// </para>
/// <para>
/// The Windows flavour: <c>altsep</c> to <c>sep</c>, empty and <c>.</c> components dropped,
/// <c>..</c> kept, a drive or a UNC share kept in front. The POSIX flavour: <c>/</c> and nothing
/// else separates, empty and <c>.</c> components dropped, <c>..</c> kept, a leading <c>/</c> (or a
/// doubled <c>//</c>) kept. <c>.stem</c> is python 3.14's: the final component minus its last
/// suffix, with a stem that has to keep at least one non-dot character (<c>c.</c> is <c>c</c>,
/// <c>.gitignore</c> has no suffix at all).
/// </para>
/// </remarks>
public static class PathHelper
{
    /// <summary>The flavour this host's <c>pathlib</c> would pick (<c>os.name: nt</c> or not).</summary>
    private static readonly PathFlavour Host =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? PathFlavour.Windows : PathFlavour.Posix;

    /// <summary>The normalized path (<c>str(Path(s))</c>; <c>"."</c> for the empty string).</summary>
    public static string Normalize(string path) => Normalize(path, Host);

    /// <inheritdoc cref="Normalize(string)"/>
    internal static string Normalize(string path, PathFlavour flavour)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        (string drive, string root, List<string> parts) = Parse(path, flavour);
        string result = drive + root + string.Join(Separator(flavour), parts);
        return result.Length == 0 ? "." : result;
    }

    /// <summary>
    /// <c>Path(s).expanduser()</c>: <c>~</c> becomes the home directory — <c>%USERPROFILE%</c> on
    /// Windows, <c>$HOME</c> (and then the passwd entry) on POSIX — while <c>~user</c> is left
    /// alone (ntpath does not look users up, and the posix <c>pwd</c> lookup is not ported). The
    /// result is normalized.
    /// </summary>
    public static string ExpandUser(string path) => ExpandUser(path, Host, Home(Host));

    /// <inheritdoc cref="ExpandUser(string)"/>
    internal static string ExpandUser(string path, PathFlavour flavour) => ExpandUser(path, flavour, Home(flavour));

    /// <summary><see cref="ExpandUser(string)"/> with the home directory handed in instead of read.</summary>
    internal static string ExpandUser(string path, PathFlavour flavour, string? home)
    {
        string text = Normalize(path, flavour);
        if (!text.StartsWith('~'))
        {
            return text;
        }

        int separator = text.IndexOf(flavour == PathFlavour.Windows ? '\\' : '/', 1);
        if (separator < 0)
        {
            separator = text.Length;
        }

        if (separator != 1 || string.IsNullOrEmpty(home))
        {
            return text;
        }

        return Normalize(home + text[1..], flavour);
    }

    /// <summary>
    /// <c>Path.stem</c>: the last component without its last suffix. A name that is all suffix
    /// (<c>.gitignore</c>) or whose stem would be all dots (<c>..</c>) keeps its name.
    /// </summary>
    public static string Stem(string path) => Stem(path, Host);

    /// <inheritdoc cref="Stem(string)"/>
    internal static string Stem(string path, PathFlavour flavour)
    {
        string name = Name(path, flavour);
        int dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return name;
        }

        string stem = name[..dot];
        return stem.TrimStart('.').Length > 0 ? stem : name;
    }

    /// <summary><c>Path(left) / right</c> (the right side wins when it has a drive or a root).</summary>
    public static string Join(string left, string right) => Join(left, right, Host);

    /// <inheritdoc cref="Join(string, string)"/>
    internal static string Join(string left, string right, PathFlavour flavour)
    {
        string normalized = Normalize(right, flavour);
        if (PrefixLength(normalized, flavour) > 0)
        {
            return normalized;
        }

        return Normalize(left + Separator(flavour) + right, flavour);
    }

    /// <summary>The last component (<c>Path.name</c>; empty when the path is only a drive).</summary>
    private static string Name(string path, PathFlavour flavour)
    {
        string text = Normalize(path, flavour);
        int start = PrefixLength(text, flavour);
        int end = text.Length;
        while (end > start && IsSeparator(text[end - 1], flavour))
        {
            end--;
        }

        int begin = end;
        while (begin > start && !IsSeparator(text[begin - 1], flavour))
        {
            begin--;
        }

        return text[begin..end];
    }

    /// <summary>The separator the flavour's <c>str()</c> joins with (pathlib's <c>sep</c>).</summary>
    private static string Separator(PathFlavour flavour)
        => flavour == PathFlavour.Windows ? "\\" : "/";

    private static bool IsSeparator(char c, PathFlavour flavour)
        => c == '/' || (flavour == PathFlavour.Windows && c == '\\');

    private static bool IsDriveLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static int PrefixLength(string path, PathFlavour flavour)
    {
        (string drive, string root, _) = Parse(path, flavour);
        return drive.Length + root.Length;
    }

    /// <summary>The home directory <c>expanduser</c> reads for <c>~</c> in one flavour.</summary>
    /// <remarks>
    /// <c>ntpath.expanduser</c> reads <c>USERPROFILE</c> (its <c>HOMEDRIVE</c>+<c>HOMEPATH</c>
    /// fallback is not ported); <c>posixpath.expanduser</c> reads <c>HOME</c> and then the passwd
    /// entry, which is what <c>GetFolderPath</c> answers with on POSIX.
    /// </remarks>
    private static string? Home(PathFlavour flavour) => flavour == PathFlavour.Windows
        ? Environment.GetEnvironmentVariable("USERPROFILE")
        : Environment.GetEnvironmentVariable("HOME")
          ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>pathlib's <c>_Flavour.parse_parts</c> for a single string, in one flavour.</summary>
    private static (string Drive, string Root, List<string> Parts) Parse(string path, PathFlavour flavour)
        => flavour == PathFlavour.Windows ? ParseWindows(path) : ParsePosix(path);

    /// <summary>pathlib's Windows parse: <c>/</c> is an altsep, and drives and UNC shares anchor.</summary>
    private static (string Drive, string Root, List<string> Parts) ParseWindows(string path)
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

    /// <summary>pathlib's POSIX parse: <c>/</c> separates, and <c>//</c> is a root of its own.</summary>
    /// <remarks>
    /// <c>PurePosixPath</c> has no drive and no altsep: a backslash and a <c>C:</c> are ordinary
    /// characters. <c>posixpath.splitroot</c> keeps a doubled leading slash (<c>//a</c>) and folds
    /// three or more into one (<c>///a</c> is <c>/a</c>).
    /// </remarks>
    private static (string Drive, string Root, List<string> Parts) ParsePosix(string path)
    {
        string root = "";
        int i = 0;
        if (path.StartsWith('/'))
        {
            if (path.Length > 1 && path[1] == '/' && (path.Length == 2 || path[2] != '/'))
            {
                root = "//";
                i = 2;
            }
            else
            {
                root = "/";
                i = 1;
            }
        }

        var parts = new List<string>();
        foreach (string part in path[i..].Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            parts.Add(part);
        }

        return ("", root, parts);
    }
}

/// <summary>
/// The <c>pathlib</c> flavour a path is spelled in — python's own choice is the host's
/// (<c>os.name</c>), and the port offers the other one so both are testable on either host.
/// </summary>
internal enum PathFlavour
{
    /// <summary><c>PureWindowsPath</c>: <c>\</c> separates, <c>/</c> is an altsep, drives and UNC.</summary>
    Windows,

    /// <summary><c>PurePosixPath</c>: <c>/</c> separates and nothing else does.</summary>
    Posix,
}
