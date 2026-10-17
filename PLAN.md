# PLAN.md — execution plan

Living plan for porting **indra-ocr** (Python) to C#. Kept in sync with `docs/`.
Status legend: `[ ]` todo · `[~]` in progress · `[x]` done · `[!]` blocked.

Source of truth for requirements: `AGENTS.md`. Step specs: `docs/NNN-*.md`.

## Decisions (locked)

- Imaging: pure managed C# (replicate OpenCV ops). `Core` carries no image-codec or PDF
  dependency — its only image input is 8-bit grayscale `GrayImage`, a `ref struct` view it
  never owns (outputs are caller-provided buffers). The encoded-image decoder (ImageSharp) is
  the `Indra.Ocr` project (`Images/ImageFiles.cs`, one owning `GrayImageData` per frame) and
  PDF rasterisation is `Indra.Ocr.Pdf`, which binds PDFium itself over the
  `bblanchon.PDFium.*` native packages and emits grayscale directly (`docs/335-multitarget-netstandard20.md`).
  The two are siblings: neither references the other, and the host picks one
  with `OrtOcr.IsPdf` (`docs/165-project-split.md`). ONNX: `Microsoft.ML.OnnxRuntime`.
- Parity: bit-exact for deterministic output; tolerance only for timings and <1e-3 floats.
- numpy's float32 `exp` is not the CRT's `expf`, so the port reproduces numpy's own vector
  kernel (`Indra.Ocr.NpyExp`, with hardware FMA) instead of calling `MathF.Exp`; see
  `docs/070-det-post.md`. numpy's contiguous float32 *reductions* are the pairwise tree, and the
  port transcribes it exactly (`Numerics.Sum32`/`Mean32` are bit-exact; step `155` restored the
  split rounding the transcription had lost); see `docs/090-orientation-deskew.md`.
- numpy's float32 `log` is the same story and gets its own kernel (`Indra.Ocr.NpyLog`), because
  the reading-order line features feed it to the order model. `np.logaddexp`'s float32 path, by
  contrast, *does* call the CRT's `expf`/`log1pf` (which is why `OrderPost` uses `MathF.Exp`);
  see `docs/110-order-post.md`.
- Order: Core + Cli first (aligned against `indra-ocr`), then Viewer.
- The viewer's Tesseract comparison is **out of scope** (user, 2026-10-16). The reference needs
  `pytesseract` and the `tesseract` binary and dies at startup without them; the port keeps the
  flag in its `--help`, refuses it at startup (exit 1) and never offers a backend, so `/info`
  always answers `"tesseract": false`. No further work planned (`docs/230-viewer-page.md`).
- Math (`--math`) ported last.
- The viewer's behaviour is verified **over HTTP against the live reference** by the Python
  harnesses (`viewer_parity.py`, `viewer_cli_parity.py`): they start each side and compare the
  answers, so the two sides run the same logic with only the launch command differing. C# viewer
  tests are kept only where they must run without Python (`ViewerPageTests` on the generated page
  bytes, `ViewerArgparseTests` on the help/version fixtures); the replay test of the engine
  (`ViewerEngineTests`) was removed as redundant (user, 2026-10-16).
- `resources/` read-only and gitignored; binaries/temp gitignored.

## Step plan

### Phase 0 — Foundation
- [x] `000` Governance & layout — `AGENTS.md`, `PLAN.md`, `.gitignore`, `docs/` skeleton.
- [x] `010` Solution, projects, build props, NuGet refs, run-original helper.
- [x] `012` Project layout: everything .NET under `src/`; `Indra.Ocr.Pdf` split out; `Core`
  takes a `GrayImage` view and drops ImageSharp.
- [x] `015` Test scaffolding — fixtures, comparator, reference runner.

### Phase 1 — Pure-managed numerics & CV
- [x] `020` NumericOps (numpy parity).
- [x] `030` Gray image types + host image codecs (OpenCV gray weights).
- [x] `040` OpenCV primitives: resize, getRotationMatrix2D, warpAffine, CCL(4).
- [x] `050` Vocab + Unicode helpers.

### Phase 2 — ONNX pipeline
- [x] `060` ORT session infra + meta/vocab load.
- [x] `070` `det_post` — `page_to_input` + `decode`; numpy's float32 `exp` reproduced
  bit-exactly (`NpyExp`). `encode_targets` is training-only and intentionally omitted
  (`docs/070-det-post.md`).
- [x] `080` `rec_preproc` — `crop_line`, `pad_batch`, `width_batches`
  (`docs/080-rec-preproc.md`).
- [x] `085` `line_mask` — CenterMask windowing + `crop_lines`, with an `IMaskModel`
  seam so the masking is testable although the shipped set has no `mask.onnx`
  (`docs/085-line-mask.md`).
