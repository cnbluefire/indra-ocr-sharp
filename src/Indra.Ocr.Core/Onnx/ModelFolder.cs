namespace Indra.Ocr.Onnx;

/// <summary>
/// The reference's <c>models.model_dir</c> — which model folder a run uses: the
/// <c>--model-dir</c> argument, then <c>INDRA_OCR_MODEL_DIR</c>, then a Hugging Face download
/// (cached, and re-checked at most once a day).
/// </summary>
/// <remarks>
/// It lives in Core because both hosts resolve their models the same way — the reference has
/// <c>models.model_dir</c> shared by <c>cli.py</c> and <c>viewer.py</c> — and because
/// <see cref="ModelDownload"/> and <see cref="ModelError"/> are here.
/// <para>
/// The download itself (<see cref="ModelDownload"/>) is one self-contained operation — a folder
/// in, the model files in it. The port's cache is the Hub's own cache
/// (<c>HF_HUB_CACHE</c>/<c>HUGGINGFACE_HUB_CACHE</c>, else <c>HF_HOME/hub</c>, else
/// <c>~/.cache/huggingface/hub</c>) under the repo's folder, so a model set the reference already
/// downloaded is found and used as it is — and the daily-check stamp is the same file the
/// reference touches, so the two do not check the Hub twice.
/// </para>
/// </remarks>
public static class ModelFolder
{
    /// <summary>A cached model folder is checked against the Hub at most this often.</summary>
    private const long CheckEverySeconds = 24 * 3600;

    /// <summary>The folder a run should load its models from (downloading them if need be).</summary>
    /// <param name="path">The <c>--model-dir</c> value, or <c>null</c> when it was not given.</param>
    /// <param name="math">Whether the math models are needed.</param>
    /// <param name="downloadProgress">
    /// Told about each file the download fetches, as <see cref="ModelDownload.Download"/> reports
    /// it. <c>null</c> (the default) reports nothing; a folder that needs no download reports
    /// nothing either way.
    /// </param>
    /// <exception cref="ModelError">The folder is incomplete, or the download failed.</exception>
    public static string Resolve(string? path, bool math,
                                 Action<ModelDownloadProgress>? downloadProgress = null)
    {
        string? given = path;
        if (given is null)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("INDRA_OCR_MODEL_DIR");
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                given = fromEnvironment;
            }
        }

        if (given is not null)
        {
            string folder = PathHelper.ExpandUser(given);
            List<string> missing = Missing(folder, math);
            if (missing.Count > 0)
            {
                throw new ModelError($"the model folder {folder} lacks {string.Join(", ", missing)}");
            }

            return folder;
        }

        string cache = CacheFolder();
        string repo = $"models--{ModelDownload.HfRepo.Replace("/", "--", StringComparison.Ordinal)}";
        string modelRoot = Path.Combine(cache, repo);
        string snapshot = Path.Combine(modelRoot, "snapshots", SnapshotName(modelRoot));
        string stamp = Path.Combine(cache, StampName());

        bool cached = Missing(snapshot, math).Count == 0;
        if (cached && Age(stamp) < CheckEverySeconds)
        {
            return snapshot;
        }

        try
        {
            // The stamp is only touched when the Hub was really reached (the reference's `_touch`
            // sits on the online branch alone): an offline run keeps the cached models and keeps
            // checking, rather than going quiet for a day.
            if (ModelDownload.Download(snapshot, math, downloadProgress: downloadProgress))
            {
                Touch(stamp);
            }

            return snapshot;
        }
        catch (ModelError)
        {
            // Offline or the Hub is down: a cached model set is kept (the reference does the same).
            if (cached)
            {
                return snapshot;
            }

            throw;
        }
    }

    /// <summary>
    /// The files the reference checks for: <c>FILES[:2] + FILES[3:]</c> — <c>det.onnx</c>,
    /// <c>rec.onnx</c> and <c>meta.json</c>, but not <c>order.onnx</c> (the reference's own quirk:
    /// a folder without the order model is accepted, and the pipeline then has no order model).
    /// </summary>
    private static List<string> Missing(string folder, bool math)
    {
        var missing = new List<string>();
        foreach (string name in ModelDownload.Files.Take(2).Concat(ModelDownload.Files.Skip(3)))
        {
            if (!File.Exists(Path.Combine(folder, name)))
            {
                missing.Add(name);
            }
        }

        if (math)
        {
            foreach (string name in ModelDownload.MathFiles)
            {
                if (!File.Exists(Path.Combine(folder, name)))
                {
                    missing.Add(name);
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// The snapshot folder name: the commit the revision points at, as the Hub's cache records it
    /// in <c>refs/&lt;revision&gt;</c>; without that file the revision names the folder.
    /// </summary>
    private static string SnapshotName(string modelRoot)
    {
        try
        {
            string reference = Path.Combine(modelRoot, "refs", ModelDownload.HfRevision);
            if (File.Exists(reference))
            {
                string commit = File.ReadAllText(reference).Trim();
                if (commit.Length > 0)
                {
                    return commit;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable refs: fall back to the revision name.
        }

        return ModelDownload.HfRevision;
    }

    /// <summary><c>huggingface_hub.constants.HF_HUB_CACHE</c>.</summary>
    private static string CacheFolder()
    {
        string? cache = Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_CACHE");
        if (string.IsNullOrEmpty(cache))
        {
            cache = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        }

        if (!string.IsNullOrEmpty(cache))
        {
            return cache;
        }

        string? home = Environment.GetEnvironmentVariable("HF_HOME");
        if (string.IsNullOrEmpty(home))
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            string baseDirectory = string.IsNullOrEmpty(xdg)
                ? Path.Combine(UserProfile(), ".cache")
                : xdg;
            home = Path.Combine(baseDirectory, "huggingface");
        }

        return Path.Combine(home, "hub");
    }

    private static string UserProfile()
        => Environment.GetEnvironmentVariable("USERPROFILE")
           ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The reference's stamp file name (<c>_check_stamp</c>).</summary>
    private static string StampName()
        => $".indra_ocr_checked--{ModelDownload.HfRepo.Replace("/", "--", StringComparison.Ordinal)}" +
           $"--{ModelDownload.HfRevision}";

    /// <summary><c>_age</c>: seconds since the stamp was touched (infinite without one).</summary>
    private static double Age(string stamp)
    {
        try
        {
            return (DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp)).TotalSeconds;
        }
        catch (IOException)
        {
            return double.PositiveInfinity;
        }
    }

    /// <summary><c>_touch</c>: a read-only cache is checked again next time.</summary>
    private static void Touch(string stamp)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
            if (!File.Exists(stamp))
            {
                File.WriteAllBytes(stamp, []);
            }

            File.SetLastWriteTimeUtc(stamp, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // A read-only cache: check again next time.
        }
    }
}
