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
- [ ] `040` OpenCV primitives: resize, getRotationMatrix2D, warpAffine, CCL(4).
- [ ] `050` Vocab + Unicode helpers.

### Phase 2 — ONNX pipeline
- [ ] `060` ORT session infra + meta/vocab load.
- [ ] `070` `det_post`.
- [ ] `080` `rec_preproc` + `line_mask`.
- [ ] `090` orientation + deskew.
- [ ] `095` adaptive detect.
- [ ] `100` recognize (CTC, CJK routing).
- [ ] `110` `order_post`.
- [ ] `120` `layout_post`.
- [ ] `130` assemble / gutter cuts / split.
- [ ] `135` image inputs (hosts decode files → `GrayImage`).

### Phase 3 — PDF + Document
- [ ] `140` `Indra.Ocr.Pdf` PDFium backend (grayscale render + text layer).
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

## Current state

- Environment verified: dotnet 10.0.400, uv 0.12.7, Python ref env at `tools/pyenv`.
- Original CLI `--help` captured for both `indra-ocr` and `indra-ocr-viewer`.
- ONNX signatures inspected; `det_long_side=1024`, `rec_height=32`, vocab 1677 (latin) /
  9967 (cjk). `order.onnx` has 4 outputs (incl. table heads); `table_heads` is off by default.
- Layout settled: four projects under `src/` (+ `src/Shared/` linked sources); `Core` is
  free of ImageSharp/PDFium and consumes grayscale `GrayImage`.
- Steps `000`–`030` done: numerics, gray decode/encode parity (17 tests green, build clean).