- [x] `090` orientation + deskew — `orient.onnx` quarter turns (`IOrientModel`), the
  `estimate_skew` search (numpy's `arange` and float32 reductions reproduced), `rotate_page`,
  `np.rot90`, `unrotate_box` and `upright` (`docs/090-orientation-deskew.md`).
- [x] `095` adaptive detect — `det.onnx` behind an `IDetModel` seam, `_detect_at`/`detect`
  (`DetectAt`, `AdaptiveLongSide`, `Run`), including the float32 median, the 32-grid candidate,
  the 2048 cap and the NEP 50 scale/clip tail (`docs/095-adaptive-detect.md`).
- [x] `100` recognize — `recognize` + `hanzi_lines` (`IRecModel`/`IScriptModel` over both
  recognizers and the script classifier), the crop/route/batch loop, the CTC collapse and the
  token → page-x mapping (`docs/100-recognize.md`).
- [x] `110` `order_post` — `line_features`, `decode_order`, `order_top_down`, `order_xycut`,
  `_cluster` and `decode_blocks_order`, plus numpy's vectorised float32 `log` (`NpyLog`) and the
  Python `isdigit`/`isupper`/`isspace` predicates (`docs/110-order-post.md`). Training-only
  helpers (`kendall_tau`, `successor_acc`, `block_class`, `label_order`) intentionally omitted.
- [x] `115` `reading_order` — the driver (`IOrderModel`/`OnnxOrderModel` over `order.onnx`, the
  XY-cut fallback for short pages and missing models, the pairwise vs layout-head choice and the
  `table_heads` gate) (`docs/115-reading-order.md`).
- [x] `120` `layout_post` — the same-block split, the per-class rendering (section headers,
  paragraphs with the hyphen/CJK joins, list groups, the geometric HTML table and the one from the
  table heads), the display-equation path, the furniture rules and `page_blocks`, plus Python's
  `round` (`Numerics.RoundHalfEven`) and `str.strip` (`UnicodeText.Strip`)
  (`docs/120-layout-post.md`).
- [x] `130` `assemble` — the `SPLIT_GAP` / gutter-cut line splitting (`gutter_cuts`, `split_line`),
  the table-head cell re-split with the second order pass, the text-layer `content_order` override
  (`order_backtracks`, `pdf_order_coherent`) and the `Line`/`Page` assembly (with `OcrLine`,
  `OcrPage`) (`docs/130-assemble.md`).
- [x] `135` image inputs — folded into `030`: `Indra.Ocr/Images/ImageFiles.cs` decodes files to
  `List<GrayImageData>` (an owning frame per image frame), covered by `ImageDecodeTests`.

### Phase 3 — PDF + Document
- [x] `140` `Indra.Ocr.Pdf` PDFium backend — document open (path/bytes/password) with
  pypdfium2's error text, page size, the grayscale render (`FPDFBitmap_Gray`, packed, white,
  `FPDF_ANNOT|FPDF_GRAYSCALE`), the text layer's characters, the `FPDF_PageToDevice` affine and
  the invisible-text share (`docs/140-pdfium.md`; the `ITextDocument`/`ITextPage` seam it first
  introduced was deleted in `165`).
- [x] `145` Unicode bidi class table (`unicodedata.bidirectional` parity, used by
  `document.py`): 2 300 generated ranges, checked at every code point
  (`docs/145-bidi-classes.md`).
- [x] `150` `textlayer` — the text-layer runs and the verdict on them: the uprightness test
  through the page affine, the bad-character test (`unicodedata.category`), the
  invisible-text / broken-font-map / no-ink checks and their messages, in Core
  (`Indra.Ocr.Text.TextLayerReader`, over `PdfPage.ExtractRuns`; `docs/150-textlayer.md`).
- [x] `155` `OrtOCR` — the whole-page pipeline over a model folder: turn upright
  (`orientation`/`upright`), `detect`, `recognize` (incl. `hanzi_lines`), `assemble`
  (`gutter_cuts`/`split_line`/`reading_order`/`_decode`), `__call__`'s box mapping and timings
  (`docs/155-ortocr.md`).
- [x] `160` `document` Converter — `PdfConverter` (text layer where sound, OCR elsewhere, the
  timings merge, the error paths) and `ImageConverter` (every frame is a page), with `SafePage`,
  `IsPdf` and the run-level helpers (`readable`, `right_to_left`, `assign_runs`)
  (`docs/160-document.md`).
- [x] `165` Three-way split — `Indra.Ocr` (ImageSharp, `GrayImageData`), `Indra.Ocr.Pdf`
  (PDFium + `ExtractRuns`), `Core` (every algorithm over a result); `src/Shared/`,
  `ITextPage`/`ITextDocument`, `GrayImageConsumer` and the callback `ReadImages` deleted
  (`docs/165-project-split.md`).
- [x] `170` `models` — `ModelDownload` (Core): fetch the model set from the Hugging Face repo
  into a folder the caller names, with the reference's patterns, sizes and failure wording
  (`docs/170-models.md`).

