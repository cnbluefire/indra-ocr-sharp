# AGENTS.md — requirements and working rules

This repository ports the Python package **indra-ocr** (`resources/indra-src`) to a C#
solution with **1:1 feature parity**. This file records the user's requirements and the
rules every agent must follow. Treat it as authoritative.

## Goal

Port `indra-ocr` (CLI + viewer + pipeline) to C#, keeping behaviour, options and output
aligned 1:1 with the Python version. ONNX inference must be referenced through a **NuGet
package** (`Microsoft.ML.OnnxRuntime`).

## Hard requirements

1. **Three projects**
   - `Indra.Ocr.Core` — all core functionality: ONNX Runtime reference, inference-engine
     lifecycle management, the whole OCR pipeline.
   - `Indra.Ocr.Cli` — console app; commands must match the `indra-ocr` command 1:1.
   - `Indra.Ocr.Viewer` — preview project; must match the `indra-ocr-viewer` command 1:1.
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
- **Scope**: OCR + PDF text layer first; `--math` last.
- **Image codecs**: pure-managed ImageSharp for decode/encode; grayscale conversion uses
  OpenCV's exact weights. (Licence: ImageSharp v3 Six Labors Split License — revisit if a
  strictly permissive codec is required.)

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
