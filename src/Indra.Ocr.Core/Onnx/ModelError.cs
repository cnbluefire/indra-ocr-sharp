namespace Indra.Ocr.Onnx;

/// <summary>
/// The models are not available: a download failed (no network, no access) or a local model
/// folder lacks files (<c>models.ModelError</c>).
/// </summary>
/// <remarks>
/// The reference subclasses <c>OSError</c>; nothing in the pipeline catches that — only the CLI
/// catches <see cref="ModelError"/> itself — so the base class is not observable here. The
/// message is, because the CLI prints it.
/// </remarks>
public sealed class ModelError : Exception
{
    public ModelError(string message) : base(message)
    {
    }

    public ModelError(string message, Exception inner) : base(message, inner)
    {
    }
}
