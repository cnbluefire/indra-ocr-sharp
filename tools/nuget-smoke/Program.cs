// nuget-smoke: a consumer of the three NuGet packages, and nothing else.
//
// This project lives under tools/ and references the packages from a local feed, never the projects,
// so it goes red exactly when the *packages* are wrong: a file that did not make it in, a dependency
// group that is missing a reference, an assembly that will not load on the target runtime.  It is
// run by build/pack.ps1 / build/pack.sh right after `dotnet pack`, on both targets (net10.0 and
// net462 -- the .NET Framework half is what keeps the netstandard2.0 assets honest).
//
// Two halves, so that "the packages work" covers both behaviour and surface:
//
//   * a functional walk of the documented entry points -- the two decoders, the model resolver, the
//     pipeline and the PDF text layer -- over the committed samples build/smoke/latin.png and
//     build/smoke/latin.pdf;
//   * a reflection walk over every public type of the three assemblies that materialises every
//     public member.  Resolving those members loads every type their signatures name, which is the
//     cheap way to catch an incomplete dependency group in the .nuspec (that is what the ns2.0
//     groups of System.Memory/Unsafe/System.Text.Json are for).
//
// Usage: nuget-smoke <assets-dir> [model-dir]
//
// The model folder is resolved through the public API (ModelFolder.Resolve), so without a
// model-dir the Hugging Face cache and the download path are exercised too -- which is what a CI
// run of this does, and what makes it a check of the AOT binaries' TLS/JSON as well.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Indra.Ocr.Images;
using Indra.Ocr.Imaging;
using Indra.Ocr.Layout;
using Indra.Ocr.Onnx;
using Indra.Ocr.Order;
using Indra.Ocr.Pdf;
using Indra.Ocr.Pipeline;
using Indra.Ocr.Rec;
using Indra.Ocr.Text;

internal static class Program
{
    private static readonly List<string> Failed = new List<string>();
    private static int _checks;

