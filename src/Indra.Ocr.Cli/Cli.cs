using System.Text;
using Indra.Ocr.Cli.Argparse;
using Indra.Ocr.Cli.Worker;
using Indra.Ocr.Images;
using Indra.Ocr.Layout;
using Indra.Ocr.Onnx;
using Indra.Ocr.Pdf;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Py;

namespace Indra.Ocr.Cli;

/// <summary>
/// The port of <c>indra_ocr.cli</c>: PDFs or images in, markdown (or text, or JSON lines) out.
/// </summary>
/// <remarks>
/// The command line's surface is a parity target, so the argument parser is a port of argparse
/// (<see cref="ArgumentParser"/>) and the output — the pages, the error lines, the timings, the
/// JSON — is written the way the reference writes it (Python's <c>json.dumps</c>, Python's float
/// spelling, Python's newline translation on Windows). The pages of the inputs are converted in
/// this process for now; the worker-process pool is the next step.
/// </remarks>
internal static class Cli
{
    /// <summary>
    /// <c>cli.main</c>: exit code 0, or 1 when an input failed or the models are not available.
    /// </summary>
    public static int Run(string[] args)
    {
        try
        {
            return Execute(args);
        }
        catch (ArgparseExit exit)
        {
            // argparse writes --help and --version to stdout and an error to stderr, then exits.
            if (exit.Stdout.Length > 0)
            {
                PyConsole.Write(exit.Stdout);
            }

            if (exit.Stderr.Length > 0)
            {
                PyConsole.WriteError(exit.Stderr);
            }

            return exit.Status;
        }
        catch (ModelError e)
        {
            PyConsole.WriteError($"indra-ocr: {e.Message}\n");
            return 1;
        }
        catch (Exception e)
        {
            // The reference lets anything but a ModelError out as a traceback, which exits 1.
            PyConsole.WriteError($"indra-ocr: {e.GetType().Name}: {e.Message}\n");
            return 1;
        }
    }

