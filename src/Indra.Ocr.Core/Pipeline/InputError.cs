namespace Indra.Ocr.Pipeline;

/// <summary>
/// A file that cannot be read: not a PDF or an image, corrupt, or locked
/// (<c>pipeline.InputError</c>).
/// </summary>
/// <remarks>
/// The pipeline treats it as data, not as a crash: <c>document.Converter</c> catches it per page
/// and reports an error page, and the CLI turns it into an error line per input file. The message
/// is what a caller sees, so the port keeps the reference's wording.
/// </remarks>
public sealed class InputError : Exception
{
    public InputError(string message) : base(message)
    {
    }
}
