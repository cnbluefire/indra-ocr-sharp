namespace Indra.Ocr.Onnx;

/// <summary>
/// One progress report from the model downloader: the file being fetched, where it comes from and
/// where it goes, and how much of it has arrived (step 310).
/// </summary>
/// <remarks>
/// The reference draws a <c>tqdm</c> bar on stderr for every file <c>snapshot_download</c> fetches.
/// The port reports instead, and the hosts decide what to do with it: the report carries what the
/// bar is made of rather than a finished line, so a host can print it, log it or put it on a page.
/// </remarks>
/// <param name="File">The file's name in the repository (<c>det.onnx</c>).</param>
/// <param name="Path">The local file it is written to.</param>
/// <param name="Url">The URL it is fetched from.</param>
/// <param name="Downloaded">How many bytes have arrived (0 in the report that opens a file).</param>
/// <param name="Total">The file's size, or 0 when the Hub did not say.</param>
public readonly record struct ModelDownloadProgress(string File, string Path, string Url,
                                                    long Downloaded, long Total);