### Phase 4 — CLI
- [x] `180` CLI — the argparse port (usage line, abbreviations, `--opt=v`, `--`, error wording,
  exit codes), the `Py*` helpers (path/number/float/text/JSON spelling), the model-folder policy
  (`models.model_dir`, the Hub cache reused) and the output (`md`/`text`/`json`, `--out`,
  `--timings`, the error lines). 65 argv cases byte-identical against the reference
  (`tools/scripts/test/cli_parity.py`); `docs/180-cli.md`.
- [x] `190` `convert_many` — the worker-process pool: a worker is this same program started with
  `INDRA_OCR_WORKER=1` (the reference's spawn context and `initializer=_init`), its own binary
  line protocol, one task per worker pulling from a shared cursor with the results published by
  index (`ex.map`'s order), `THREADS_PER_WORKER`, `WORKER_MEMORY`, `MIN_POOL_PAGES`, the serial
  path and the broken-pool fallback. 12 pool cases byte-identical against the reference (77 in the
  harness now); `docs/190-parallel.md`.
- [x] `195` CLI alignment harness — the machinery (`tools/scripts/test/parity.py`:
  `Case`, `Harness`, `Report`, the first-difference diff, parallel `--jobs`, name/glob
  selection) with the command line's 77 cases on top (`cli_parity.py`), the generated inputs
  built on demand, and `tools/scripts/parity.ps1` as the short way in; `docs/195-harness.md`.
  The viewer's HTTP harness (step `240`) reuses the machinery with its own commands.

### Phase 5 — Math
- [x] `200` MathReader + integration — `math.py`'s classifier and MathRec (`Indra.Ocr.Maths`:
  `MathTensor`/`IMathModel`/`MathReading`/`MathReader`, `OnnxMathModel`, `MathModels.Load`) and its
  three call sites: the OCR path (`Recognizer`'s math hook), the text-layer path
  (`PdfConverter.ReadMath` + `math_ms`) and `OrtOcr(math: true)` (the shared `SessionOptions`, the
  `FileNotFoundException`). `fixtures/math.json` (15 `detokenize` cases, the real four graphs over
  the math page, four stand-in graphs that assert the decoding loop's invariants), `MathReaderTests`
  (21), six new harness cases (83 in all) and the float64 pairwise reduction (`Numerics.Sum64`/
  `Mean64`, now pinned against numpy) (`docs/200-math.md`).

### Phase 6 — Viewer
- [x] `205` Shared helpers move into Core — `Py*` (namespace `Indra.Ocr.Py`) and `ModelFolder`
  (namespace `Indra.Ocr.Onnx`) leave the CLI, because the viewer is a second host that must
  agree on `--model-dir`, the environment variable, the cache and the download; this supersedes
  the earlier "the model folder lives in the CLI" note. **Superseded by `245`** for the
  helpers (they are the CLI's again; only `ModelFolder` and `PathHelper` stay in Core)
  (`docs/205-core-shared.md`).
- [x] `210` Viewer Engine — `viewer.Engine`: the uploads it keeps (five, oldest first, temp
  files and all), one warmed-up recognizer per `(math, furniture)`, and `page` under a single
  lock (PDFium is not thread-safe) — the preview (render at the converter's scale, turn upright,
  shrink to 1600 px, JPEG q82), the boxes scaled onto it, the Markdown, and the CLI-shaped
  `json`. Twelve recorded pages, two error texts and the five-upload limit match the reference
  (`docs/210-viewer-engine.md`).
- [x] `215` Argparse engine + shared options move into Core — `ArgumentParser`/`Namespace`/
  `ArgparseExit` (namespace `Indra.Ocr.Argparse`) and the new `SourceArgs` (the reference's
  `add_source_args`/`source_options` in one place), because the viewer is a second host with the
  same `--help`/`--version`/error surface and the same four source options; the CLI's 77 cases are
  unchanged by the move. **Superseded by `245`**: the engine is the CLI's again
  (namespace `Indra.Ocr.ArgumentParsing`) and the viewer references the CLI
  (`docs/215-argparse-core.md`).  *Superseded by `245`: the engine is the CLI's again.*
- [x] `220` Viewer HTTP layer — the reference's five routes over ASP.NET Core's minimal API
  (`GET /`+`/index.html`, `GET /info`, `POST /upload`, `POST /page`, a `404 not found` fallback),
  the answers written by a source-generated `System.Text.Json` context in the reference's key order,
  the reference's `error: …` + `400 {"error": …}` on anything that throws, and the banner. The JSON
  bytes are not `json.dumps` (the plan's AOT-friendly serializer), so the comparison is semantic:
  key order and values match, the spelling of numbers and whitespace does not. `--help`/`--version`
  are byte-identical; `viewer_parity.py` drives both servers over nine request cases and the console
  lines (`docs/220-viewer-api.md`).
- [x] `230` The viewer's page — `viewer.PAGE` (22 kB of HTML/CSS/JS) and `viewer.LOGO` (the
  base64 Datalab mark) taken from the reference module by `gen_viewer_page.py` and written as C#
  literals, `Render()` substituting the logo. The reference serves its page exactly as spelled,
  and CPython normalises the line endings inside string literals, so the answer is LF — which the
  port keeps. `fixtures/viewer_page.html` records the body and the `page` case of
  `viewer_parity.py` compares it over the wire byte for byte (`docs/230-viewer-page.md`).
- [x] `240` The viewer's parity surface — `viewer_cli_parity.py` (20 command-line cases on
  `parity.Harness`: `--help`/`--version`, the argparse errors, the abbreviation and `=` forms, the
  model folder with and without `--math`, the source options and the refused `--tesseract`) and the
  `concurrent` case in `viewer_parity.py` (four overlapping `POST /page`, which is what the
  engine's single lock has to survive). It found a real bug: the reference's *viewer* prints its
  `ModelError` to stdout while the command line prints it to stderr, and the port had copied the
  CLI's choice (`docs/240-viewer-http.md`).

### Phase 7 — Hardening
- [x] `245` Host layout — the argparse port and the Python-spelling helpers go back to
  `Indra.Ocr.Cli` (namespaces `Indra.Ocr.ArgumentParsing` and `Indra.Ocr.Cli.PythonInterop`,
  C# names: `ArgumentAction`/`ArgumentType`/`ArgumentArity`/`ParsedArguments`/
  `ArgumentParserExit`/`SourceArguments`, `FloatFormat`/`PythonJson`/`ArgumentNumber`/
  `PythonText`/`ConsoleOutput`) and `Indra.Ocr.Viewer` references `Indra.Ocr.Cli`, which is
  the reference's own shape (`viewer.py` imports from `cli.py`); `PyPath` is gone — a
  path is a `string` and `Indra.Ocr.PathHelper` (Core) is the one place that knows Python's
  spelling of one. No behaviour change: 177 tests, 83/83 + 20/20 + 11/11 parity cases
  (`docs/245-host-layout.md`).
- [x] `247` One console class — `PythonInterop` is gone: its four classes (`FloatFormat`,
  `PythonJson`, `PythonText`, `ConsoleOutput`) are one class,
  `Indra.Ocr.ArgumentParsing.ConsoleOutput`, which is what both hosts write with (the streams
  and `os.linesep`, Python's float `repr`/`format(x, '.Nf')`, `json.dumps`). No behaviour
  change: 177 tests, 83/83 + 20/20 + 11/11 (`docs/247-console-output.md`).
- [x] `250` The reference's own unit tests — `tests/test_inputs.py` (10) and
  `tests/test_textlayer.py` (17) ported case for case into
  `ReferenceInputsTests`/`ReferenceTextLayerTests` (26 tests; `test_version_flag` stays
  with the command-line surface that already covers it), over the seams the port has
  (`SafePage.Read`, `IOrientModel`/`IRecModel`/`IScriptModel`, a local `HF_ENDPOINT`).
  Two parity fixes came out of it: `HF_ENDPOINT` was ignored, and the daily model-cache
  stamp was touched on the offline path (the reference only stamps after a real Hub
  call). 203 tests, 83/83 + 20/20 + 11/11 (`docs/250-reference-tests.md`).
- [x] `260` End-to-end parity sweep — every PNG the reference ships under
  `resources/indra-src/assets` (the four example pages, the two benchmark charts, the logo)
  through both commands in `md`/`text`/`json`, then all seven at once through the worker pool
  (parallel and serial) and through `--out` (`tools/scripts/test/parity_sweep.py`, 24 cases),
  plus two of the example pages through the viewer's API (`viewer_parity.py`'s `assets` case).
  All of it matches byte for byte; no finding (`docs/260-parity-sweep.md`).

### Phase 8 — Last
- [x] `270` ONNX backend benchmark: `Microsoft.ML.OnnxRuntime` vs `Lokad.Onnx` on the Indra
  models, as a BenchmarkDotNet project under `temp/` (see `docs/270-onnx-benchmark.md`).
  Result: Lokad.Onnx 0.3.0 cannot execute `det.onnx` (`Softplus`) or `rec.onnx` (`ArgMax`) at
  all, even after rewriting `HardSwish`/`Xor`/`Mod`/`BatchNormalization`/`DepthToSpace`; where
  it does run it is 1.6–2.8× slower than ORT on the same graph and allocates ~63 MB per
  `orient` call. `Microsoft.ML.OnnxRuntime` stays the reference backend.

### Phase 9 — Performance
- [x] `280` the deskew search's threads: `EstimateSkew`/`Upright` take the host's own thread
  count (the reference's `cv2.setNumThreads`: `--cores` in one process, 1 in a pool worker,
  `--threads` in the viewer) and score the 32 angles in parallel, still taking the first
  maximum in index order, so the output stays bit-exact — 3.3× on the deskew (174 → 51 ms),
  the page 599 → 481 ms on `latin.png`, and the whole process now ahead of the reference's.
  ReadyToRun (plain and composite) and `TieredCompilation=false` were all measured and all
  lose: the app is not startup-bound, its ~0.9 s before the page is ONNX Runtime's own
  session setup, so the default Release publish stays (`docs/280-deskew-threads.md`).

### Phase 10 — NativeAOT
- [x] `290` the solution is AOT-ready: the three class libraries set `IsAotCompatible` and
  the two hosts `PublishAot`, so every build runs the trim and AOT analyzers and
  `dotnet publish -r <rid>` makes a native binary. `IL3000` in the worker pool is fixed (the
  assembly path is `AppContext.BaseDirectory` + the entry assembly's name, not
  `Assembly.Location`, which is empty in a single-file/AOT app — and a `dotnet` host is the
  one case such a build cannot be in); ImageSharp 2.1.13's `IL2104`/`IL3053` summaries are
  suppressed with the reason written down, its whole warning set being one `IL2075` in an
  EXIF tag-description helper the port never reaches. No MSVC toolchain here, so the AOT
  publish itself is left to a machine that has one; what is verified here is a **trimmed
  self-contained** publish of both hosts — 0 warnings, and all four harnesses green against
  those binaries (`INDRA_OCR_EXE` / `INDRA_OCR_VIEWER_EXE` point them at any build)
  (`docs/290-aot.md`).

## Current state

- Environment verified: dotnet 10.0.400, uv 0.12.7, Python ref env at `tools/pyenv`.
- Original CLI `--help` captured for both `indra-ocr` and `indra-ocr-viewer`.
- ONNX signatures inspected; `det_long_side=1024`, `rec_height=32`, vocab 1677 (latin) /
  9967 (cjk). `order.onnx` has 4 outputs (incl. table heads); `table_heads` is off by default.
- Layout settled: five projects under `src/` — `Indra.Ocr` (ImageSharp), `Indra.Ocr.Pdf`
  (PDFium), `Indra.Ocr.Core` (ONNX only), `Indra.Ocr.Cli`, `Indra.Ocr.Viewer`. `Core` is free of
  ImageSharp/PDFium and consumes grayscale `GrayImage`; the two decoders are siblings
  (`docs/165-project-split.md`). `src/Shared/` no longer exists.
- Steps `000`–`165` done: numerics, gray decode/encode parity, bit-exact OpenCV primitives,
  the recognizer vocabulary, the ORT model set, the LineNet pre/post-processing (`DetPost`,
  with numpy's float32 `exp` reproduced bit-exactly), the line crop rule + batching
  (`RecPreproc`), the CenterMask windowing (`LineMask`), the page uprighting
  (`Orientation`: orient.onnx turns, deskew search, rotations), line detection
  (`Detect`: one LineNet pass, the adaptive second pass and the scale/clip tail), line
  recognition (`Recognizer`: cropping, the hanzi routing, width batching, the CTC collapse and
  the token → page-x mapping), the reading-order building blocks (`OrderPost`: line features
  over numpy's float32 `log`, pairwise-order decoding, the top-down and XY-cut baselines, the
  average-linkage block clustering and the layout-head block decoder), the reading-order
  driver (`ReadingOrder` over `order.onnx`, with the XY-cut fallback and the table-heads gate),
  the page layout (`LayoutPost`: the block split, paragraph/list/section rendering, the geometric
  and head-driven HTML tables, the display-equation path, `page_blocks`/`page_markdown`, plus
  Python's `round` and `str.strip`), the page assembly (`Assembler`: the wide-gap and
  gutter-cut line splitting, the table-head cell re-split, the text-layer content-order override
  and the `Line`/`Page` result), and the PDFium backend (`Indra.Ocr.Pdf`: document open with
  pypdfium2's error text, page size, bit-exact grayscale renders, the text layer's characters,
  the `FPDF_PageToDevice` affine and the invisible-text share), the text-layer policy
  (`TextLayerReader`: the runs and the trust verdict — uprightness through the affine, the
  bad-character test, invisible text, the ink check), the whole-page driver (`OrtOcr`: upright →
  detect → recognize → assemble, with the page's boxes, markdown, blocks, source and timings), the
  document layer (`PdfConverter` + `ImageConverter`: the text layer where it can be trusted and
  OCR elsewhere, `page_scale`, `readable`/`right_to_left`/`assign_runs`, `_safe`'s error pages and
  the `is_pdf` dispatch), the three-way project split (`docs/165-project-split.md`) and the model
  download (`ModelDownload`, `docs/170-models.md`).
- Step `180` done: the command line runs end to end — an argparse port (`src/Indra.Ocr.Cli/Argparse/`)
  whose help, errors and exit codes are byte-identical to the reference's, the `Py*` helpers that
  spell Python's paths, numbers, floats, newlines and JSON, the model-folder policy (argument →
  `INDRA_OCR_MODEL_DIR` → the Hub cache, reusing the reference's own download) and the output
  (`md`/`text`/`json` to stdout or `--out`, the error lines, `--timings`).
  `tools/scripts/test/cli_parity.py` compares 65 argv cases byte for byte against the reference —
  all matching — and `ArgparseTests` checks the help against the reference's captured text without
  Python (`docs/180-cli.md`).
- Step `190` done: `convert_many`'s worker-process pool — one single-threaded worker process per
  core (a worker is the CLI itself, started with `INDRA_OCR_WORKER=1`, speaking a private binary
  line protocol), the results published by unit index in `ex.map`'s order, the serial path for
  fewer than `MIN_POOL_PAGES` pages or fewer than two workers, and the fallback to this process
  when the workers stop. 12 pool cases — including a six-page PDF, two workers over six pages and
  a mixed run with plan errors and a worker's error — are byte-identical to the reference
  (`docs/190-parallel.md`).
- Step `195` done: the comparison harness is official — `tools/scripts/test/parity.py` holds the
  machinery (`Case`, `Harness`, `Report`, the first-difference diff, `--jobs` parallelism, name and
  glob selection, a per-side timeout) and `cli_parity.py` the command line's 77 cases on top of it.
  Inputs are built on demand, a case's output directory is emptied first, `parity_selftest.py`
  checks the machinery against synthetic mismatches, and `tools/scripts/parity.ps1` is the short way
  in. The viewer's HTTP harness (step `240`) reuses the same machinery (`docs/195-harness.md`).
- Step `205` done: the `Py*` helpers (namespace `Indra.Ocr.Py`) and `ModelFolder`
  (`Indra.Ocr.Onnx`) moved from the CLI into Core, because the viewer is a second host that must
  agree on `--model-dir`, `INDRA_OCR_MODEL_DIR`, the Hub cache and the download; the earlier
  "the model folder lives in the CLI" decision is superseded (`docs/205-core-shared.md`).  *Superseded by `245` for the helpers.*
- Step `210` done: `Indra.Ocr.Viewer`'s engine — `Upload` (the `%PDF-` prefix, the page count,
  `uuid4().hex[:12]` ids, five uploads kept, the reference's `not a PDF or a readable image`
  text) and `Page` (one warmed-up recognizer per `(math, furniture)`, everything under one lock
  for PDFium's sake, the preview rendered at the converter's scale → turned upright → shrunk to
  1600 px → JPEG q82, the boxes scaled by the same factor, the CLI-shaped `json` and the console
  line). `fixtures/viewer.json` records twelve pages, three error texts and the page counts from
  the reference's own `Engine`; the engine is verified against the **live** reference by
  `viewer_parity.py` (the same twelve option sets, the six inputs and the three error texts over
  HTTP, field by field — the JPEG bytes and the timing values excepted). The C# replay test
  (`ViewerEngineTests`) was removed by the user's decision (2026-10-16): it asserted the same
  fields against a snapshot instead of the reference (`docs/210-viewer-engine.md`).
- Step `215` done: the argparse engine (`ArgumentParser`, `Namespace`, `ArgparseExit`) moved from
  the CLI into Core (namespace `Indra.Ocr.Argparse`), with `SourceArgs` holding the option group
  both hosts share (the reference's `add_source_args`/`source_options`); the CLI's private helper
  and its inline reading are gone, and its 77 parity cases are unchanged
  (`docs/215-argparse-core.md`).
- Step `220` done: the viewer's HTTP layer — `GET /`+`/index.html`, `GET /info`, `POST /upload`,
  `POST /page` and a `404 not found` fallback over the minimal API, the answers written by the
  source-generated `ViewerJson` context in the reference's key order, the reference's `error: …`
  console line + `400 {"error": …}` whenever a handler throws, and the banner. `--help`/`--version`
  are byte-identical to the reference's; `ViewerArgparseTests` (6) and `viewer_parity.py` (9 cases,
  both servers driven over the same requests) pass. The JSON bytes are deliberately not
  `json.dumps`'s (whitespace, non-ASCII escaping, the spelling of integral doubles), so the HTTP
  comparison is semantic — every key, its position and every value (`docs/220-viewer-api.md`).
- Step `230` done: the viewer's page — `viewer.PAGE` (22 kB of HTML/CSS/JS) and `viewer.LOGO` (the
  base64 Datalab mark) are taken from the reference module by
  `tools/scripts/test/gen_viewer_page.py` (writing `ViewerPageAssets.Generated.cs` and the
  `fixtures/viewer_page.html` the C# test reads) and `ViewerPage.Render()` puts the logo where
  `__LOGO__` is. The reference serves its page exactly as spelled in its source, and CPython
  normalises the line endings inside string literals, so the answer is LF; the port keeps it.
  `ViewerPageTests` (2) compare the page with the record, and the harness's `page` case compares
  `/` and `/index.html` byte for byte over the wire (`docs/230-viewer-page.md`).
- Step `240` done: the viewer's command line compared against the reference by a second harness
  (`viewer_cli_parity.py`, 20 cases on the shared `parity.Harness` — the help, the version, the
  argparse errors, `--opt=v` and abbreviations, a model folder that lacks the files, with and
  without `--math`, and the refused `--tesseract`) plus a `concurrent` case in `viewer_parity.py`
  that fires four overlapping `POST /page` at both servers and compares the answers as a multiset.
  It found a real bug: the reference's viewer prints its `ModelError` to **stdout** (the command
  line prints the same error to stderr) and the port had copied the CLI's choice — fixed, with
  `nofolder` as the regression test (`docs/240-viewer-http.md`).
- Step `200` done: the math step (`--math`) — `Indra.Ocr.Maths.MathReader` (the classifier and
  MathRec, the attention decoder: `MathTensor`/`IMathModel`/`MathReading`, the `detokenize` rule,
  the width-batched decoding loop with the cache feedback, the EOS/early-stop decisions, the
  float32 peak probability and the float64 mean), `OnnxMathModel`/`MathModels.Load` (the four
  graphs plus `math_meta.json`, over the caller's shared `SessionOptions`) and its three call
  sites: `Recognizer`'s hook (a confident reading with `$` replaces an OCR line with one token),
  `PdfConverter.ReadMath` (the text-layer path, `math_ms`, the line keeping its content-order
  index) and `OrtOcr(math: true)` (the `FileNotFoundException`). `fixtures/math.json` records the
  real four graphs over the math page (probabilities bit-exact, flags, five readings) and four
  stand-in graphs that assert the loop's invariants; `MathReaderTests` adds 21 tests and
  `NumericsTests` pins the new `Sum64`/`Mean64` against numpy (`docs/200-math.md`). The harness has
  six new math cases (83 in all), all byte-identical.
- Step `245` done: the host layout — the argparse port and the Python-spelling helpers go back
  to `Indra.Ocr.Cli` (namespaces `Indra.Ocr.ArgumentParsing` / `Indra.Ocr.Cli.PythonInterop`,
  C# names: `ArgumentAction`/`ArgumentType`/`ArgumentArity`/`ParsedArguments`/
  `ArgumentParserExit`/`SourceArguments`, `FloatFormat`/`PythonJson`/`ArgumentNumber`/
  `PythonText`/`ConsoleOutput`) and `Indra.Ocr.Viewer` references `Indra.Ocr.Cli`, which is the
  reference's own shape (`viewer.py` imports from `cli.py`); `PyPath` is gone — a path is a
  `string` and Core's new `PathHelper` is the one place that knows Python's spelling of one.
  Supersedes `205`/`215` for the helpers; no behaviour change (177 tests, 83/83 + 20/20 + 11/11)
  (`docs/245-host-layout.md`).
- Step `247` done: `PythonInterop` folded into `Indra.Ocr.ArgumentParsing.ConsoleOutput` —
  the streams plus the `os.linesep` translation, Python's float `repr`/`format(x, '.Nf')` and
  `json.dumps` in one class, since all of it is the console's (`docs/247-console-output.md`).
  No behaviour change (177 tests, 83/83 + 20/20 + 11/11).
- Step `250` done: the reference's own `tests/` ported — 26 of its 27 cases into
  `ReferenceInputsTests` (9 inputs; the tenth, `test_version_flag`, is a command-line
  surface) and `ReferenceTextLayerTests` (17 text-layer), each carrying the reference's
  test name. It found two parity bugs: `HF_ENDPOINT` was ignored, and the model cache's
  daily stamp was touched on the offline path (the reference stamps only after a real Hub
  call). 203 tests, 83/83 + 20/20 + 11/11 (`docs/250-reference-tests.md`).
- Step `260` done: the end-to-end sweep over the reference's own material — `parity_sweep.py`
  (24 cases: seven assets x three formats, the seven-asset pool parallel and serial, `--out`)
  and `viewer_parity.py`'s new `assets` case (two example pages through the API, skipped when
  `resources/` is not downloaded). Byte-identical everywhere; no finding
  (`docs/260-parity-sweep.md`).
- Step `280` done: the deskew search's threads — the parallel angle scoring above; all five
  harnesses still byte-identical (203 tests, 83/83 + 24/24 + 20/20 + 12)
  (`docs/280-deskew-threads.md`).
- Step `290` done: the AOT properties on the five projects, the `IL3000` fix and the
  ImageSharp suppression; build clean with the analyzers on, 203 tests, and the four
  harnesses (83/24/20/12) green on the Debug build *and* on a trimmed self-contained publish
  of each host (`docs/290-aot.md`).
- Step `295` done: `<InvariantGlobalization>true</InvariantGlobalization>`, so the native
  binaries need no ICU. It made `string.Normalize` a no-op, which broke one test: the port's
  NFC moved into managed code, over tables generated from CPython's own `unicodedata`
  (`docs/295-invariant-globalization.md`). 206 tests, 83/24/20/12.
- Step `300` done: the viewer's Ctrl+C — the console lifetime is asked for explicitly (the slim
  host happens to register one already) and `ViewerHostTests` pins the shutdown: the signal's
  target, and the server closing with the engine disposed (`docs/300-viewer-ctrl-c.md`).
  207 tests, 83/24/20/12.
- Step `310` done: the model download's progress — Core reports (`ModelDownloadProgress`:
  file, path, url, downloaded, total) through `Download`/`Resolve`'s `downloadProgress`
  callback, and the hosts print it on stderr (`ConsoleOutput`, shared by the CLI and the
  viewer), a line per tenth with the file's name, its size, how much has arrived and the
  percentage (`docs/310-model-download-progress.md`). 216 tests, 83/24/20/12, and both hosts
  checked against a real first-run download.
- Step `320` done: the I/O boundary is asynchronous — `ModelDownload.DownloadAsync` and
  `ModelFolder.ResolveAsync` are the implementations, with the synchronous `Download`/`Resolve`
  as thin wrappers over them, and a cancelled token is no longer mistaken for "the Hub could not
  be reached" (`Offline` checks the token). The pipeline itself stays synchronous: ONNX
  Runtime's .NET binding has no `RunAsync` and `GrayImage` is a non-owning `ref struct`, so a
  `Task.Run` wrapper would be fake async and a copy per frame
  (`docs/320-async-io.md`). 218 tests, 83/24/20/12, and a real first run over both paths.
- Step `325` done: the README became the front page — `Indra.Ocr.Sharp`, the upstream project
  and its models, what this port is (parity, ONNX Runtime through NuGet, no OpenCV, the two
  decoder projects, AOT), the build and publish commands, the CLI and viewer examples with
  their option tables, `PdfConverter`/`ImageConverter` examples, and the licensing (code
  Apache-2.0, weights modified OpenRAIL-M, both as upstream). `NOTICE`'s product name became
  `Indra.Ocr.Sharp` (`docs/325-readme.md`).
- Step `330` done: `ProductInfo` (Core's two `const string`s) is gone — `Product` and `Version`
  are set in `src/Directory.Build.props` and each host reads them back from its own assembly
  (`Indra.Ocr.Cli.ArgumentParsing.ProductAttributes`; the name is the *product* `indra-ocr`, not the
  viewer's assembly name `indra-ocr-viewer`, because the reference prints `indra-ocr {__version__}`
  in both hosts), with `IncludeSourceRevisionInInformationalVersion` off so no `+<sha>` can reach
  the parity surface (`docs/330-product-version.md`). 218 tests, 83/24/20/12, both hosts' `--version`
  and the viewer's `/info` byte-identical.
- Step `335` done: the three class libraries multi-target `net10.0;netstandard2.0`, so the port can
  be packaged for NuGet and consumed from .NET Framework. The net10.0 asset is untouched — the
  compatibility layer is `src/Compat/NetStandard20.cs`, compiled into all three (PolySharp for the
  language-level polyfills, System.Memory/Unsafe for the types, and `#if`-dispatched helpers where
  every condition names the API's *real* minimum version). The **PDFiumCore** wrapper is gone:
  `Indra.Ocr.Pdf` binds PDFium itself (`Native/PdfiumBindings.cs`, the 27 functions the port calls,
  transcribed from fpdfview.h/fpdf_text.h/fpdf_edit.h with the C names kept) over the native
  `bblanchon.PDFium.*` 156.0.8076 packages, because the wrapper has no netstandard2.0 asset and
  .NET Framework cannot reference netstandard2.1 at all (`docs/335-multitarget-netstandard20.md`).
  218 tests, 83/24/20/12, and the throwaway `temp/ns20-test` (the same program on net462 and
  net10.0, over 10 cases including two error inputs) agrees line for line, down to the bit
  patterns; `temp/ns20-probe/unicode-equiv` checks the Unicode shim over all 1 114 112 code points
  (0 mismatches), and a cold-cache `--download` run on .NET Framework fetched the model set
  byte-identical to `resources/models` through the ns2.0 shims.
- 218 tests green, build clean (0 warnings); the CLI alignment harness 83/83, the end-to-end
  asset sweep 24/24, the viewer's command line 20/20 and its HTTP harness 12 cases plus
  `stderr`. The three libraries also build a `netstandard2.0` asset that runs on .NET Framework
  4.6.2 with the same answers (`docs/335-multitarget-netstandard20.md`).
