using System.Text.Json;

namespace Indra.Ocr.Tests;

/// <summary>
/// <see cref="PathHelper"/> against CPython's own answers — <c>str(pathlib.PurePosixPath(s))</c>,
/// <c>str(PureWindowsPath(s))</c>, <c>.stem</c>, <c>PurePath.__truediv__</c> and
/// <c>expanduser()</c>, read from python 3.14.7 (the interpreter the reference runs under here) —
/// see <c>docs/370-path-flavour.md</c> and <c>docs/375-path-parity.md</c>.
/// </summary>
/// <remarks>
/// <c>pathlib</c> takes its flavour from <c>os.name</c> and the port picks the same one, but the
/// exercises below name the flavour instead: the POSIX rules are checkable on a Windows host and
/// the Windows rules cannot drift when nobody runs the suite on Windows. The one thing the host
/// decides is exercised on the host, at the end.
/// <para>
/// The cases named here are the rules; <see cref="The_fuzz_corpus_matches_cpython"/> runs the whole
/// committed corpus (<c>fixtures/path_fuzz.json</c>) under them.
/// </para>
/// </remarks>
public class PathHelperTests
{
    // ---------------------------------------------------------------- str(Path(s))

    /// <summary><c>str(PurePosixPath(s))</c>: <c>/</c> separates, a backslash is a name.</summary>
    [Theory]
    [InlineData("", ".")]
    [InlineData(".", ".")]
    [InlineData("./", ".")]
    [InlineData("/", "/")]
    [InlineData("//", "//")]
    [InlineData("///", "/")]
    [InlineData("///a", "/a")]
    [InlineData("//a", "//a")]
    [InlineData("//a//b", "//a/b")]
    [InlineData("//a/.", "//a")]
    [InlineData("a", "a")]
    [InlineData("a/", "a")]
    [InlineData("./a", "a")]
    [InlineData(".//a", "a")]
    [InlineData("a/./b", "a/b")]
    [InlineData("a//b", "a/b")]
    [InlineData("a/../b", "a/../b")]
    [InlineData("../a", "../a")]
    [InlineData("..", "..")]
    [InlineData("/a/", "/a")]
    [InlineData("/a//b/./c/", "/a/b/c")]
    [InlineData("/..", "/..")]
    [InlineData(@"\a\b", @"\a\b")]
    [InlineData(@"a\b/c", @"a\b/c")]
    [InlineData("C:/a", "C:/a")]
    [InlineData(@"C:\a", @"C:\a")]
    [InlineData("////a", "/a")]
    [InlineData("//a/b", "//a/b")]
    public void Normalize_posix(string path, string expected)
        => Assert.Equal(expected, PathHelper.Normalize(path, PathFlavour.Posix));

