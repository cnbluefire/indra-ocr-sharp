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
    /// when it was not and the folder's own model set was kept as it is. The distinction is the
    /// caller's: the reference's <c>model_dir</c> only touches its daily stamp after the online
    /// call, so an offline run keeps the models <i>and</i> keeps checking every time.
    /// </returns>
    /// <exception cref="ModelError">
    /// The Hub could not be reached or a file could not be fetched, and the folder does not
    /// already hold the model set (in which case the folder is left as it is: an offline run
    /// keeps the models it has, as the reference does).
    /// </exception>
    public static bool Download(string folder, bool math = false, string? revision = null,
                                Action<ModelDownloadProgress>? downloadProgress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        string rev = revision ?? HfRevision;
        Directory.CreateDirectory(folder);

        List<(string Path, long Size)> wanted;
        try
        {
            wanted = HubFiles(rev, math);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
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
                Fetch($"{Host}/{HfRepo}/resolve/{rev}/{path}", dest, path, size, downloadProgress);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new ModelError(DownloadFailed(e), e);
            }
        }

        return true;
    }

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
    private static List<(string Path, long Size)> HubFiles(string rev, bool math)
    {
        string json = Http.GetStringAsync($"{Host}/api/models/{HfRepo}/tree/{rev}?recursive=true")
                          .GetAwaiter().GetResult();
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
    private static void Fetch(string url, string dest, string name, long size,
                              Action<ModelDownloadProgress>? downloadProgress)
    {
        using HttpResponseMessage res = Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        res.EnsureSuccessStatusCode();
        string part = dest + ".part";
        downloadProgress?.Invoke(new ModelDownloadProgress(name, dest, url, 0, size));
        using (Stream src = res.Content.ReadAsStream())
        using (FileStream dst = File.Create(part))
        {
            Copy(src, dst, size,
                 done => downloadProgress?.Invoke(
                     new ModelDownloadProgress(name, dest, url, done, size)));
        }

        File.Move(part, dest, overwrite: true);
    }

    /// <summary>
    /// Copies a response body to <paramref name="dst"/>, calling <paramref name="report"/> with how
    /// much has arrived at every tenth of <paramref name="size"/> — and once more at the end, when
    /// the body does not end on a report (a short file, or a body that is not the size the Hub
    /// listed). A size of 0 (the Hub did not say) is reported at the end alone.
    /// </summary>
    internal static void Copy(Stream src, Stream dst, long size, Action<long> report)
    {
        byte[] buffer = new byte[Chunk];
        long done = 0;
        long reported = 0;
        while (true)
        {
            int read = src.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            dst.Write(buffer, 0, read);
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
    private static string DownloadFailed(Exception e)
    {
        string message = e.Message.Trim();
        int nl = message.IndexOfAny(['\n', '\r']);
        string reason = nl >= 0 ? message[..nl] : message;
        if (reason.Length == 0)
        {
            reason = e.GetType().Name;
        }

        if (reason.Length > 200)
        {
            reason = reason[..200];
        }

        return $"could not download the models from Hugging Face ({HfRepo}, {HfRevision}): " +
               $"{e.GetType().Name}: {reason}. Check the network connection, or point at a local " +
               "model folder with --model-dir or INDRA_OCR_MODEL_DIR.";
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
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
