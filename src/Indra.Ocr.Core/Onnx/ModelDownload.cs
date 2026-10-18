using System.Globalization;
using System.Net.Http;

namespace Indra.Ocr.Onnx;

/// <summary>
/// Downloads the model set from the Hugging Face repo into a folder you name
/// (<c>indra_ocr.models</c>, the download half).
/// </summary>
/// <remarks>
/// The reference's <c>model_dir</c> does two things: it picks a folder (the argument, then
/// <c>INDRA_OCR_MODEL_DIR</c>, then a Hub download) and it downloads into the Hub's cache. Here
/// the download is one self-contained operation — a folder in, the model files in it — so it
/// never has to know where the folder came from or who is going to read it: <see cref="OrtOcr"/>
/// takes the folder, and the caller decides which one (its own, the environment's, or a cache
/// folder it hands to this class). Nothing is cached globally and no lookup order is imposed.
/// <para>
/// What is downloaded is the Hub's own file list for the revision, filtered the way the
/// reference filters it: <c>*.onnx</c>, <c>meta.json</c> and <c>config.json</c>, with the math
/// models only when <c>math</c> is asked for. A file that is already there at the right size is
/// left alone, so a second call is cheap and a partially-filled folder is completed.
/// </para>
/// <para>
/// <c>HF_HUB_OFFLINE</c> (or the legacy <c>TRANSFORMERS_OFFLINE</c>) turns the call offline
/// outright: no request leaves the process, whatever the Hub holds. The two request budgets are
/// the reference's own numbers — <c>HF_HUB_ETAG_TIMEOUT</c> for the Hub's file listing and
/// <c>HF_HUB_DOWNLOAD_TIMEOUT</c> for a body's headers and for each read of it — and neither is a
/// cap on a transfer as a whole: a model that takes a minute to arrive is fine as long as it keeps
/// arriving.
/// </para>
/// </remarks>
public static class ModelDownload
{
    /// <summary>The Hugging Face repo the models come from.</summary>
    public const string HfRepo = "datalab-to/indra-ocr";

    /// <summary>The model set this package version was built and tested against (a tag).</summary>
    public const string HfRevision = "v3.1.1";

    /// <summary>Every file a model folder needs (the reference's <c>models.FILES</c>).</summary>
    public static readonly string[] Files = ["det.onnx", "rec.onnx", "order.onnx", "meta.json"];

    /// <summary>The math models, downloaded only when <c>math</c> is asked for.</summary>
    public static readonly string[] MathFiles =
    [
        "mathcls.onnx", "math_enc.onnx", "math_dec_init.onnx", "math_dec_step.onnx", "math_meta.json",
    ];

    private const string DefaultHost = "https://huggingface.co";

    /// <summary>
    /// The Hub to talk to: <c>HF_ENDPOINT</c> when it is set, else the public one — the same
    /// override <c>huggingface_hub.constants.ENDPOINT</c> reads (with its trailing slashes
    /// trimmed). A mirror, or an unreachable one for a test that must not reach the network.
    /// </summary>
    private static string Host
    {
        get
        {
            string? endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT");
            return string.IsNullOrEmpty(endpoint)
                ? DefaultHost
                : endpoint.TrimEnd('/');
        }
    }

    /// <summary>
    /// <c>huggingface_hub.constants.HF_HUB_OFFLINE</c> — its <c>_is_true</c> over
    /// <c>HF_HUB_OFFLINE or TRANSFORMERS_OFFLINE</c>, the legacy name. Offline, the reference's
    /// session raises before a request leaves the process, so the port makes none at all.
    /// </summary>
    private static bool OfflineMode
    {
        get
        {
            // An empty value falls through to the legacy variable, and only "1"/"on"/"yes"/"true"
            // (any case, and not trimmed) are true: `_is_true`'s ENV_VARS_TRUE_VALUES.
            string? value = Environment.GetEnvironmentVariable("HF_HUB_OFFLINE");
            if (string.IsNullOrEmpty(value))
            {
                value = Environment.GetEnvironmentVariable("TRANSFORMERS_OFFLINE");
            }

            return value is not null
                   && value.ToUpperInvariant() is "1" or "ON" or "YES" or "TRUE";
        }
    }

