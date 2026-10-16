# AGENTS.md — requirements and working rules

This repository ports the Python package **indra-ocr** (`resources/indra-src`) to a C#
solution with **1:1 feature parity**. This file records the user's requirements and the
rules every agent must follow. Treat it as authoritative.

## Goal

Port `indra-ocr` (CLI + viewer + pipeline) to C#, keeping behaviour, options and output
aligned 1:1 with the Python version. ONNX inference must be referenced through a **NuGet
package** (`Microsoft.ML.OnnxRuntime`).

## Hard requirements

1. **Five projects**, all under `src/`:
   - `Indra.Ocr.Core` — all core functionality: ONNX Runtime reference, inference-engine
     lifecycle management, the whole OCR pipeline, and every algorithm that works on an OCR
     *result* (the text layer's runs and the verdict on them, the reading order, the layout,
     the assembled page). It has **no** image-codec or PDF dependency: the only image input
     it accepts is `Indra.Ocr.Imaging.GrayImage`, a non-owning `ref struct` view over 8-bit
     single-channel pixels (`Width` / `Height` / `Stride` / `Data`), so callers can pass
     padded buffers with no copy. It owns and allocates nothing: outputs are caller-provided
     buffers.
   - `Indra.Ocr` — the encoded-image decoder: image *files* (ImageSharp) to grayscale, one
     `GrayImageData` per frame (`ImageFiles.ReadImages` / `ReadImage`), plus `ImageConverter`,
     the image half of `document.convert`. It references `Indra.Ocr.Core`.
   - `Indra.Ocr.Pdf` — the PDF decoder: page rasterisation + text layer via `PDFiumCore`. This
     is the **only** project that references the PDFium package, and it holds `PdfConverter`,
     the PDF half of `document.convert`. PDFium renders straight to grayscale
     (`FPDFBitmap_Gray`), so rendered pages come back as `GrayImage` with no decoding. It
     references `Indra.Ocr.Core`.
   - `Indra.Ocr.Cli` — console app; commands must match the `indra-ocr` command 1:1.
   - `Indra.Ocr.Viewer` — preview project; must match the `indra-ocr-viewer` command 1:1.
   `Cli` and `Viewer` reference `Core`, `Indra.Ocr` and `Pdf`; `Indra.Ocr` and `Indra.Ocr.Pdf`
   do **not** reference each other — both hand Core the same `GrayImage`, so neither needs the
   other — and `Core` references none of them. The hosts choose a decoder with `OrtOcr.IsPdf`
   and own no codec themselves.
2. **`resources/` is read-only.** Never modify anything under `resources/`. It also never
   enters git (it is externally downloaded material).
3. **ONNX runtime via NuGet.** `Microsoft.ML.OnnxRuntime`.
4. **Reference commands**
   - `indra-ocr` runs with `uv run indra-ocr`.
   - `indra-ocr-viewer` runs with `uv run indra-ocr-viewer`. **This one blocks** (it starts an
     HTTP server and then waits). When testing it, never block the agent: run it in the
     background / non-blocking.
5. **Method**
   - Analyse the Python source and run the original tool's `--help` to collect information
     before implementing.
   - Record implementation steps in `docs/` using coarse, insert-friendly numbers:
     `010-xxx`, `020-xxx`, `030-xxx`, later slicing in `015-xxx`, `025-xxx`, ...
   - Keep `PLAN.md` (the execution plan) and the `docs/` step files in sync with the current
     state. Update them at every step.
   - **Two phases**:
     1. Implement `Indra.Ocr.Core` + `Indra.Ocr.Cli`; test by aligning against the
        `indra-ocr` command (test scripts may be Python, since this is a console app).
     2. Implement `indra-ocr-viewer` with ASP.NET Core (minimal API + source-generated
        `System.Text.Json`, so it can be AOT-converted later). Port the API first (test with
        Python `http.client`/`curl` against JSON), and only after full alignment port the
        HTML across.
6. **Tooling / layout**
   - Tools installed for the work go under `tools/`.
   - Test scripts go under `tools/scripts/test/`; other scripts under `tools/scripts/`.
   - Generated / temporary files go under `temp/`.
   - Binaries and `resources/` contents must never be committed to git.
7. **Git**
   - Commit after every step, so work can be traced back later.

## Parity decisions (confirmed with the user)

- **Imaging**: pure managed C#. Replicate the required OpenCV operations directly (no
  OpenCvSharp). Match OpenCV's algorithms (fixed-point resize/warp, 4-connectivity CCL).
- **PDF**: `PDFiumCore` NuGet package (which pulls in `bblanchon.PDFium`). If the wrapper
  does not expose a needed text-layer API (`FPDFText_GetCharAngle`,
  `FPDFTextObj_GetTextRenderMode`, `FPDF_PageToDevice`), P/Invoke the bundled `pdfium.dll`
  directly.
- **Parity level**: bit-exact for deterministic output (markdown / text / JSON-lines);
  tolerance only for timings and sub-1e-3 floats.

  **Known, principled deviation — `np.argsort` tie order.** numpy's default `quicksort`
  argsort dispatches to a **CPU-specific SIMD sorting network** (AVX2/AVX512 via
  `x86-simd-sort`). For keys that are *exactly equal* the relative order it returns is an
  implementation detail of that SIMD build, is **not** part of numpy's contract, and
  differs between machines/CPUs. Reproducing it is neither portable nor meaningful, so the
  port uses a **stable** argsort (ties keep index order). Keys in the pipeline are mostly
  distinct floats (identical results); divergence is only possible when keys tie exactly.
  Python `sorted(...)` calls are stable and are matched exactly.
- **Scope**: OCR + PDF text layer first; `--math` last.
- **Image codecs**: the `Indra.Ocr` project decodes image files with pure-managed
  **ImageSharp 2.1.13** (the last Apache-2.0 release; 3.x/4.x are under the Six Labors Split
  License, and 4.x refuses Release builds without a key). `Core` never touches a file it
  cannot itself describe as pixels — the decoders hand `Core` a `GrayImage` (`Indra.Ocr`
  through `GrayImageData`, which owns the frame; `Indra.Ocr.Pdf` straight from the render).
  Reproducing OpenCV 5.0.0's grayscale conversion needs **three different** fixed-point
  formulas (one per decoder), documented in `docs/030-image-codecs.md`.

## Environment

- .NET SDK 10.0.400 (target `net10.0`).
- `uv` 0.12.7 available; Python reference env at `tools/pyenv` (gitignored).
- Local models at `resources/models` (det/rec/rec_cjk/order/orient/script/meta.json).
  `mask.onnx` and the math models are absent; handle their absence the way Python does.
- Reference tool invocation used for alignment:
  ```powershell
  $env:PYTHONPATH="<repo>/resources/indra-src"
  tools\pyenv\Scripts\python.exe -m indra_ocr <args>
  ```
