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
/// The rules are python 3.14's, and they are structural rather than textual: a spelling is parsed
/// into pathlib's <c>(drive, root, parts)</c> — <c>ntpath.splitroot</c> / <c>posixpath.splitroot</c>
/// plus <c>PurePath._parse_path</c>'s "a rooted drive with no root of its own is <c>\\server\share</c>
/// and takes one" rule — and every answer is built from that triple. So <c>str()</c> is
/// <c>_format_parsed_parts</c> (<c>drive + root + sep.join(parts)</c>, or <c>.</c> when it is empty),
/// <c>.name</c>/<c>.stem</c> read the last <em>part</em> (a path that is only a drive or a root has
/// none: <c>Path(".")</c>, <c>Path("C:\\")</c> and <c>Path("\\\\srv~")</c> all have an empty stem),
/// and <c>Path(a) / b</c> is <c>ntpath.join</c>/<c>posixpath.join</c> over the two <em>raw</em>
/// strings followed by that parse — not a concatenation: <c>Path("") / "b"</c> is <c>b</c> and
/// <c>Path("C:") / "b"</c> is <c>C:b</c>, because <c>ntpath.join</c> inserts its separator only
/// where it owns one.
/// </para>
/// <para>
/// The Windows thinking this reproduces: <c>/</c> is an altsep replaced before the parse, a drive or
/// a UNC share anchors, and the degenerate shapes follow from the slicing in <c>ntpath.splitroot</c>
/// rather than from a special case here (<c>\\</c> is a drive with no root, <c>\\srv..a\\</c> keeps
/// the share's separator, an empty server is the drive <c>\\</c>). See
/// <c>docs/370-path-flavour.md</c> and <c>docs/375-path-parity.md</c>.
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
    internal static string Normalize(string path, PathFlavour flavour) => Format(Parse(path, flavour), flavour);

    /// <summary>
    /// <c>Path(s).expanduser()</c>: the first component of a relative path becomes the home
    /// directory when it is <c>~</c> — <c>%USERPROFILE%</c> on Windows, <c>$HOME</c> (and then the
    /// passwd entry) on POSIX. <c>~user</c> is left alone: ntpath's lookup raises
    /// <c>RuntimeError</c> when the user is not found there, and the port does not look users up.
    /// The result is normalized.
    /// </summary>
    public static string ExpandUser(string path) => ExpandUser(path, Host, Home(Host));

    /// <inheritdoc cref="ExpandUser(string)"/>
    internal static string ExpandUser(string path, PathFlavour flavour) => ExpandUser(path, flavour, Home(flavour));

    /// <summary><see cref="ExpandUser(string)"/> with the home directory handed in instead of read.</summary>
    internal static string ExpandUser(string path, PathFlavour flavour, string? home)
    {
        (string drive, string root, List<string> parts) = Parse(path, flavour);
        // `home is null` before the length check: netstandard2.0's string.IsNullOrEmpty carries no
        // NotNullWhen, so the nullable flow would not be known there.
        if (drive.Length > 0 || root.Length > 0 || parts.Count == 0 || parts[0] != "~" ||
            home is null || home.Length == 0)
        {
            return Normalize(path, flavour);
        }

        // pathlib expands the first *component* (a component holds no separator, so "~user" is the
        // only other thing it can be), parses what came back and appends the rest of the tail.
        (string homeDrive, string homeRoot, List<string> homeParts) = Parse(home, flavour);
        for (int i = 1; i < parts.Count; i++)
        {
            homeParts.Add(parts[i]);
        }

        return Format((homeDrive, homeRoot, homeParts), flavour);
    }

    /// <summary>
    /// <c>Path.stem</c>: the last component without its last suffix. A name that is all suffix
    /// (<c>.gitignore</c>) or whose stem would be all dots (<c>..</c>) keeps its name, and a path
    /// with no component at all (<c>.</c>, <c>/</c>, <c>C:\</c>, <c>\\srv</c>) has none.
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
        => Normalize(JoinRaw(left, right, flavour), flavour);

    /// <summary>The last component (<c>Path.name</c>; empty when the path has no components).</summary>
    private static string Name(string path, PathFlavour flavour)
    {
        (_, _, List<string> parts) = Parse(path, flavour);
        return parts.Count == 0 ? "" : parts[^1];
    }

    /// <summary>
    /// pathlib's <c>_format_parsed_parts</c> as <c>str()</c> uses it: the drive, the root and the
    /// components back in one spelling, <c>.</c> when there is nothing to spell.
    /// </summary>
    private static string Format((string Drive, string Root, List<string> Parts) parsed, PathFlavour flavour)
    {
        (string drive, string root, List<string> parts) = parsed;
        if (drive.Length > 0 || root.Length > 0)
        {
            return drive + root + string.Join(Separator(flavour), parts);
        }

        if (parts.Count > 0 && SplitRoot(parts[0], flavour).Drive.Length > 0)
        {
            // A first component that carries a drive of its own — `Path("a") / "C:b"` keeps it as a
            // component — is written with a leading ".", so it cannot be read back as a drive.
            parts = new List<string> { "." };
            parts.AddRange(parsed.Parts);
        }

        string text = string.Join(Separator(flavour), parts);
        return text.Length == 0 ? "." : text;
    }

    /// <summary>
    /// pathlib's <c>_parse_path</c>: a spelling into its <c>(drive, root, parts)</c> triple.
    /// </summary>
    private static (string Drive, string Root, List<string> Parts) Parse(string path, PathFlavour flavour)
    {
        if (path.Length == 0)
        {
            return ("", "", new List<string>());
        }

        string text = flavour == PathFlavour.Windows ? path.Replace('/', '\\') : path;
        (string drive, string root, string tail) = SplitRoot(text, flavour);
        if (root.Length == 0 && drive.Length > 0 && drive[0] == SeparatorChar(flavour) &&
            drive[^1] != SeparatorChar(flavour))
        {
            string[] driveParts = drive.Split(SeparatorChar(flavour));
            // `//server/share` is a UNC path and `//?/unc/server/share` a device form of one, so
            // both take the root that splitroot could not see; `//./device` and `//?/device` do not.
            // The membership test is python's `drv_parts[2] not in '?.'`, a *substring* test: an
            // empty component (`\\\server` splits into `['', '', '', 'server']`) counts as one of
            // them too, and so does the two-character `?.`.
            bool device = "?.".IndexOf(driveParts[2], StringComparison.Ordinal) >= 0;
            if ((driveParts.Length == 4 && !device) || driveParts.Length == 6)
            {
                root = Separator(flavour);
            }
        }

        var parts = new List<string>();
        foreach (string part in tail.Split(SeparatorChar(flavour)))
        {
            if (part.Length > 0 && part != ".")
            {
                parts.Add(part);
            }
        }

        return (drive, root, parts);
    }

    /// <summary><c>ntpath.splitroot</c> and <c>posixpath.splitroot</c>: drive, root and tail.</summary>
    /// <remarks>
    /// The Windows rule (ntpath) replaces <c>/</c> with <c>\</c> for its <em>checks</em> only and
    /// slices the string it was handed, so the caller gets its own separators back. pathlib's
    /// <c>_parse_path</c> has replaced them before calling; <c>ntpath.join</c> has not, and that is
    /// why <c>ntpath.join("a/b", "c")</c> keeps the forward slash.
    /// </remarks>
    private static (string Drive, string Root, string Tail) SplitRoot(string path, PathFlavour flavour)
    {
        if (flavour == PathFlavour.Posix)
        {
            if (path.Length == 0 || path[0] != '/')
            {
                return ("", "", path);
            }

            if (path.Length == 1 || path[1] != '/' || (path.Length > 2 && path[2] == '/'))
            {
                return ("", "/", path[1..]);
            }

            // Precisely two leading slashes are a root of their own (POSIX leaves it up to the
            // implementation, and python keeps them).
            return ("", "//", path[2..]);
        }

        string normalized = path.Replace('/', '\\');
        if (normalized.Length > 0 && normalized[0] == '\\')
        {
            if (normalized.Length > 1 && normalized[1] == '\\')
            {
                // UNC and device drives: the share, or `\\?\UNC\server\share`, is the drive and the
                // separator after it the root — a shape with only one separator has neither.
                int start = normalized.Length >= 8 &&
                            string.Equals(normalized[..8], @"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                    ? 8
                    : 2;
                int index = normalized.IndexOf('\\', start);
                if (index < 0)
                {
                    return (path, "", "");
                }

                int share = normalized.IndexOf('\\', index + 1);
                if (share < 0)
                {
                    return (path, "", "");
                }

                return (path[..share], path[share..(share + 1)], path[(share + 1)..]);
            }

            return ("", path[..1], path[1..]);
        }

        if (normalized.Length > 1 && normalized[1] == ':')
        {
            if (normalized.Length > 2 && normalized[2] == '\\')
            {
                return (path[..2], path[2..3], path[3..]);
            }

            return (path[..2], "", path[2..]);
        }

        return ("", "", path);
    }

    /// <summary><c>ntpath.join</c> and <c>posixpath.join</c>: two raw spellings joined.</summary>
    private static string JoinRaw(string left, string right, PathFlavour flavour)
    {
        if (flavour == PathFlavour.Posix)
        {
            if (right.StartsWith('/') || left.Length == 0)
            {
                return right;
            }

            return left.EndsWith('/') ? left + right : left + "/" + right;
        }

        (string drive, string root, string tail) = SplitRoot(left, flavour);
        (string rightDrive, string rightRoot, string rightTail) = SplitRoot(right, flavour);
        if (rightRoot.Length > 0)
        {
            // A rooted right side is taken as it is, and keeps its drive when the left has none.
            return Finish(rightDrive.Length > 0 || drive.Length == 0 ? rightDrive : drive, rightRoot,
                          rightTail);
        }

        if (rightDrive.Length > 0 && rightDrive != drive)
        {
            if (!rightDrive.Equals(drive, StringComparison.OrdinalIgnoreCase))
            {
                // Another drive: the left path is dropped altogether.
                return Finish(rightDrive, rightRoot, rightTail);
            }

            drive = rightDrive; // the same drive in another case
        }

        if (tail.Length > 0 && tail[^1] != '\\' && tail[^1] != '/')
        {
            tail += "\\";
        }

        return Finish(drive, root, tail + rightTail);
    }

    /// <summary>ntpath.join's last step: a separator between a bare UNC drive and a tail.</summary>
    private static string Finish(string drive, string root, string tail)
        => tail.Length > 0 && root.Length == 0 && drive.Length > 0 &&
           drive[^1] != ':' && drive[^1] != '\\' && drive[^1] != '/'
            ? drive + "\\" + tail
            : drive + root + tail;

    /// <summary>The separator the flavour's <c>str()</c> joins with (pathlib's <c>sep</c>).</summary>
    private static string Separator(PathFlavour flavour)
        => flavour == PathFlavour.Windows ? "\\" : "/";

    /// <inheritdoc cref="Separator(PathFlavour)"/>
    private static char SeparatorChar(PathFlavour flavour)
        => flavour == PathFlavour.Windows ? '\\' : '/';

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
