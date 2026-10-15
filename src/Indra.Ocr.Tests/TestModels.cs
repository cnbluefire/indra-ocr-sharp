namespace Indra.Ocr.Tests;

/// <summary>
/// Locates <c>resources/models</c> for the tests that need real model files.
/// </summary>
/// <remarks>
/// The model folder is externally downloaded material and is not in git, so those tests are
/// skipped (they return early) when it is missing; the signature fixture itself
/// (<c>fixtures/onnx_models.json</c>) is committed, so the expectations are still reviewed.
/// </remarks>
public static class TestModels
{
    public static string? Directory { get; } = Find();

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "resources", "models");
            if (File.Exists(Path.Combine(candidate, "meta.json")))
                return candidate;
        }
        return null;
    }
}
