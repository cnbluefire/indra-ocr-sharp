namespace Indra.Ocr.Viewer;

/// <summary>
/// What the viewer's HTTP layer turns into <c>{"error": "..."}</c>: the message the reference's
/// handler would put there, which is Python's <c>str(exception)</c>.
/// </summary>
/// <remarks>
/// The reference catches every exception and reports its text, so the port's errors carry the
/// reference's wording rather than .NET's. A missing upload, for instance, is reported as
/// <c>'abc123'</c> — the text of Python's <c>KeyError</c>.
/// </remarks>
public sealed class ViewerError : Exception
{
    public ViewerError(string message)
        : base(message)
    {
    }
}
