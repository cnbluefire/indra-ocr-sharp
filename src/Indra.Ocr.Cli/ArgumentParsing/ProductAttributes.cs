using System.Reflection;

namespace Indra.Ocr.ArgumentParsing;

/// <summary>
/// The name and version a host reports, read back from its own assembly: the build stamps
/// <c>Product</c> and <c>Version</c> onto every project from <c>src/Directory.Build.props</c>.
/// </summary>
/// <remarks>
/// The reference prints <c>indra-ocr {__version__}</c> in both hosts (<c>cli.py:72</c>,
/// <c>viewer.py:609</c>) and the viewer's <c>/info</c> reports the same <c>__version__</c>
/// (<c>viewer.py:262</c>), so the name is the product — <c>indra-ocr</c> — and not the assembly
/// name, which for the viewer is <c>indra-ocr-viewer</c>.  Both values are read from the assembly
/// the host is running as, so <c>Directory.Build.props</c> is the one place to bump them.
/// </remarks>
public static class ProductAttributes
{
    /// <summary>The product name (<c>AssemblyProductAttribute</c>): <c>indra-ocr</c>.</summary>
    public static string Name(Assembly host) =>
        host.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? host.GetName().Name
        ?? "";

    /// <summary>
    /// The product version (<c>AssemblyInformationalVersionAttribute</c>): <c>0.3.2</c>, the
    /// Python package's own version.
    /// </summary>
    public static string Version(Assembly host) =>
        host.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? host.GetName().Version?.ToString()
        ?? "";

    /// <summary>The <c>--version</c> line: <c>indra-ocr 0.3.2</c>.</summary>
    public static string VersionLine(Assembly host) => $"{Name(host)} {Version(host)}";
}