    /// <summary><c>cli._main</c>: the parser, the plan, the pages and the output.</summary>
    private static int Execute(string[] args)
    {
        ArgumentParser parser = CliParser.Build();
        Namespace a = parser.ParseArgs(args);

        PyPath? outDirectory = a.PathOrNull("out");
        if (outDirectory is PyPath outPath)
        {
            Directory.CreateDirectory(outPath.ToString());
        }

        string format = a.Str("format") ?? "md";
        string extension = format switch
        {
            "md" => ".md",
            "text" => ".txt",
            _ => ".jsonl",
        };

        int cores = a.Int("cores");
        int maxPages = a.Int("max_pages");
        string? password = a.Str("password");
        double dpi = a.Float("dpi");
        bool furniture = a.Flag("keep_furniture");
        bool math = a.Flag("math");
        bool timings = a.Flag("timings");
        string mode = a.Str("mode") ?? "auto";
        bool pdfOrder = !a.Flag("model_order");
        bool imageOcr = !a.Flag("no_image_ocr");

        // convert_many resolves (and on a first run downloads) the models once, before the
        // workers start; a ModelError reaches main() and becomes one line and exit 1.
        string modelDirectory = ModelFolder.Resolve(a.PathOrNull("model_dir"), math);
        int threads = Math.Max(1, cores != 0 ? cores : CliParser.UsableCores());
        var load = new LoadOptions(modelDirectory, dpi, furniture, math, mode, pdfOrder, imageOcr);

        List<PyPath> inputs = a.PathList("inputs");
        List<object> plan = Plan(inputs, maxPages, password);
        int failed = 0;
        var names = new HashSet<string>();

        foreach ((string path, object result) in ConvertMany(inputs, plan, maxPages, password,
                                                             threads, load))
        {
            PyPath input = PyPath.From(path);
            var chunks = new List<string>();
            if (result is InputError error)
            {
                PyConsole.WriteError($"indra-ocr: {error.Message}\n");
                failed++;
                continue;
            }

            foreach ((int i, OcrPage page) in (List<(int Index, OcrPage Page)>)result)
            {
                if (page.Source == "error")
                {
                    PyConsole.WriteError($"indra-ocr: {input} page {i + 1}: {page.Note}\n");
                }

                if (format == "json")
                {
                    chunks.Add(PageJson(input, i, page));
                }
                else if (format == "md")
                {
                    chunks.Add(page.Markdown ?? page.Text);
                }
                else
                {
                    chunks.Add(page.Text);
                }

                if (timings)
                {
                    page.Timings.TryGetValue("total_ms", out double total);
                    PyConsole.WriteError(
                        $"{input} page {i + 1}: {page.Source}, {PyFloat.Fixed(total, 0)} ms " +
                        $"{page.Note}\n");
                }
            }

            string separator = format == "json" ? "\n" : "\n\n";
            string body = string.Join(separator, chunks) + "\n";
            if (outDirectory is PyPath directory)
            {
                string name = input.Stem;
                int k = 1;
                while (names.Contains(name)) // two inputs with one name: keep both outputs
                {
                    k++;
                    name = $"{input.Stem}_{k}";
                }

                names.Add(name);
                File.WriteAllText(directory.Join(name + extension).ToString(),
                                  PyText.Translate(body), new UTF8Encoding(false));
            }
            else
            {
                PyConsole.Write(body);
            }
        }

        if (failed > 0)
        {
            PyConsole.WriteError($"indra-ocr: {failed} of {inputs.Count} inputs failed\n");
        }

        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// <c>parallel._plan</c>: per input, its work units or the <see cref="InputError"/> that
    /// stops it. A PDF is opened once for its page count (the arithmetic is the caller's, as in
    /// the reference); an image file is one unit that converts the whole file.
    /// </summary>
    private static List<object> Plan(List<PyPath> paths, int maxPages, string? password)
    {
        var plan = new List<object>();
        foreach (PyPath p in paths)
        {
            string path = p.ToString();
            if (!File.Exists(path))
            {
                plan.Add(new InputError($"{path}: no such file"));
                continue;
            }

            if (!OrtOcr.IsPdf(path))
            {
                plan.Add(new List<WorkUnit> { new(path, null, maxPages) });
                continue;
            }

            int count;
            try
            {
                using PdfDocument doc = PdfConverter.Open(path, password);
                count = doc.PageCount;
            }
            catch (InputError e)
            {
                plan.Add(e);
                continue;
            }

            if (maxPages != 0)
            {
                count = Math.Min(count, maxPages);
            }

            var units = new List<WorkUnit>();
            for (int i = 0; i < count; i++)
            {
                units.Add(new WorkUnit(path, i, password));
            }

            plan.Add(units);
        }

        return plan;
    }

    /// <summary>
    /// <c>parallel.convert_many</c>: (path, result) per input, in input order — the result being
    /// the pages in page order or the error that stopped the file.
    /// </summary>
    /// <remarks>
    /// The pages of all inputs are spread over one worker process per core (fewer when the
    /// machine's memory is under <see cref="WorkerMemory"/> per worker); with fewer than
    /// <see cref="MinPoolPages"/> pages, or fewer than two workers, one process reads everything
    /// with all the cores on each page.
    /// </remarks>
    internal static IEnumerable<(string Path, object Result)> ConvertMany(
        List<PyPath> paths, List<object> plan, int maxPages, string? password, int cores,
        LoadOptions load)
    {
        int pages = 0;
        foreach (object item in plan)
        {
            if (item is List<WorkUnit> units)
            {
                pages += units.Count;
            }
        }

        int workers = Math.Min(Math.Min(cores / ThreadsPerWorker, pages), MaxWorkersForMemory());
        if (workers < 2 || pages < MinPoolPages)
        {
            return Serial(paths, plan, maxPages, password, cores, load);
        }

        return Pooled(paths, plan, workers, maxPages, password, cores, load);
    }

    /// <summary>
    /// <c>parallel._serial</c>: the inputs in this process, <paramref name="threads"/> ONNX
    /// Runtime threads on each page.
    /// </summary>
    private static IEnumerable<(string Path, object Result)> Serial(
        List<PyPath> paths, List<object> plan, int maxPages, string? password, int threads,
        LoadOptions load)
    {
        using var ocr = new OrtOcr(load.ModelDirectory, threads: threads, providers: null,
                                   furniture: load.Furniture, math: load.Math);
        var pdf = new PdfConverter(ocr, load.Dpi, load.Mode, load.PdfOrder, load.ImageOcr);
        var images = new ImageConverter(ocr);

        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i].ToString();
            if (plan[i] is InputError error)
            {
                yield return (path, error);
                continue;
            }

            object result;
            try
            {
                result = ReadFile(path, maxPages, password, pdf, images);
            }
            catch (InputError e)
            {
                result = e;
            }

            yield return (path, result);
        }
    }

    /// <summary>
    /// <c>parallel._pooled</c>: the pages on a pool of worker processes. If the workers cannot
    /// run — the reference warns about the spawn start method importing the caller's main
    /// module, which a self-exec worker never does, but a worker can still die — the remaining
    /// inputs are read in this process.
    /// </summary>
    private static IEnumerable<(string Path, object Result)> Pooled(
        List<PyPath> paths, List<object> plan, int workers, int maxPages, string? password,
        int cores, LoadOptions load)
    {
        var flat = new List<WorkUnit>();
        foreach (object item in plan)
        {
            if (item is List<WorkUnit> units)
            {
                flat.AddRange(units);
            }
        }

        // Thread pools of the libraries the workers import (OpenMP, BLAS) take their size from
        // the environment at import: keep each to the worker's share while they start.
        string[] names = ["OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"];
        string?[] saved = new string?[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            saved[i] = Environment.GetEnvironmentVariable(names[i]);
        }

        SetWorkerThreads(names, ThreadsPerWorker);
        WorkerPool? pool = null;
        bool broken = false;
        try
        {
            pool = new WorkerPool(workers, load, ThreadsPerWorker, flat);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException
                                  or NotSupportedException
                                  or System.ComponentModel.Win32Exception)
        {
            broken = true; // the reference's BrokenProcessPool
        }
        finally
        {
            SetWorkerThreads(names, saved);
        }

        int unit = 0;
        int done = 0;
        if (!broken)
        {
            try
            {
                for (int i = 0; i < paths.Count && !broken; i++)
                {
                    string path = paths[i].ToString();
                    if (plan[i] is InputError error)
                    {
                        yield return (path, error);
                        done++;
                        continue;
                    }

                    var pages = new List<(int Index, OcrPage Page)>();
                    InputError? failure = null;
                    foreach (WorkUnit _ in (List<WorkUnit>)plan[i])
                    {
                        WorkResult result;
                        try
                        {
                            result = pool!.Result(unit++);
                        }
                        catch (BrokenPoolException)
                        {
                            broken = true;
                            break;
                        }

                        if (result.Error is not null)
                        {
                            failure ??= result.Error;
                        }
                        else
                        {
                            pages.AddRange(result.Pages);
                        }
                    }

                    if (broken)
                    {
                        break;
                    }

                    yield return (path, failure is not null ? failure : pages);
                    done++;
                }
            }
            finally
            {
                pool!.Dispose();
            }
        }

        if (broken)
        {
            PyConsole.WriteError(
                "RuntimeWarning: indra_ocr.convert_many: the worker processes stopped (with the " +
                "spawn start method, call convert_many under `if __name__ == \"__main__\":`); " +
                "reading the remaining inputs in this process\n");
            foreach ((string path, object result) in
                     Serial(paths.GetRange(done, paths.Count - done),
                            plan.GetRange(done, plan.Count - done), maxPages, password, cores,
                            load))
            {
                yield return (path, result);
            }
        }
    }

    /// <summary>Set the thread-pool environment variables (a <c>null</c> value removes them).</summary>
    private static void SetWorkerThreads(string[] names, int value)
    {
        foreach (string name in names)
        {
            Environment.SetEnvironmentVariable(name, value.ToString());
        }
    }

    private static void SetWorkerThreads(string[] names, string?[] values)
    {
        for (int i = 0; i < names.Length; i++)
        {
            Environment.SetEnvironmentVariable(names[i], values[i]);
        }
    }

    /// <summary><c>document.Converter.convert</c>: the pages of one file, in page order.</summary>
    private static List<(int Index, OcrPage Page)> ReadFile(
        string path, int maxPages, string? password, PdfConverter pdf, ImageConverter images)
    {
        if (!OrtOcr.IsPdf(path))
        {
            return images.ConvertFrames(path, maxPages).ToList();
        }

        using PdfDocument doc = PdfConverter.Open(path, password);
        int count = doc.PageCount;
        if (maxPages != 0)
        {
            count = Math.Min(count, maxPages);
        }

        var pages = new List<(int Index, OcrPage Page)>();
        for (int i = 0; i < count; i++)
        {
            pages.Add((i, SafePage.Read(() => pdf.PdfPage(doc, i))));
        }

        return pages;
    }

    /// <summary>ORT threads in each worker process (<c>parallel.THREADS_PER_WORKER</c>).</summary>
    private const int ThreadsPerWorker = 1;

    /// <summary>Fewer pages than this: one process, all cores on each page.</summary>
    private const int MinPoolPages = 4;

    /// <summary>Bytes a worker can reach (giant scans); caps the pool.</summary>
    private const long WorkerMemory = 1_500_000_000;

    /// <summary>
    /// <c>parallel._max_workers_for_memory</c>: the physical memory divided by
    /// <see cref="WorkerMemory"/>. Windows Python has no <c>os.sysconf</c> and returns
    /// <c>1 &lt;&lt; 30</c>, i.e. never the limit.
    /// </summary>
    private static int MaxWorkersForMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            return 1 << 30;
        }

        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return ram <= 0 ? 1 << 30 : Math.Max(1, (int)Math.Min(int.MaxValue, ram / WorkerMemory));
    }

    /// <summary>The reference's <c>--format json</c> line for one page.</summary>
    private static string PageJson(PyPath path, int index, OcrPage page)
        => PyJson.Object([
            ("file", PyJson.Str(path.ToString())),
            ("page", PyJson.Num(index)),
            ("source", PyJson.Str(page.Source)),
            ("rotation", PyJson.Num(page.Rotation)),
            ("skew", PyJson.Num(page.Skew)),
            ("note", PyJson.Str(page.Note)),
            ("lines", PyJson.Array(page.Lines.Select(LineJson))),
            ("markdown", page.Markdown is null ? PyJson.Null() : PyJson.Str(page.Markdown)),
            ("blocks", PyJson.Array(page.Blocks.Select(BlockJson))),
            ("timings", TimingsJson(page.Timings)),
        ]);

    /// <summary><c>dataclasses.asdict(Line)</c>.</summary>
    private static string LineJson(OcrLine line)
        => PyJson.Object([
            ("box", PyJson.Array(line.Box.Select(PyJson.Num))),
            ("text", PyJson.Str(line.Text)),
            ("conf", PyJson.Num(line.Confidence)),
        ]);

    /// <summary>One of <c>layout_post.page_blocks</c>' dicts.</summary>
    private static string BlockJson(LayoutBlock block)
        => PyJson.Object([
            ("label", PyJson.Str(block.Label)),
            ("bbox", PyJson.Array(block.Bbox.Select(PyJson.Num))),
            ("text", PyJson.Str(block.Text)),
        ]);

    /// <summary>The page's timings, in the order the pipeline filled them in.</summary>
    private static string TimingsJson(IReadOnlyDictionary<string, double> timings)
        => PyJson.Object(timings.Select(kv => (kv.Key, PyJson.Num(kv.Value))));
}
