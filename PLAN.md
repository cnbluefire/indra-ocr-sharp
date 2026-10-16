# PLAN.md — execution plan

Living plan for porting **indra-ocr** (Python) to C#. Kept in sync with `docs/`.
Status legend: `[ ]` todo · `[~]` in progress · `[x]` done · `[!]` blocked.

Source of truth for requirements: `AGENTS.md`. Step specs: `docs/NNN-*.md`.

## Decisions (locked)

- Imaging: pure managed C# (replicate OpenCV ops). `Core` carries no image-codec or PDF
  dependency — its only image input is 8-bit grayscale `GrayImage`, a `ref struct` view it
  never owns (outputs are caller-provided buffers). The encoded-image decoder (ImageSharp) is
  the `Indra.Ocr` project (`Images/ImageFiles.cs`, one owning `GrayImageData` per frame) and
  PDF rasterisation (`PDFiumCore` + `bblanchon.PDFium`) is `Indra.Ocr.Pdf`, which emits
  grayscale directly. The two are siblings: neither references the other, and the host picks one
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
- Math (`--math`) ported last.
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
- [ ] `200` MathReader + integration.

### Phase 6 — Viewer
- [x] `205` Shared helpers move into Core — `Py*` (namespace `Indra.Ocr.Py`) and `ModelFolder`
  (namespace `Indra.Ocr.Onnx`) leave the CLI, because the viewer is a second host that must
  agree on `--model-dir`, the environment variable, the cache and the download; this supersedes
  the earlier "the model folder lives in the CLI" note (`docs/205-core-shared.md`).
- [x] `210` Viewer Engine — `viewer.Engine`: the uploads it keeps (five, oldest first, temp
  files and all), one warmed-up recognizer per `(math, furniture)`, and `page` under a single
  lock (PDFium is not thread-safe) — the preview (render at the converter's scale, turn upright,
  shrink to 1600 px, JPEG q82), the boxes scaled onto it, the Markdown, and the CLI-shaped
  `json`. Twelve recorded pages, two error texts and the five-upload limit match the reference
  (`docs/210-viewer-engine.md`).
- [ ] `220` Minimal API + source-gen JSON.
- [ ] `230` PAGE/LOGO port.
- [ ] `240` HTTP parity tests.
  Note for `220`: requests arrive on several threads while PDFium is not thread-safe (step `190`
  keeps one process per core for that reason). The engine already serialises every page under one
  lock; the API must not add a second path around it.

### Phase 7 — Hardening
- [ ] `250` Port python unit tests.
- [ ] `260` End-to-end parity sweep + docs.

### Phase 8 — Last
- [x] `270` ONNX backend benchmark: `Microsoft.ML.OnnxRuntime` vs `Lokad.Onnx` on the Indra
  models, as a BenchmarkDotNet project under `temp/` (see `docs/270-onnx-benchmark.md`).
  Result: Lokad.Onnx 0.3.0 cannot execute `det.onnx` (`Softplus`) or `rec.onnx` (`ArgMax`) at
  all, even after rewriting `HardSwish`/`Xor`/`Mod`/`BatchNormalization`/`DepthToSpace`; where
  it does run it is 1.6–2.8× slower than ORT on the same graph and allocates ~63 MB per
  `orient` call. `Microsoft.ML.OnnxRuntime` stays the reference backend.

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
  "the model folder lives in the CLI" decision is superseded (`docs/205-core-shared.md`).
- Step `210` done: `Indra.Ocr.Viewer`'s engine — `Upload` (the `%PDF-` prefix, the page count,
  `uuid4().hex[:12]` ids, five uploads kept, the reference's `not a PDF or a readable image`
  text) and `Page` (one warmed-up recognizer per `(math, furniture)`, everything under one lock
  for PDFium's sake, the preview rendered at the converter's scale → turned upright → shrunk to
  1600 px → JPEG q82, the boxes scaled by the same factor, the CLI-shaped `json` and the console
  line). `fixtures/viewer.json` records twelve pages, three error texts and the page counts from
  the reference's own `Engine`, and `ViewerEngineTests` compares the response field by field —
  the JPEG bytes and the timing values excepted, and the five-upload limit and the error texts
  checked too (`docs/210-viewer-engine.md`).
- 151 tests green, build clean (0 warnings); the CLI alignment harness still 77/77.