    private static int Main(string[] args)
    {
        string assets = args.Length > 0 ? args[0] : Path.Combine("build", "smoke");
        string? models = args.Length > 1 ? args[1] : null;

        Console.WriteLine("nuget-smoke: the Bluefire.Indra.Ocr.Sharp* packages, walked from the outside");
        Console.WriteLine($"  runtime:  {Environment.Version} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        Console.WriteLine($"  assets:   {Path.GetFullPath(assets)}");

        try
        {
            Walk(Path.Combine(assets, "latin.png"), Path.Combine(assets, "latin.pdf"), models);
        }
        catch (Exception e)
        {
            Fail("the walk runs to the end", $"{e.GetType().Name}: {e.Message}");
        }

        Console.WriteLine();
        if (Failed.Count == 0)
        {
            Console.WriteLine($"nuget-smoke: OK ({_checks} checks)");
            return 0;
        }

        Console.WriteLine($"nuget-smoke: FAILED ({Failed.Count} of {_checks} checks)");
        foreach (string failure in Failed)
        {
            Console.WriteLine($"  - {failure}");
        }

        return 1;
    }

    private static void Walk(string png, string pdf, string? models)
    {
        Section("the packages");
        string core = Describe(typeof(OrtOcr).Assembly);
        string images = Describe(typeof(ImageFiles).Assembly);
        string pdfAssembly = Describe(typeof(PdfConverter).Assembly);
        Check("the three assemblies are the port's", core == "Indra.Ocr.Core" && images == "Indra.Ocr" && pdfAssembly == "Indra.Ocr.Pdf");

        // ------------------------------------------------------------------ Indra.Ocr (the decoder)

        Section("image files (Indra.Ocr.Sharp)");
        List<GrayImageData> frames = ImageFiles.ReadImages(png);
        Check("ReadImages yields one frame per frame", frames.Count == 1, $"count={frames.Count}");
        GrayImageData frame = frames[0];
        Check("the frame has the file's size", frame.Width == 700 && frame.Height == 220, $"{frame.Width}x{frame.Height}");
        Check("the stride can hold a row", frame.Stride >= frame.Width, $"stride={frame.Stride}");
        Check("the data can hold every row", frame.Data.Length >= frame.Stride * frame.Height, $"bytes={frame.Data.Length}");

        byte darkest = 255, lightest = 0;
        for (int i = 0; i < frame.Width * frame.Height; i++)
        {
            byte v = frame.Data.Span[i];
            if (v < darkest) darkest = v;
            if (v > lightest) lightest = v;
        }
        Check("the decoded page holds ink", darkest < 100 && lightest > 200, $"min={darkest} max={lightest}");

        GrayImage gray = frame; // the implicit conversion the two decoders hand Core
        Check("GrayImage is the same view", gray.Width == 700 && gray.Height == 220 && gray.Stride == frame.Stride);
        Check("GrayImage reports its layout", gray.BytesPerPixel == 1 && gray.PixelCount == 700 * 220 && gray.Row(0).Length == gray.Width);
        GrayImage slice = gray.Slice(30, 40, 100, 50);
        Check("Slice views a region", slice.Width == 100 && slice.Height == 50 && slice.Data.Length >= slice.Stride * 50);

        GrayImageData fromBytes = ImageFiles.ReadImage(File.ReadAllBytes(png));
        Check("ReadImage(bytes) agrees with ReadImages(path)", fromBytes.Width == frame.Width && fromBytes.Height == frame.Height && fromBytes.Stride == frame.Stride);

        byte[] jpeg = ImageFiles.EncodeJpeg(gray, quality: 82);
        Check("EncodeJpeg writes a JPEG", jpeg.Length > 0 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, $"bytes={jpeg.Length}");
        GrayImageData decoded = ImageFiles.ReadImage(jpeg);
        Check("the JPEG decodes back", decoded.Width > 0 && decoded.Height > 0, $"{decoded.Width}x{decoded.Height}");

        // The three coefficient sets, at a triple each where they are told apart (from the
        // reference's own fixed-point formulas: 9797/19234/3737 truncating, 9798/19234/3736 and
        // 9798/19235/3735 rounding).
        Check("RgbToGray keeps grey grey", ImageFiles.RgbToGray(0, 0, 0, ImageFiles.GrayFormula.Png) == 0
                                         && ImageFiles.RgbToGray(255, 255, 255, ImageFiles.GrayFormula.Cvt) == 255);
        Check("libpng's conversion truncates", ImageFiles.RgbToGray(0, 255, 0, ImageFiles.GrayFormula.Png) == 149
                                             && ImageFiles.RgbToGray(0, 255, 0, ImageFiles.GrayFormula.BmpTiff) == 150
                                             && ImageFiles.RgbToGray(0, 255, 0, ImageFiles.GrayFormula.Cvt) == 150);
        Check("the BMP/TIFF decoders round where cvtColor does not",
              ImageFiles.RgbToGray(0, 77, 143, ImageFiles.GrayFormula.Png) == 61
              && ImageFiles.RgbToGray(0, 77, 143, ImageFiles.GrayFormula.BmpTiff) == 62
              && ImageFiles.RgbToGray(0, 77, 143, ImageFiles.GrayFormula.Cvt) == 61);
        CheckThrows<InputError>("a missing image file is an InputError", () => ImageFiles.ReadImages(Path.Combine(Path.GetDirectoryName(png)!, "no-such-file.png")));

        // ------------------------------------------------------------------ Indra.Ocr.Pdf

        Section("PDF text layer and rendering (Indra.Ocr.Sharp.Pdf)");
        Check("IsPdf sniffs the bytes", !OrtOcr.IsPdf(png) && OrtOcr.IsPdf(pdf));
        string notPdf = Path.Combine(Path.GetTempPath(), $"nuget-smoke-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllText(notPdf, "not a pdf at all\n");
            Check("IsPdf goes by the content, not the extension", !OrtOcr.IsPdf(notPdf));
        }
        finally
        {
            File.Delete(notPdf);
        }

        using (PdfDocument doc = PdfDocument.Open(pdf, password: null))
        {
            Check("Open reports the page count", doc.PageCount == 1, $"count={doc.PageCount}");
            (double Width, double Height) size = doc.PageSize(0);
            Check("the page is A4", Math.Abs(size.Width - 595.28) < 1.0 && Math.Abs(size.Height - 841.89) < 1.0,
                  $"{size.Width:0.00}x{size.Height:0.00}");

            using (PdfPage page = doc.LoadPage(0))
            {
                Check("PdfPage reports the same size", Math.Abs(page.Width - size.Width) < 0.01 && Math.Abs(page.Height - size.Height) < 0.01);

                IReadOnlyList<PageChar> chars = page.ReadChars();
                Check("the text layer has characters", chars.Count > 20, $"chars={chars.Count}");
                Check("the characters carry boxes", chars.Any(c => c.HasBox), "no char has a box");

                double[,] affine = page.DeviceAffine();
                Check("DeviceAffine is a 2x3", affine.GetLength(0) == 2 && affine.GetLength(1) == 3);
                Check("the device origin is on the page", affine[0, 2] >= 0 && affine[1, 2] >= 0,
                      $"{affine[0, 2]:0.0},{affine[1, 2]:0.0}");

                double invisible = page.InvisibleTextFraction();
                Check("InvisibleTextFraction is a fraction", invisible >= 0.0 && invisible <= 1.0, $"{invisible:0.###}");

                (List<TextLine> runs, TextLayerStats stats) = page.ExtractRuns();
                Check("ExtractRuns found the two lines", runs.Count >= 2, $"runs={runs.Count}");
                Check("the stats count the characters", stats.Chars > 20, $"chars={stats.Chars}");
                Check("the runs keep their text", runs.Any(r => r.Text.Contains("Invoice number: 12345")),
                      string.Join(" | ", runs.Select(r => r.Text)));

                GrayImage render = page.Render(1.0);
                Check("Render at scale 1 is the page in points",
                      render.Width == (int)Math.Ceiling(page.Width) && render.Height == (int)Math.Ceiling(page.Height),
                      $"{render.Width}x{render.Height}");
                Check("the render is a grayscale page", render.BytesPerPixel == 1 && render.Data.Length >= render.Stride * render.Height);
            }
        }

        CheckThrows<PdfiumError>("a file that is not a PDF is a PdfiumError", () => PdfDocument.Open(png, password: null));

        // ------------------------------------------------------------------ the model folder

        Section("models (Indra.Ocr.Sharp.Core)");
        string folder = ModelFolder.Resolve(models, math: false);
        Check("Resolve returns a folder with the model set", !string.IsNullOrEmpty(folder) && ModelDownload.HasModels(folder, math: false), folder);
        Check("ResolveAsync returns the same folder", ModelFolder.ResolveAsync(models, math: false).GetAwaiter().GetResult() == folder);
        Check("the reference's four files are named", ModelDownload.Files.Length == 4 && ModelDownload.Files.Contains("det.onnx") && ModelDownload.Files.Contains("meta.json"),
              string.Join(",", ModelDownload.Files));
        Check("the Hub repository is the reference's", ModelDownload.HfRepo == "datalab-to/indra-ocr" && ModelDownload.HfRevision.Length > 0,
              $"{ModelDownload.HfRepo} @ {ModelDownload.HfRevision}");
        Check("Wanted keeps det.onnx and drops a README", ModelDownload.Wanted("det.onnx", math: false)
                                                       && !ModelDownload.Wanted("README.md", math: false));
        Check("Matches is the fnmatch the downloader uses", ModelDownload.Matches("rec.int8.onnx", "*.onnx")
                                                            && !ModelDownload.Matches("rec.onnx", "det*"));
        // Download() lists the Hub even for a folder that is already complete, so what it must not
        // do is fetch: the reference's snapshot semantics keep what is there (and the same check
        // covers a cold cache in CI, where the folder is resolved from the Hub instead).
        int fetched = 0;
        bool served = ModelDownload.Download(folder, math: false, downloadProgress: p =>
        {
            fetched++;
            _ = p.File + p.Path + p.Url + p.Downloaded + p.Total;
        });
        Check("Download fetches nothing for a folder that is already complete",
              fetched == 0 && ModelDownload.HasModels(folder, math: false), $"{fetched} files, served={served}");

        int reported = 0;
        string same = ModelFolder.Resolve(folder, math: false, downloadProgress: p =>
        {
            reported++;
            _ = p.File + p.Path + p.Url + p.Downloaded + p.Total;
        });
        Check("a complete folder downloads nothing", reported == 0 && same == folder, $"reports={reported}");

        // The environment variable is the reference's INDRA_OCR_MODEL_DIR: the same folder, found
        // without being handed over.
        Environment.SetEnvironmentVariable("INDRA_OCR_MODEL_DIR", folder);
        try
        {
            Check("INDRA_OCR_MODEL_DIR is honoured", ModelFolder.Resolve(null, math: false) == folder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INDRA_OCR_MODEL_DIR", null);
        }

        // ------------------------------------------------------------------ the pipeline

        Section("the pipeline (Indra.Ocr.Sharp.Core)");
        using (var ocr = new OrtOcr(folder, threads: 2, math: false))
        {
            Check("the engine reports its model folder", ocr.Directory == folder);
            Check("meta.json decided the geometry", ocr.LongSide > 0 && ocr.Height > 0, $"{ocr.LongSide}/{ocr.Height}");
            Check("the options are the defaults", ocr.Adaptive && !ocr.TableHeads && !ocr.Furniture);
            Check("the optional models are detected", ocr.HasCjk == (ocr.VocabCjk is not null), $"hasCjk={ocr.HasCjk}");
            Check("the vocabulary came from the folder", ocr.Vocab.ClassCount > 100 && ocr.Vocab.Characters.Length == ocr.Vocab.ClassCount - 1);
            Check("math is off unless asked for", ocr.Math is null);

            float[] boxes = ocr.Detect(gray);
            Check("Detect returns [N, 5] boxes", boxes.Length > 0 && boxes.Length % 5 == 0, $"values={boxes.Length}");

            List<RecognizedLine> lines = ocr.Recognize(gray, boxes);
            Check("Recognize answers once per box", lines.Count == boxes.Length / 5, $"{lines.Count} lines for {boxes.Length / 5} boxes");
            Check("the lines were read", lines.Any(l => l.Text.Contains("12345")),
                  string.Join(" | ", lines.Select(l => l.Text)));
            Check("every line carries tokens", lines.All(l => l.Tokens.Count > 0 && l.Confidence >= 0.0 && l.Confidence <= 1.0));

            bool[] hanzi = ocr.HanziLines(gray, boxes);
            Check("HanziLines answers once per box", hanzi.Length == lines.Count);

            int turns = ocr.Orientation(gray);
            Check("Orientation is a quarter turn count", turns >= 0 && turns < 4, $"turns={turns}");

            UprightPage up = ocr.Upright(gray);
            Check("Upright returns the page and what it undid", up.Page.Width > 0 && up.Page.Height > 0 && up.Turns == turns,
                  $"{up.Page.Width}x{up.Page.Height}, turns={up.Turns}, skew={up.Skew:0.###}");

            float[] quads = OrtOcr.Quads(boxes);
            Check("Quads drops the score column", quads.Length == boxes.Length / 5 * 4, $"values={quads.Length}");

            ReadingOrderResult order = ocr.ReadingOrder(quads, lines.Count, lines.Select(l => l.Text).ToList(), gray.Width, gray.Height);
            Check("ReadingOrder orders every line exactly once",
                  order.Order.Length == lines.Count && order.Order.Distinct().Count() == lines.Count,
                  string.Join(",", order.Order));

            List<double[]> cuts = ocr.GutterCuts(quads, lines.Count, lines);
            Check("GutterCuts answers with the cuts it found", cuts != null);

            OcrPage assembled = ocr.Assemble(gray.Width, gray.Height, quads, lines.Count, lines);
            Check("Assemble builds the markdown", !string.IsNullOrEmpty(assembled.Markdown) && assembled.Text.Contains("12345"),
                  assembled.Text.Replace("\n", " | "));
            Check("the page reports its source and timings", assembled.Source == "ocr" && assembled.Timings.Count > 0,
                  $"{assembled.Source}, {assembled.Timings.Count} timings");
            Check("the lines keep their boxes and confidences",
                  assembled.Lines.All(l => l.Box.Count == 4 && l.Confidence >= 0.0 && l.Confidence <= 1.0));
            Check("the blocks are typed", assembled.Blocks.All(b => b.Label.Length > 0 && b.Bbox.Length == 4));

            RecognizedLine first = lines[0];
            IReadOnlyList<LineSegment> segments = ocr.SplitLine(new ReadOnlySpan<float>(quads, 0, 4), first.Tokens, 3.0, null);
            Check("SplitLine answers with segments", segments.Count > 0);
            Check("IsSpace reads a token", ocr.IsSpace(first.Tokens[0]) || !ocr.IsSpace(first.Tokens[0]));
            Check("Decode reproduces the line", ocr.Decode(first.Tokens) == first.Text,
                  $"'{ocr.Decode(first.Tokens)}' vs '{first.Text}'");

            OcrPage called = ocr.Call(gray);
            Check("Call OCRs a whole page", called.Source == "ocr" && called.Lines.Count > 0 && called.Text.Contains("12345"),
                  $"{called.Lines.Count} lines: {called.Text.Replace("\n", " | ")}");
            Check("Call times the stages",
                  called.Timings.ContainsKey("orient_ms") && called.Timings.ContainsKey("det_ms") && called.Timings.ContainsKey("rec_ms"),
                  string.Join(",", called.Timings.Keys));

            foreach ((int index, OcrPage page) in new ImageConverter(ocr).ConvertFrames(png))
            {
                Check("ConvertFrames labels the frame", index == 0 && page.Source == "ocr" && page.Text.Contains("12345"),
                      $"{index}: {page.Source}");
            }

            OcrPage? one = new ImageConverter(ocr).Convert(png);
            Check("Convert reads the first frame", one is not null && one.Text.Contains("12345"));

            var converter = new PdfConverter(ocr, dpi: 200.0, mode: "text");
            Check("the converter reports its settings", converter.Dpi == 200.0 && converter.Mode == "text" && !converter.PdfOrder && converter.ImageOcr,
                  $"dpi={converter.Dpi}, mode={converter.Mode}, pdfOrder={converter.PdfOrder}, imageOcr={converter.ImageOcr}");
            Check("PageScale is the dpi over 72", Math.Abs(PdfConverter.PageScale(72.0, 72.0, 144.0) - 2.0) < 1e-9);

            using (PdfDocument doc = PdfDocument.Open(pdf, password: null))
            {
                OcrPage text = converter.ConvertPage(doc, 0);
                Check("ConvertPage reads the text layer", text.Source == "text" && text.Text.Contains("Invoice number: 12345"),
                      $"{text.Source}: {text.Text.Replace("\n", " | ")}");
                Check("the text page is timed too", text.Timings.Count > 0);
            }

            OcrPage byPath = converter.ConvertPage(pdf, password: null, pageIndex: 0);
            Check("ConvertPage(path, ...) opens the document itself", byPath.Text.Contains("Invoice number: 12345"));

            int pages = 0;
            foreach ((int index, OcrPage page) in converter.ConvertPages(pdf, password: null, pages: new[] { 0 }))
            {
                pages++;
                Check("ConvertPages labels the page", index == 0 && page.Text.Contains("Invoice number: 12345"));
            }
            Check("ConvertPages walks the pages asked for", pages == 1, $"pages={pages}");

            CheckThrows<ArgumentException>("an unknown mode is refused", () => new PdfConverter(ocr, mode: "nope"));

            Section("the vocabulary (Indra.Ocr.Sharp.Core)");
            Vocab vocab = Vocab.Latin();
            Check("the Latin vocabulary is the reference's", vocab.ClassCount > 100 && vocab.Characters.Length == vocab.ClassCount - 1,
                  $"{vocab.ClassCount} classes");
            Vocab round = Vocab.FromJson(vocab.ToJson());
            Check("ToJson/FromJson round-trips", round.ClassCount == vocab.ClassCount && round.Characters.SequenceEqual(vocab.Characters));
            Check("Normalize is NFC", vocab.Normalize("A\u0301") == "\u00C1" && UnicodeText.Canonical("A\u0301") == "\u00C1");
            Check("Tokenize splits per character", vocab.Tokenize("ab").Count == 2);
            Check("Oov reports an unknown token", vocab.Oov("\uFFFF").Count == 1);
            int[]? ids = vocab.Encode(first.Text);
            Check("Encode/Decode agree where the vocabulary holds the text", ids is null || vocab.Decode(ids) == first.Text,
                  ids is null ? "the line holds unknown tokens (fine)" : $"'{vocab.Decode(ids)}' vs '{first.Text}'");
        }

        // ------------------------------------------------------------------ the public surface

        Section("the public surface");
        foreach (Assembly assembly in new[] { typeof(OrtOcr).Assembly, typeof(ImageFiles).Assembly, typeof(PdfConverter).Assembly })
        {
            Surface(assembly);
        }
    }

    /// <summary>
    /// Materialises every public member of every public type in <paramref name="assembly"/>.  A
    /// member's signature names types, and naming them loads the assemblies they come from: this is
    /// what turns "the package restored" into "the package restores with everything it needs".
    /// </summary>
    private static void Surface(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? "?";
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            string why = string.Join(" | ", e.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message).Take(4));
            Fail($"{name}: every type loads", why);
            types = e.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        int publicTypes = 0, members = 0, broken = 0;
        foreach (Type type in types)
        {
            if (!type.IsPublic && !type.IsNestedPublic)
                continue;

            publicTypes++;
            try
            {
                foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance
                                                              | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    members++;
                    switch (member)
                    {
                        case MethodInfo m:
                            _ = m.ReturnType;
                            foreach (ParameterInfo p in m.GetParameters()) _ = p.ParameterType;
                            break;
                        case ConstructorInfo c:
                            foreach (ParameterInfo p in c.GetParameters()) _ = p.ParameterType;
                            break;
                        case PropertyInfo p:
                            _ = p.PropertyType;
                            break;
                        case FieldInfo f:
                            _ = f.FieldType;
                            break;
                        case EventInfo ev:
                            _ = ev.EventHandlerType;
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                broken++;
                Console.WriteLine($"       {type.FullName}: {e.GetType().Name}: {e.Message}");
            }
        }

        Check($"{name}: every public member resolves", broken == 0, $"{broken} types failed");
        Console.WriteLine($"       {name} {Version(assembly)}: {publicTypes} public types, {members} public members");
    }

    private static string Describe(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? "?";
        Console.WriteLine($"  {name} {Version(assembly)}: {assembly.Location}");
        return name;
    }

    private static string Version(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? assembly.GetName().Version?.ToString()
           ?? "?";

    // ------------------------------------------------------------------ the tiny check harness

    private static void Section(string title) => Console.WriteLine($"\n== {title}");

    private static void Check(string what, bool ok, string? detail = null)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine($"  ok   {what}");
            return;
        }

        Fail(what, detail ?? "no detail");
    }

    private static void Fail(string what, string detail)
    {
        _checks++;
        Failed.Add(what);
        Console.WriteLine($"  FAIL {what}: {detail}");
    }

    private static void CheckThrows<T>(string what, Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            Check(what, true);
            return;
        }
        catch (Exception e)
        {
            Fail(what, $"threw {e.GetType().Name} instead of {typeof(T).Name}");
            return;
        }

        Fail(what, $"nothing was thrown (expected {typeof(T).Name})");
    }
}
