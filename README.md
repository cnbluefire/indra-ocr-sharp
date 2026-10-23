# Indra.Ocr.Sharp

[![NuGet](https://img.shields.io/nuget/vpre/Bluefire.Indra.Ocr.Sharp.svg?logo=nuget)](https://www.nuget.org/packages/Bluefire.Indra.Ocr.Sharp)
[![Upstream Indra OCR](https://img.shields.io/badge/upstream-v1.1.2-blue)](https://github.com/datalab-to/indra/tree/v1.1.2)

A C# / .NET port of Datalab's **Indra OCR 1.1.2** (`indra-ocr`): the same command line, the same local
viewer, the same output — no Python, no PyTorch, no OpenCV.

- Upstream code: <https://github.com/datalab-to/indra> (Apache-2.0); this port targets
  **v1.1.2** / commit [`0e96b93`](https://github.com/datalab-to/indra/commit/0e96b93c6810f17cf8352acbb4487cc9e45b66d5)
- Model weights: <https://huggingface.co/datalab-to/indra-ocr> (modified OpenRAIL-M)

## About Indra OCR

Indra OCR is Datalab's 1M-parameter OCR model: it turns PDFs and images into markdown, plain
text or JSON with boxes, on the CPU. Its numbers belong to upstream, not to this port — about
76 MB of networks and language models, about 44 pages per second on 32 x86 cores, handwriting,
80+ scan languages, pages tilted by up to 45 degrees, light text on dark ground, and a bounding box
for every line and layout block. A digital PDF's own text layer is used where it passes the trust
checks, with OCR only where it is missing or misleading.

- Repository and full README: <https://github.com/datalab-to/indra>
- Models: <https://huggingface.co/datalab-to/indra-ocr>
- Documentation: <https://documentation.datalab.to>
- Current limitations and benchmark scope: upstream's *Limitations* section.

## About this port

This repository is a 1:1 behavioural port of the `indra-ocr` Python package — its command line,
its viewer and its library — to C# on .NET 10.

- **Same surface, same output.** Every option, exit code and message is ported from the
  reference, and deterministic output is byte-for-byte identical. The alignment harnesses in
  `tools/scripts/test/` run this port and the Python package on the same inputs and compare
  (83 CLI cases, 24 end-to-end asset cases, 20 viewer command-line cases, 12 HTTP cases). The
  few deliberate differences — a stable sort where numpy's `argsort` tie order is CPU-specific,
  for one — are written down in `docs/`.
- **ONNX Runtime through NuGet.** `Microsoft.ML.OnnxRuntime`; the model set is loaded once per
  run, as the reference builds its `InferenceSession`s.
- **No OpenCV, no Python.** The OpenCV operations the pipeline needs — fixed-point resize and
  warp, connected components, the deskew search — are implemented in managed C#.
- **The decoders are separate projects.** Image files are decoded with ImageSharp (pure
  managed); PDFs are rasterised and their text layer read with PDFium, bound by this port's own
  P/Invoke, which renders straight to grayscale. Core itself sees only pixels.
- **Packaged.** The three libraries are on NuGet as `Bluefire.Indra.Ocr.Sharp.Core`,
  `Bluefire.Indra.Ocr.Sharp.Pdf` and `Bluefire.Indra.Ocr.Sharp`, each with a `net10.0` and a
  `netstandard2.0` asset, so a .NET Framework application can reference them too. The assemblies
  and namespaces inside stay `Indra.Ocr.*`.
- **AOT-ready.** The three libraries are `IsAotCompatible` and both hosts `PublishAot`, so
  `dotnet publish -r <rid>` produces native binaries, one per host, with no runtime to install; the
  solution is globalization-invariant, so they need no ICU.
- **Same models.** The same Hugging Face repository and revision (`v1.1.0`) the Python package
  uses, downloaded on the first run (about 76 MB) and cached. `--model-dir` or
  `INDRA_OCR_MODEL_DIR` uses a local copy instead, and the math models download only with
  `--math`.

The executables keep the reference's names — `indra-ocr` and `indra-ocr-viewer` — because they
are part of the surface that has to match; `Indra.Ocr.Sharp` is this port's own name.

### Layout

| Path | Purpose |
|---|---|
| `src/Indra.Ocr.slnx` | The .NET solution (build and test entry point). |
| `src/Indra.Ocr.Core` | Core pipeline: models, detection, recognition, reading order, layout, text layer, and the model download. Consumes grayscale pixels only (`GrayImage`, a non-owning `ref struct` over 8-bit single-channel pixels); no image-codec or PDF dependency. |
| `src/Indra.Ocr.Pdf` | `PdfConverter`: PDF rasterisation + text layer over PDFium (bound by this port) — the only project that touches it. |
| `src/Indra.Ocr` | `ImageConverter`: encoded image files (ImageSharp) to grayscale, one page per frame. |
| `src/Indra.Ocr.Cli` | The `indra-ocr` console app, plus the argument parser and console helpers the viewer shares. |
| `src/Indra.Ocr.Viewer` | The `indra-ocr-viewer` local web viewer (ASP.NET Core minimal API). |
| `src/Indra.Ocr.Tests` | xUnit tests: the ported reference unit tests, the parity tests and the library's own. |
| `build/` | The release scripts: the NuGet packages, the native AOT publish and its smoke, the GitHub release. |
| `docs/` | The step-by-step port record; `PLAN.md` is the plan. |
| `tools/scripts/` | Helper scripts; `tools/scripts/test/` holds the alignment harnesses. |
| `resources/` | The upstream Python source and a local model copy (read-only, not in git). |

`Pdf` and `Indra.Ocr` reference `Core`; `Cli` references all three; `Viewer` references `Cli` and
all three — the two hosts share the parser, as the reference's `viewer.py` imports it from
`cli.py`; `Core` depends on none of them, and the two decoders never reference each other.

## Build

Requires the .NET 10 SDK (10.0.400 or newer).

```powershell
dotnet build src/Indra.Ocr.slnx     # 0 warnings (the AOT and trim analyzers are on)
dotnet test  src/Indra.Ocr.slnx     # 218 tests
```

Run the hosts from the source tree:

```powershell
dotnet run --project src/Indra.Ocr.Cli    -- paper.pdf --format text
dotnet run --project src/Indra.Ocr.Viewer -- --port 8765
```

Publish native AOT binaries (`PublishAot`; the RID has to be the machine's — native AOT links with
the platform's own toolchain and does not cross-compile):

```powershell
dotnet publish src/Indra.Ocr.Cli/Indra.Ocr.Cli.csproj       -c Release -r win-x64 -o out
dotnet publish src/Indra.Ocr.Viewer/Indra.Ocr.Viewer.csproj -c Release -r win-x64 -o out
```

For the release itself — one zip per RID, holding both hosts, the native libraries they share,
`LICENSE` and `NOTICE`, and smoked afterwards — use the scripts in `build/`:

```powershell
build/publish.ps1 -Rid win-x64      # the release ships win-x64 and win-arm64
```

On Linux the same scripts run in the release workflow — the real native AOT build and the platform
smoke — but the zips they produce stay job artifacts and are not attached to the release. A native
AOT binary links the glibc of the machine that built it, so one built on a current runner fails on
older distributions, and the OCR engine's own native library sets a floor no older anyway
(`docs/290-aot.md`). On Linux, build it here, or reference the three NuGet packages:

```shell
bash build/publish.sh --rid linux-x64     # what the Linux CI leg runs
```

On a machine without the platform toolchain (the MSVC linker + Windows SDK, or clang),
`INDRA_OCR_FAKE_AOT` publishes a trimmed self-contained single-file stand-in instead: everything
runs and is checked, but the artifact is not AOT and is named `..._notaot`, which every release gate
refuses.

## Command line

The first run downloads the models (about 76 MB) from Hugging Face and caches them.

```shell
indra-ocr paper.pdf                       # markdown to stdout
indra-ocr *.pdf --out results/            # one .md per input, all usable cores
indra-ocr scan.png --format json          # lines, boxes, layout blocks, confidences, timings
indra-ocr paper.pdf --format text         # plain text, one line per detected line
indra-ocr paper.pdf --mode ocr --timings  # ignore the text layer; per-page time on stderr
indra-ocr paper.pdf --keep-furniture      # keep page headers and footers in the markdown
indra-ocr *.pdf --out results/ --cores 8  # limit the cores (default: all usable ones)
indra-ocr paper.pdf --math                # write math as LaTeX (off by default)
indra-ocr paper.pdf --model-dir models/   # use a local model folder, download nothing
```

| Option | Meaning |
|---|---|
| `--out DIR` | write one file per input (`.md`, `.txt` or `.jsonl`) |
| `--format md\|text\|json` | output format (default `md`) |
| `--mode auto\|ocr\|text` | `auto` (default) reads the text layer where it passes the checks and OCRs elsewhere; `ocr` never reads it; `text` uses it even when the checks fail |
| `--no-image-ocr` | on text-layer pages, do not OCR text inside images |
| `--model-dir DIR` | folder with the models (default: `INDRA_OCR_MODEL_DIR`, else download) |
| `--cores N` (alias `--threads`) | cores to use, one worker process each (default: all usable cores) |
| `--dpi F` | render resolution for PDFs (default 200) |
| `--max-pages N` | read at most N pages per file |
| `--password P` | password of encrypted PDFs |
| `--keep-furniture` | keep page headers and footers in the markdown |
| `--math` | write math as LaTeX (`$...$`); slower on pages with math |
| `--timings` | print per-page source and time to stderr |

The CLI continues past files it cannot read and exits 1 if any input failed.

```shell
indra-ocr-viewer                           # http://localhost:8765 (Ctrl+C stops it)
indra-ocr-viewer --port 8765 --threads 8   # port, and ONNX Runtime threads
indra-ocr-viewer --dpi 300 --max-pages 50  # initial render resolution and page limit
```

| Option | Meaning |
|---|---|
| `--port N` | port (default 8765) |
| `--threads N` | ONNX Runtime threads (default 4) |
| `--dpi F` | initial PDF render resolution (default 200) |
| `--max-pages N` | initial page limit per document (default 20) |
| `--keep-furniture` | initially keep page headers and footers |
| `--math` | initially write math as LaTeX |
| `--model-dir DIR`, `--mode`, `--no-image-ocr` | as the CLI's: the model folder and the initial pipeline options |
| `--tesseract`, `--tesseract-lang L` | accepted for command-line parity, but this port does not implement the comparison: `--tesseract` fails at startup (`tesseract is not supported by this port`) and `/info` always reports `tesseract: false` |

Drop in a PDF or an image and each page shows its lines, layout blocks and reading order over
the page, the rendered markdown, the markdown source, the lines with their confidences, the JSON
the CLI writes, where the text came from and the time of each stage. Files stay on your machine.

## Using the library

The three libraries are on NuGet: add `Bluefire.Indra.Ocr.Sharp.Core` and the decoder you need.

```shell
dotnet add package Bluefire.Indra.Ocr.Sharp.Core   # the pipeline and the model download
dotnet add package Bluefire.Indra.Ocr.Sharp.Pdf    # PDFs (PdfConverter)
dotnet add package Bluefire.Indra.Ocr.Sharp        # image files (ImageConverter)
```

The assemblies and namespaces inside are `Indra.Ocr.*` (only the package ids carry the `Bluefire.`
prefix), every package ships both a `net10.0` and a `netstandard2.0` asset, and the details —
dependency ranges, what each package holds, and the .NET Framework caveats — are in
`docs/340-nuget-packages.md`.

`PdfConverter` and `ImageConverter` are the two halves of the reference's `document.convert`, and
`OrtOcr.IsPdf` dispatches on the file's own bytes, exactly as `pipeline.is_pdf` does.

```csharp
using Indra.Ocr.Images;    // ImageConverter
using Indra.Ocr.Onnx;      // ModelFolder
using Indra.Ocr.Pdf;       // PdfConverter, PdfDocument
using Indra.Ocr.Pipeline;  // OrtOcr, OcrPage, OcrLine

// Models: a local folder, or null for INDRA_OCR_MODEL_DIR and then a Hugging Face download.
string models = ModelFolder.Resolve(path: null, math: false);
using var ocr = new OrtOcr(models, threads: 4);

const string input = "paper.pdf";
if (OrtOcr.IsPdf(input))
{
    var pdf = new PdfConverter(ocr, dpi: 200.0, mode: "auto"); // auto | ocr | text
    using PdfDocument doc = PdfConverter.Open(input, password: null);
    for (int i = 0; i < doc.PageCount; i++)
    {
        OcrPage page = pdf.ConvertPage(doc, i);
        Console.WriteLine($"{i + 1}: {page.Source}, {page.Timings["total_ms"]:0} ms");
        Console.WriteLine(page.Markdown ?? page.Text);
        foreach (OcrLine line in page.Lines) // Box = x0, y0, x1, y1 in image pixels
        {
            Console.WriteLine($"  {line.Confidence:0.###}  " +
                              $"({line.Box[0]:0},{line.Box[1]:0})-({line.Box[2]:0},{line.Box[3]:0})  " +
                              $"{line.Text}");
        }
    }
}
else
{
    var images = new ImageConverter(ocr); // one page per frame (a multi-page TIFF has many)
    foreach ((int index, OcrPage page) in images.ConvertFrames(input))
    {
        Console.WriteLine($"{index}: {page.Text}");
    }
}
```

Notes:

- `pdf.ConvertPages(path, password, pages)` and `pdf.ConvertPage(path, password, index)` open the
  document themselves; `images.Convert(path)` takes the first frame only.
- `OcrPage` carries `Markdown`, `Text`, `Lines` (`Box`, `Text`, `Confidence`), `Blocks`
  (`Label`, `Bbox`, `Text`), `Source` (`ocr` | `text` | `hybrid` | `error`), `Note`, `Rotation`,
  `Skew` and the per-step `Timings`.
- Already have pixels? `ocr.Call(gray)` takes an `Indra.Ocr.Imaging.GrayImage` — a non-owning
  `ref struct` view over 8-bit single-channel pixels with a stride, so a padded buffer costs no
  copy. That is how the two decoders hand a page to Core.
- `ModelFolder.Resolve` downloads into the Hugging Face cache and keeps the reference's daily
  check. `ModelDownload.DownloadAsync` and `ModelFolder.ResolveAsync` are the asynchronous
  versions, and `ModelDownloadProgress` reports each file as it arrives, if you want to show it.

### On .NET Framework

The `netstandard2.0` assets are the same port and run on .NET Framework 4.6.2 and later, with three
things a consumer has to add (NuGet cannot arrange them): the native libraries are per-architecture,
the `netstandard2.0` assets' BCL dependencies need binding redirects, and ONNX Runtime's native
library is copied by a `build/` target that only runs for a direct reference.

```xml
<PropertyGroup>
  <PlatformTarget>x64</PlatformTarget>
  <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
  <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="[1.31.0,1.32.0)" />
  <!-- 4.6.x only: 4.7+ has the type, and the packages leave it out so it never collides with mscorlib. -->
  <PackageReference Include="System.ValueTuple" Version="4.5.0" />
</ItemGroup>
```

Two caveats, both deliberate:

- The `netstandard2.0` asset is functionally, not bit-for-bit, identical: .NET Framework has no
  fused multiply-add and no CRT `expf` behind `MathF.Exp`, and its `CharUnicodeInfo` tables are
  older than .NET 10's. The `net10.0` asset is the reference.
- `SixLabors.ImageSharp` is pinned below 3.0 (2.1.13 is the last Apache-2.0 release), which carries
  advisories fixed only in 3.x — under the Six Labors Split License. The port takes that knowingly,
  so you will see the same `NU1902`/`NU1903`/`NU1904` warnings and can suppress them the same way.

## License

This port keeps the licensing of the original project and its models; nothing here is
re-licensed.

- **Code: Apache-2.0** — see `LICENSE`. This repository is a derivative work of
  [datalab-to/indra](https://github.com/datalab-to/indra), which is Apache-2.0; the attribution
  and the notice of the original work are in `NOTICE`.
- **Model weights: modified OpenRAIL-M** — see `THIRD-PARTY-LICENSES/MODIFIED-OPENRAIL-M.txt`,
  the same licence the models carry on [Hugging Face](https://huggingface.co/datalab-to/indra-ocr).
  The models are not distributed with this repository: they are downloaded at runtime from
  <https://huggingface.co/datalab-to/indra-ocr>.

## Development

The port is verified against the Python package itself, which is not part of this repository.

```powershell
tools/scripts/setup-pyenv.ps1            # once: the reference environment under tools/pyenv
tools/scripts/run-original.ps1 --help    # the original indra-ocr
tools/scripts/parity.ps1                 # the CLI parity harness
```

`run-original-viewer.ps1` starts the reference viewer (it blocks). `resources/` (not in git)
holds the upstream Python source (`resources/indra-src`) and a local model copy
(`resources/models`). The alignment harnesses in `tools/scripts/test/` run this port and the
reference on the same inputs and compare their output:

```powershell
tools/pyenv/Scripts/python.exe tools/scripts/test/cli_parity.py         # 83 cases
tools/pyenv/Scripts/python.exe tools/scripts/test/parity_sweep.py       # 24 end-to-end cases
tools/pyenv/Scripts/python.exe tools/scripts/test/viewer_cli_parity.py  # 20 cases
tools/pyenv/Scripts/python.exe tools/scripts/test/viewer_parity.py      # 12 HTTP cases
```

The packaging and release side is scripted too, and runs the same way locally:

```powershell
build/pack.ps1                        # the three NuGet packages, checked and smoked
$env:INDRA_OCR_FAKE_AOT = "1"         # no MSVC/Windows SDK: publish the trimmed stand-in
build/publish.ps1 -Rid win-x64        # one host zip for the RID, smoked afterwards
```

```shell
bash build/pack.sh
bash build/release.sh --dry-run       # what a release would attach, and the notes it would carry
```

`.github/workflows/ci.yml` runs the suite, the packages and a trimmed stand-in publish — on demand:
`workflow_dispatch` only, nothing on a push or a pull request, so a branch cannot spend the
repository's minutes. `.github/workflows/release.yml` does the real thing — real AOT, one runner per
RID, push to NuGet, GitHub release — manually and dry by default. The logic is all in `build/`; the
workflows only decide what runs where.

`INDRA_OCR_EXE` and `INDRA_OCR_VIEWER_EXE` point a harness at another binary. `docs/` records
every step of the port, `PLAN.md` is the plan, and `AGENTS.md` holds the working rules.
