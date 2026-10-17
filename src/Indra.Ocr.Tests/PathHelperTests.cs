namespace Indra.Ocr.Tests;

/// <summary>
/// <see cref="PathHelper"/> against CPython's own answers — <c>str(pathlib.PurePosixPath(s))</c>,
/// <c>str(PureWindowsPath(s))</c>, <c>.stem</c>, <c>PurePath.__truediv__</c> and
/// <c>expanduser()</c>, read from python 3.14.7 (the interpreter the reference runs under here) —
/// see <c>docs/370-path-flavour.md</c>.
/// </summary>
/// <remarks>
/// <c>pathlib</c> takes its flavour from <c>os.name</c> and the port picks the same one, but the
/// exercises below name the flavour instead: the POSIX rules are checkable on a Windows host and
/// the Windows rules cannot drift when nobody runs the suite on Windows. The one thing the host
/// decides is exercised on the host, at the end.
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