    /// <summary>
    /// The budget for one request to the Hub: <c>HF_HUB_ETAG_TIMEOUT</c>, or the reference's
    /// <c>DEFAULT_ETAG_TIMEOUT</c>. The reference leaves its own tree listing untimed
    /// (<c>repo_info</c> and <c>paginate</c> pass no timeout); the port gives it this number so a
    /// Hub that accepts the connection and then says nothing is given up on rather than waited on
    /// forever.
    /// </summary>
    private static double EtagSeconds => Seconds("HF_HUB_ETAG_TIMEOUT");

    /// <summary>
    /// The budget for a file's headers and for each read of its body:
    /// <c>HF_HUB_DOWNLOAD_TIMEOUT</c>, or <c>DEFAULT_DOWNLOAD_TIMEOUT</c>. httpx applies its
    /// download timeout to the request and to every read, so a slow transfer is not cut short.
    /// </summary>
    private static double DownloadSeconds => Seconds("HF_HUB_DOWNLOAD_TIMEOUT");

    /// <summary>
    /// One of the two timeouts the way the reference reads it:
    /// <c>_as_int(os.environ.get(name)) or DEFAULT_…_TIMEOUT</c>, so an unset, empty, zero or
    /// unparsable value is the default. (Python's <c>int()</c> would raise on the unparsable one,
    /// and httpx on a negative one; the port takes the default instead.)
    /// </summary>
    private static double Seconds(string name)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int seconds) && seconds > 0
            ? seconds
            : DefaultTimeoutSeconds;

    /// <summary>The reference's <c>DEFAULT_ETAG_TIMEOUT</c>/<c>DEFAULT_DOWNLOAD_TIMEOUT</c>.</summary>
    private const double DefaultTimeoutSeconds = 10;

    /// <summary>
    /// The reference's <c>LocalEntryNotFoundError</c> text for an offline process with no complete
    /// snapshot in the cache, verbatim.
    /// </summary>
    private const string OfflineReason =
        "Cannot find an appropriate cached snapshot folder for the specified revision on the local " +
        "disk and outgoing traffic has been disabled. To enable repo look-ups and downloads " +
        "online, set 'HF_HUB_OFFLINE=0' as environment variable.";

    /// <summary>The patterns the reference keeps, whatever the model folder.</summary>
    private static readonly string[] Always = ["*.onnx", "meta.json", "config.json"];

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Fills <paramref name="folder"/> with the model files of <paramref name="revision"/>
    /// (default <see cref="HfRevision"/>), creating it if needed. With <paramref name="math"/>,
    /// the math models are downloaded too; without it they are left out.
    /// </summary>
    /// <param name="downloadProgress">
    /// Told about each file as it is fetched: once before its body is read, then at every tenth of
    /// it, then once more at the end when the last report was not the end. A file already there at
    /// the right size is not downloaded and is not reported. <c>null</c> reports nothing: the
    /// downloader prints nothing itself — the reference's bar belongs to its library — and the
    /// hosts decide what theirs looks like.
    /// </param>
    /// <returns>
    /// <c>true</c> when the Hub was reached (the folder now holds what the Hub has); <c>false</c>
    /// when it was not and the folder's own model set was kept as it is. The reference's
    /// <c>snapshot_download</c> *returns* the cached folder when it cannot reach the Hub, so both
    /// outcomes are "the call came back" as far as the daily stamp is concerned.
    /// </returns>
    /// <exception cref="ModelError">
    /// The Hub could not be reached or a file could not be fetched, and the folder does not
    /// already hold the model set (in which case the folder is left as it is: an offline run
    /// keeps the models it has, as the reference does).
    /// </exception>
    public static bool Download(string folder, bool math = false, string? revision = null,
                                Action<ModelDownloadProgress>? downloadProgress = null)
        => DownloadAsync(folder, math, revision, downloadProgress).GetAwaiter().GetResult();

    /// <summary>
    /// <see cref="Download"/>, without blocking the calling thread: the listing, the bodies and the
    /// files are read and written asynchronously, and <paramref name="cancellationToken"/> stops
    /// the download where it stands.
    /// </summary>
    /// <remarks>
    /// This is the only implementation — the synchronous <see cref="Download"/> is a wrapper around
    /// it — so the two cannot drift apart. Nothing here awaits anything but I/O, and every await
    /// continues on a pool thread, so the wrapper cannot deadlock on a UI or ASP.NET thread.
    /// <para>
    /// A cancelled token is the caller's own decision and is <i>not</i> "the Hub could not be
    /// reached": the offline fallback (keep the folder's own model set) does not apply, and
    /// <see cref="OperationCanceledException"/> travels out. A file's <c>.part</c> file stays where
    /// it stopped; the next call overwrites it.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Stops the download where it stands.</param>
    /// <inheritdoc cref="Download(string, bool, string?, Action{ModelDownloadProgress}?)"/>
    public static async Task<bool> DownloadAsync(string folder, bool math = false,
                                                 string? revision = null,
                                                 Action<ModelDownloadProgress>? downloadProgress = null,
                                                 CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrEmpty(folder);
        string rev = revision ?? HfRevision;

        if (OfflineMode)
        {
            // No request leaves the process (the reference's session raises OfflineModeIsEnabled
            // first): a folder that holds the model set is used as it is, exactly as an
            // unreachable Hub would leave it, and anything else gets the reference's message.
            if (HasModels(folder, math))
            {
                return false;
            }

            throw new ModelError(DownloadFailed("LocalEntryNotFoundError", OfflineReason));
        }

        Directory.CreateDirectory(folder);

        List<(string Path, long Size)> wanted;
        try
        {
            wanted = await HubFilesAsync(rev, math, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Offline(e, cancellationToken))
        {
            // Offline or the Hub is down: a folder that already has the model set is used as it
            // is (the reference's "keep the cached models"); otherwise the caller must be told.
            if (HasModels(folder, math))
            {
                return false;
            }

            throw new ModelError(DownloadFailed(e), e);
        }

        foreach ((string path, long size) in wanted)
        {
            string dest = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest) && new FileInfo(dest).Length == size)
            {
                continue; // already there, at the size the Hub has
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                await FetchAsync($"{Host}/{HfRepo}/resolve/{rev}/{path}", dest, path, size,
                                 downloadProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (Offline(e, cancellationToken))
            {
                throw new ModelError(DownloadFailed(e), e);
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an exception means "the Hub could not be reached" — as opposed to a cancellation the
    /// caller asked for, which is left to travel out of the download. The two are told apart by the
    /// token, not by the exception: <c>HttpClient</c> reports its own request budgets as
    /// <see cref="TaskCanceledException"/>, and a body read over its idle budget as whatever the
    /// stream throws, so the whole family counts here and the caller's token decides.
    /// </summary>
    private static bool Offline(Exception e, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
           && e is HttpRequestException or OperationCanceledException or IOException;

    /// <summary>
    /// Whether <paramref name="folder"/> holds the model set (and, with <paramref name="math"/>,
    /// the math models).
    /// </summary>
    public static bool HasModels(string folder, bool math = false)
    {
        foreach (string name in Files)
        {
            if (!File.Exists(Path.Combine(folder, name)))
            {
                return false;
            }
        }

        if (math)
        {
            foreach (string name in MathFiles)
            {
                if (!File.Exists(Path.Combine(folder, name)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the reference's patterns keep a repo file: <c>*.onnx</c>, <c>meta.json</c> and
    /// <c>config.json</c>, with the math models only when <paramref name="math"/> is asked for.
    /// The patterns are Python's <c>fnmatch</c> ones, so <c>*.onnx</c> crosses a <c>/</c>.
    /// </summary>
    public static bool Wanted(string path, bool math = false)
    {
        bool mathFile = MathFiles.Contains(path);
        return mathFile ? math : Array.Exists(Always, p => Matches(path, p));
    }

    /// <summary>
    /// The Hub's files for <paramref name="rev"/> that the reference's patterns keep, with the
    /// sizes it reports.
    /// </summary>
    private static async Task<List<(string Path, long Size)>> HubFilesAsync(
        string rev, bool math, CancellationToken cancellationToken)
    {
        using var budget = Budget(cancellationToken, EtagSeconds);
        // HttpClient.GetStringAsync(string, CancellationToken) is .NET 5; below it the request is
        // made by hand, still under the request's budget.
#if NET5_0_OR_GREATER
        string json = await Http
            .GetStringAsync($"{Host}/api/models/{HfRepo}/tree/{rev}?recursive=true", budget.Token)
            .ConfigureAwait(false);
#else
        string json;
        using (HttpResponseMessage listing = await Http.GetAsync(
            $"{Host}/api/models/{HfRepo}/tree/{rev}?recursive=true", budget.Token).ConfigureAwait(false))
        {
            listing.EnsureSuccessStatusCode();
            json = await listing.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
#endif
        var wanted = new List<(string Path, long Size)>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (System.Text.Json.JsonElement entry in doc.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "file")
            {
                continue;
            }

            string path = entry.GetProperty("path").GetString()!;
            if (Wanted(path, math))
            {
                wanted.Add((path, entry.GetProperty("size").GetInt64()));
            }
        }

        return wanted;
    }

    /// <summary>How much of a response is read at a time while its progress is counted.</summary>
    private const int Chunk = 1 << 16; // 64 KiB

    /// <summary>The share of a file that makes a progress report (a tenth).</summary>
    private const int Step = 10;

    /// <summary>
    /// Fetches one file to <paramref name="dest"/> (via a <c>.part</c> file), reporting it under
    /// the repository name <paramref name="name"/>.
    /// </summary>
    private static async Task FetchAsync(string url, string dest, string name, long size,
                                         Action<ModelDownloadProgress>? downloadProgress,
                                         CancellationToken cancellationToken)
    {
        double seconds = DownloadSeconds;
        HttpResponseMessage res;
        // The headers (and the connect behind them) get the download timeout as their budget; the
        // budget is given up again before the body is read, so the *transfer* is never capped.
        using (var budget = Budget(cancellationToken, seconds))
        {
            res = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
        }

        using (res)
        {
            res.EnsureSuccessStatusCode();
            string part = dest + ".part";
            downloadProgress?.Invoke(new ModelDownloadProgress(name, dest, url, 0, size));
            // HttpContent.ReadAsStreamAsync(CancellationToken) is .NET 5.
#if NET5_0_OR_GREATER
            using (Stream src = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
#else
            using (Stream src = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
#endif
            using (FileStream dst = new FileStream(part, FileMode.Create, FileAccess.Write,
                                                   FileShare.None, Chunk, FileOptions.Asynchronous))
            {
                await CopyAsync(src, dst, size,
                                done => downloadProgress?.Invoke(
                                    new ModelDownloadProgress(name, dest, url, done, size)),
                                cancellationToken, seconds).ConfigureAwait(false);
            }

            // File.Move(string, string, bool) arrived with .NET Core 3.0 / netstandard2.1; below it
            // File.Move refuses an existing destination, and the reference replaces the file
            // (os.replace), so the replace is spelled out there.
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            File.Move(part, dest, overwrite: true);
#else
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }

            File.Move(part, dest);
#endif
        }
    }

    /// <summary>
    /// Copies a response body to <paramref name="dst"/>, calling <paramref name="report"/> with how
    /// much has arrived at every tenth of <paramref name="size"/> — and once more at the end, when
    /// the body does not end on a report (a short file, or a body that is not the size the Hub
    /// listed). A size of 0 (the Hub did not say) is reported at the end alone.
    /// </summary>
    /// <param name="idleSeconds">
    /// The reference's download timeout, applied to each read on its own (httpx's read timeout is
    /// an idle window, not a transfer limit), or <c>null</c> for the bare copy the tests use.
    /// </param>
    internal static async Task CopyAsync(Stream src, Stream dst, long size, Action<long> report,
                                         CancellationToken cancellationToken = default,
                                         double? idleSeconds = null)
    {
        byte[] buffer = new byte[Chunk];
        long done = 0;
        long reported = 0;
        while (true)
        {
            int read = idleSeconds is { } seconds
                ? await ReadAsync(src, buffer, seconds, cancellationToken).ConfigureAwait(false)
                : await src.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            if (size > 0 && (int)(done * 100 / size) >= (int)(reported * 100 / size) + Step)
            {
                reported = done;
                report(done);
            }
        }

        if (done > reported)
        {
            report(done);
        }
    }

    /// <summary>
    /// A budget for one request: the caller's token plus <paramref name="seconds"/> — the
    /// reference's per-request timeout, which is what its two variables are. Disposing the source
    /// stops the timer without cancelling anything, which is how a body's headers are bounded
    /// without the body itself being capped.
    /// </summary>
    private static CancellationTokenSource Budget(CancellationToken cancellationToken,
                                                  double seconds)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter((int)Math.Min(seconds * 1000, int.MaxValue));
        return budget;
    }

    /// <summary>
    /// One read of a body under its own idle budget. A read that never returns ends the download
    /// with the reference's failure (httpx would raise <c>ReadTimeout</c>, which
    /// <c>_snapshot_download</c> treats like an unreachable Hub), and the next one starts the
    /// window over: only a body that stops arriving is given up on.
    /// </summary>
    private static async Task<int> ReadAsync(Stream src, byte[] buffer, double seconds,
                                             CancellationToken cancellationToken)
    {
        using var budget = Budget(cancellationToken, seconds);
        return await src.ReadAsync(buffer.AsMemory(), budget.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Python's <c>fnmatch</c> over a whole repo path: <c>*</c> matches everything (a <c>/</c>
    /// included), <c>?</c> one character. The reference's patterns only use <c>*</c>.
    /// </summary>
    public static bool Matches(string name, string pattern)
    {
        int n = 0, p = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>The reference's one-line, 200-character summary of a download failure.</summary>
    private static string DownloadFailed(Exception e) => DownloadFailed(e.GetType().Name, e.Message);

    /// <summary>
    /// <see cref="DownloadFailed(Exception)"/> for a failure that has no exception behind it,
    /// where <paramref name="cause"/> is the name of the type the reference would have raised
    /// (<c>LocalEntryNotFoundError</c> for a process that is offline). The summary is the
    /// reference's: the cause, then the first line of the message cut to 200 characters.
    /// </summary>
    private static string DownloadFailed(string cause, string message)
    {
        string reason = message.Trim();
        int nl = reason.IndexOfAny(['\n', '\r']);
        if (nl >= 0)
        {
            reason = reason[..nl];
        }

        if (reason.Length == 0)
        {
            reason = cause;
        }

        if (reason.Length > 200)
        {
            reason = reason[..200];
        }

        return $"could not download the models from Hugging Face ({HfRepo}, {HfRevision}): " +
               $"{cause}: {reason}. Check the network connection, or point at a local " +
               "model folder with --model-dir or INDRA_OCR_MODEL_DIR.";
    }

    private static HttpClient CreateClient()
    {
        // No total timeout: huggingface_hub caps one request and one read at a time, never the
        // transfer, and a model that takes a minute to arrive must not be cut off. The per-request
        // budgets are the caller's (HubFilesAsync, FetchAsync).
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("indra-ocr-cs/0.3.2");
        string? token = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }
}
