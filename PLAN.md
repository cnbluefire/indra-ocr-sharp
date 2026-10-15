# PLAN.md — execution plan

Living plan for porting **indra-ocr** (Python) to C#. Kept in sync with `docs/`.
Status legend: `[ ]` todo · `[~]` in progress · `[x]` done · `[!]` blocked.

Source of truth for requirements: `AGENTS.md`. Step specs: `docs/NNN-*.md`.

## Decisions (locked)

- Imaging: pure managed C# (replicate OpenCV ops). `Core` carries no image-codec or PDF
  dependency — its only image input is 8-bit grayscale `GrayImage`, a `ref struct` view it
  never owns (outputs are caller-provided buffers, decoded one image at a time). File decoding
  (ImageSharp) lives in the host projects `Cli`/`Viewer`; PDF rasterisation
  (`PDFiumCore` + `bblanchon.PDFium`) lives in `Indra.Ocr.Pdf` and emits grayscale directly.
  ONNX: `Microsoft.ML.OnnxRuntime`.
- Parity: bit-exact for deterministic output; tolerance only for timings and <1e-3 floats.
- numpy's float32 `exp` is not the CRT's `expf`, so the port reproduces numpy's own vector
  kernel (`Indra.Ocr.NpyExp`, with hardware FMA) instead of calling `MathF.Exp`; see
  `docs/070-det-post.md`. numpy's contiguous float32 *reductions* are likewise a CPU-specific
  SIMD kernel, so the port uses numpy's portable pairwise sum (`Numerics.Sum32`/`Mean32`) and
  documents where the last-ulp difference can show (the `estimate_skew` ranking); see
  `docs/090-orientation-deskew.md`.
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
- [ ] `120` `layout_post`.
- [ ] `130` assemble / gutter cuts / split.
- [ ] `135` image inputs (hosts decode files → `GrayImage`).

### Phase 3 — PDF + Document
- [ ] `140` `Indra.Ocr.Pdf` PDFium backend (grayscale render + text layer).
- [ ] `145` Unicode bidi class table (`unicodedata.bidirectional` parity, used by `document.py`).
- [ ] `150` `textlayer`.
- [ ] `160` `document` Converter.
- [ ] `170` `models` model_dir + download.

### Phase 4 — CLI
- [ ] `180` argparse-faithful CLI.
- [ ] `190` `convert_many` + output writing.
- [ ] `195` CLI alignment harness.

### Phase 5 — Math
- [ ] `200` MathReader + integration.

### Phase 6 — Viewer
- [ ] `210` Viewer Engine.
- [ ] `220` Minimal API + source-gen JSON.
- [ ] `230` PAGE/LOGO port.
- [ ] `240` HTTP parity tests.

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
- Layout settled: four projects under `src/` (+ `src/Shared/` linked sources); `Core` is
  free of ImageSharp/PDFium and consumes grayscale `GrayImage`.
- Steps `000`–`115` done: numerics, gray decode/encode parity, bit-exact OpenCV primitives,
  the recognizer vocabulary, the ORT model set, the LineNet pre/post-processing (`DetPost`,
  with numpy's float32 `exp` reproduced bit-exactly), the line crop rule + batching
  (`RecPreproc`), the CenterMask windowing (`LineMask`), the page uprighting
  (`Orientation`: orient.onnx turns, deskew search, rotations), line detection
  (`Detect`: one LineNet pass, the adaptive second pass and the scale/clip tail), line
  recognition (`Recognizer`: cropping, the hanzi routing, width batching, the CTC collapse and
  the token → page-x mapping), the reading-order building blocks (`OrderPost`: line features
  over numpy's float32 `log`, pairwise-order decoding, the top-down and XY-cut baselines, the
  average-linkage block clustering and the layout-head block decoder) and the reading-order
  driver (`ReadingOrder` over `order.onnx`, with the XY-cut fallback and the table-heads gate).
  81 tests green, build clean.
