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
    /// <summary>The shipped model set, <c>resources/models</c>.</summary>
    public static string? Directory { get; } = Find("meta.json", "resources", "models");

    /// <summary>
    /// The folder with the math models — <c>temp/math-models</c>, the same snapshot plus
    /// <c>mathcls.onnx</c> and friends. It is downloaded on demand (the reference's
    /// <c>models.model_dir</c> fetches it) and is not in git, so the math tests are skipped when it
    /// is missing.
    /// </summary>
    public static string? MathDirectory { get; } = Find("math_meta.json", "temp", "math-models");

    private static string? Find(string marker, params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(Path.Combine(candidate, marker)))
                return candidate;
        }
        return null;
    }
}