    /// <summary><c>str(PureWindowsPath(s))</c>: <c>/</c> is an altsep, drives and UNC anchor.</summary>
    [Theory]
    [InlineData("", ".")]
    [InlineData("/", @"\")]
    [InlineData("a/", "a")]
    [InlineData("./a", "a")]
    [InlineData("a/./b", @"a\b")]
    [InlineData("a//b", @"a\b")]
    [InlineData("a/../b", @"a\..\b")]
    [InlineData("../a", @"..\a")]
    [InlineData("a/b.txt", @"a\b.txt")]
    [InlineData("/a/", @"\a")]
    [InlineData("/a//b/./c/", @"\a\b\c")]
    [InlineData("C:/a", @"C:\a")]
    [InlineData("c:/a", @"c:\a")]
    [InlineData(@"C:\a", @"C:\a")]
    [InlineData("C:", "C:")]
    [InlineData("C:a", "C:a")]
    [InlineData(@"\a", @"\a")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData(@"a\b/c", @"a\b\c")]
    [InlineData(@"\\server\share\a", @"\\server\share\a")]
    // Degenerate and UNC shapes, decided by python 3.14's ntpath.splitroot rather than by a special
    // case: `\\` alone is a drive with no root, `\\srv` is a drive whose share never got a
    // separator, a third separator at the start is part of the drive, and a share that *is* the
    // whole path takes the root splitroot could not see (so it keeps a trailing separator).
    [InlineData(@"\\", @"\\")]
    [InlineData("//", @"\\")]
    [InlineData(@"\\srv", @"\\srv")]
    [InlineData("\\\\srv\\\\", "\\\\srv\\\\")]
    [InlineData(@"\\\server", @"\\\server")]
    [InlineData("//server/share", "\\\\server\\share\\")]
    [InlineData(@"\\server\share", "\\\\server\\share\\")]
    [InlineData("\\\\server\\share\\\\", "\\\\server\\share\\")]
    [InlineData(@"\\?\UNC\srv\share", "\\\\?\\UNC\\srv\\share\\")]
    [InlineData("\\\\?\\\\", "\\\\?\\\\")]
    [InlineData("//?/", @"\\?\")]
    [InlineData("//./device", @"\\.\device")]
    [InlineData(@"\\?\device", @"\\?\device")]
    [InlineData(@"\\.\device", @"\\.\device")]
    [InlineData(@"\\/a..a", @"\\\a..a")]
    [InlineData(@"\\..a~xC", @"\\..a~xC")]
    [InlineData(@"\\srv\..aa", "\\\\srv\\..aa\\")]
    [InlineData("////", "\\\\\\\\")]
    [InlineData("\\\\\\\\\\\\", "\\\\\\\\")]
    public void Normalize_windows(string path, string expected)
        => Assert.Equal(expected, PathHelper.Normalize(path, PathFlavour.Windows));

    // ---------------------------------------------------------------- Path.stem

    /// <summary><c>PurePosixPath(s).stem</c> — python 3.14's rule (a stem keeps a non-dot).</summary>
    [Theory]
    [InlineData("/a/b.txt", "b")]
    [InlineData("a/b/", "b")]
    [InlineData("b.tar.gz", "b.tar")]
    [InlineData("c..", "c.")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData(".c", ".c")]
    [InlineData("..", "..")]
    [InlineData("/", "")]
    [InlineData("a.b/c.", "c")]
    [InlineData(@"a\b.txt", @"a\b")]   // one name: the dot in it is the name's, not a separator's
    // `.stem` reads the last *component*, so a path that is only a root (or nothing at all) has no
    // stem to read — the port used to answer "." for the empty spelling.
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("//", "")]
    [InlineData("////a", "a")]
    [InlineData("//a", "a")]
    [InlineData("a/..", "..")]
    [InlineData("a/.", "a")]
    public void Stem_posix(string path, string expected)
        => Assert.Equal(expected, PathHelper.Stem(path, PathFlavour.Posix));

    /// <summary><c>PureWindowsPath(s).stem</c>.</summary>
    [Theory]
    [InlineData(@"C:\a\b.txt", "b")]
    [InlineData(@"C:\a\b\", "b")]
    [InlineData(@"\\server\share\a\b.txt", "b")]
    [InlineData("C:", "")]
    [InlineData("C:a.txt", "a")]
    [InlineData("c.", "c")]
    [InlineData("c..", "c.")]
    [InlineData("report..pdf", "report.")]
    [InlineData("..", "..")]
    [InlineData(".c", ".c")]
    [InlineData(@"a.b/c.", "c")]
    // Like the POSIX half: nothing to read in a drive-only or root-only spelling.
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("/", "")]
    [InlineData(@"C:\", "")]
    [InlineData(@"\\srv", "")]
    [InlineData("\\\\srv\\\\", "")]
    [InlineData(@"\\\server", "")]
    [InlineData(@"\\?\UNC\srv", "")]
    [InlineData(@"\\server\share", "")]
    [InlineData("\\\\server\\share\\\\", "")]
    public void Stem_windows(string path, string expected)
        => Assert.Equal(expected, PathHelper.Stem(path, PathFlavour.Windows));

    // ---------------------------------------------------------------- Path(left) / right

    /// <summary><c>PurePosixPath(left) / right</c> (nothing is absolute but a root).</summary>
    [Theory]
    [InlineData("a", "b", "a/b")]
    [InlineData("a", "/b", "/b")]
    [InlineData("a", "//b", "//b")]
    [InlineData("a", "b/c", "a/b/c")]
    [InlineData("a/b", "../c", "a/b/../c")]
    [InlineData("/a", "..", "/a/..")]
    [InlineData("/a", "b", "/a/b")]
    [InlineData("a", "C:/b", "a/C:/b")]
    [InlineData("a", "", "a")]
    // `posixpath.join` over the raw spellings: an empty (or ".") left is not a root, so the join
    // does not put a leading separator on the result the left never had.
    [InlineData("", "b", "b")]
    [InlineData("", "", ".")]
    [InlineData(".", "b", "b")]
    [InlineData("", "/b", "/b")]
    [InlineData("/", "b", "/b")]
    [InlineData("//", "b", "//b")]
    public void Join_posix(string left, string right, string expected)
        => Assert.Equal(expected, PathHelper.Join(left, right, PathFlavour.Posix));

    /// <summary><c>PureWindowsPath(left) / right</c>: a drive or a root on the right wins.</summary>
    [Theory]
    [InlineData("a", "b", @"a\b")]
    [InlineData(@"C:\a", "b", @"C:\a\b")]
    [InlineData(@"C:\a\b", @"..\c", @"C:\a\b\..\c")]
    [InlineData("a", "/b", @"\b")]
    [InlineData("a", @"C:\b", @"C:\b")]
    [InlineData("a", @"\\server\share\x", @"\\server\share\x")]
    // `ntpath.join` inserts its separator only where it owns one: an empty left has none to give
    // (`Path("") / "b"` is `b`), a bare drive takes it without a separator (`C:` + `b` is `C:b`),
    // a whole UNC share does (`\\srv\share` + `b` is `\\srv\share\b`), and another drive on the
    // right drops the left altogether.
    [InlineData("", "b", "b")]
    [InlineData("", "", ".")]
    [InlineData(".", "b", "b")]
    [InlineData("", "/b", @"\b")]
    [InlineData("C:", "b", "C:b")]
    [InlineData(@"C:\", "b", @"C:\b")]
    [InlineData(@"\\srv", "b", "\\\\srv\\b\\")]
    [InlineData(@"\\srv\share", "b", @"\\srv\share\b")]
    [InlineData("\\\\srv\\share\\\\", "b", @"\\srv\share\b")]
    [InlineData("a", "C:b", "C:b")]
    [InlineData("C:", @"D:\x", @"D:\x")]
    [InlineData(@"C:\a", "C:b", @"C:\a\b")]
    [InlineData(@"C:\a", @"c:\b", @"c:\b")]
    [InlineData(@"\\srv\share", @"\\other\x", "\\\\other\\x\\")]
    public void Join_windows(string left, string right, string expected)
        => Assert.Equal(expected, PathHelper.Join(left, right, PathFlavour.Windows));

    // ---------------------------------------------------------------- expanduser()

    /// <summary>
    /// <c>Path(s).expanduser()</c> on POSIX: <c>$HOME</c> for a leading <c>~</c>.
    /// <c>~user</c> is left alone — the port does not read <c>pwd</c>, so the reference's
    /// <c>RuntimeError</c> ("Could not determine home directory") is the one thing it does not
    /// reproduce there.
    /// </summary>
    [Theory]
    [InlineData("~", "/home/runner")]
    [InlineData("~/", "/home/runner")]
    [InlineData("~/a", "/home/runner/a")]
    [InlineData("~/a/b", "/home/runner/a/b")]
    [InlineData("~//a", "/home/runner/a")]
    [InlineData("~/.", "/home/runner")]
    [InlineData("~/..", "/home/runner/..")]
    [InlineData("a/~", "a/~")]
    [InlineData("~user", "~user")]
    [InlineData("~user/a", "~user/a")]
    public void ExpandUser_posix(string path, string expected)
        => Assert.Equal(expected, PathHelper.ExpandUser(path, PathFlavour.Posix, "/home/runner"));

    /// <summary><c>Path(s).expanduser()</c> on Windows: <c>%USERPROFILE%</c> for a leading <c>~</c>.</summary>
    [Theory]
    [InlineData("~", @"C:\Users\x")]
    [InlineData("~/", @"C:\Users\x")]
    [InlineData("~/a", @"C:\Users\x\a")]
    [InlineData("~/a/b", @"C:\Users\x\a\b")]
    [InlineData("~//a", @"C:\Users\x\a")]
    [InlineData("~/..", @"C:\Users\x\..")]
    [InlineData(@"a\~", @"a\~")]
    [InlineData("~user", "~user")]
    public void ExpandUser_windows(string path, string expected)
        => Assert.Equal(expected, PathHelper.ExpandUser(path, PathFlavour.Windows, @"C:\Users\x"));

    /// <summary>A home that is not there (no variable set) leaves the <c>~</c> alone.</summary>
    [Fact]
    public void ExpandUser_without_a_home_leaves_the_tilde_alone()
    {
        Assert.Equal("~", PathHelper.ExpandUser("~", PathFlavour.Posix, null));
        Assert.Equal("~/a", PathHelper.ExpandUser("~/a", PathFlavour.Posix, null));
        Assert.Equal("~", PathHelper.ExpandUser("~", PathFlavour.Windows, null));
        Assert.Equal(@"~\a", PathHelper.ExpandUser("~/a", PathFlavour.Windows, null));
    }

    // ---------------------------------------------------------------- the fuzz corpus

    /// <summary>
    /// <c>fixtures/path_fuzz.json</c> — made by <c>tools/scripts/test/gen_path_fuzz.py</c>, which
    /// asks <c>pathlib</c> 3.14 itself: the four operations over a nasty alphabet (drives, UNC and
    /// device prefixes, doubled separators, <c>~</c>, spaces, non-ASCII), both flavours, thousands
    /// of cases each. The theories above name the rules; this is the net under them.
    /// </summary>
    [Fact]
    public void The_fuzz_corpus_matches_cpython()
    {
        JsonElement fixture = Fixtures.Load("path_fuzz.json");
        JsonElement homes = fixture.GetProperty("homes");
        string posixHome = homes.GetProperty("posix").GetString()!;
        string windowsHome = homes.GetProperty("windows").GetString()!;

        int seen = 0;
        foreach (JsonElement row in fixture.GetProperty("cases").EnumerateArray())
        {
            string kind = row[0].GetString()!;
            string flavourName = row[1].GetString()!;
            PathFlavour flavour = flavourName == "posix" ? PathFlavour.Posix : PathFlavour.Windows;
            string a = row[2].GetString()!;
            string b = row[3].GetString()!;
            string expected = row[4].GetString()!;
            string actual = kind switch
            {
                "normalize" => PathHelper.Normalize(a, flavour),
                "stem" => PathHelper.Stem(a, flavour),
                "join" => PathHelper.Join(a, b, flavour),
                "expanduser" => PathHelper.ExpandUser(
                    a, flavour, flavour == PathFlavour.Posix ? posixHome : windowsHome),
                _ => throw new InvalidOperationException($"unknown kind {kind}"),
            };

            Assert.True(expected == actual,
                        $"{kind} {flavourName} a=\"{a}\" b=\"{b}\": " +
                        $"expected \"{expected}\", got \"{actual}\"");
            seen++;
        }

        Assert.True(seen > 1000, $"the corpus held only {seen} cases");
    }

    // ---------------------------------------------------------------- the host's flavour

    /// <summary>
    /// The flavour <c>pathlib</c> would pick here: <c>Path(s)</c> and <c>PurePosixPath(s)</c> agree
    /// on POSIX and <c>Path(s)</c> and <c>PureWindowsPath(s)</c> on Windows, so the host's
    /// separator is the framework's own.
    /// </summary>
    [Fact]
    public void The_host_flavour_is_the_one_pathlib_would_pick()
    {
        Assert.Equal(Path.Combine("temp", "x"), PathHelper.Normalize("temp/x/"));
        Assert.Equal(Path.Combine("a", "..", "b.pdf"), PathHelper.Normalize("a/../b.pdf"));
    }

    /// <summary>The host reads the home variable the reference's <c>os.path</c> reads.</summary>
    [Fact]
    public void The_host_expanduser_reads_the_hosts_home()
    {
        string? home = Environment.GetEnvironmentVariable(
            OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (string.IsNullOrEmpty(home))
        {
            return;
        }

        Assert.Equal(PathHelper.Normalize(home), PathHelper.ExpandUser("~"));
    }
}
