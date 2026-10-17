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
   - `Indra.Ocr.Pdf` — the PDF decoder: page rasterisation + text layer over PDFium, which it
     binds itself (`Native/PdfiumBindings.cs`) on top of the `bblanchon.PDFium.{Linux,Win32,macOS}`
     native packages. This is the **only** project that touches PDFium, and it holds
     `PdfConverter`, the PDF half of `document.convert`. PDFium renders straight to grayscale
     (`FPDFBitmap_Gray`), so rendered pages come back as `GrayImage` with no decoding. It
     references `Indra.Ocr.Core`.
   - `Indra.Ocr.Cli` — console app; commands must match the `indra-ocr` command 1:1.
   - `Indra.Ocr.Viewer` — preview project; must match the `indra-ocr-viewer` command 1:1.
   `Cli` and `Viewer` reference `Core`, `Indra.Ocr` and `Pdf`; `Indra.Ocr` and `Indra.Ocr.Pdf`
   do **not** reference each other — both hand Core the same `GrayImage`, so neither needs the
   other — and `Core` references none of them. `Viewer` also references `Cli`, because the two
   hosts share the argument parser and the Python-spelling helpers
   (`Indra.Ocr.ArgumentParsing`; one class, `ConsoleOutput`, for the streams, `os.linesep`, the
   float spelling, `json.dumps` and the model download's progress lines) — the reference's own
   shape, its `viewer.py` importing them from `cli.py` — so they live in the CLI and the viewer
   is built on top of it (`docs/245-host-layout.md`, `docs/247-console-output.md`). The hosts
   choose a decoder with `OrtOcr.IsPdf` and own no codec themselves.
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
8. **AOT.** The three class libraries (`Indra.Ocr.Core`, `Indra.Ocr`, `Indra.Ocr.Pdf`) set
   `IsAotCompatible` (on their `net10.0` target — the only AOT-capable one) and the two
   executables (`Indra.Ocr.Cli`, `Indra.Ocr.Viewer`) set
   `PublishAot`, so the solution builds with the trim/AOT analyzers on and both hosts publish
   as native binaries. Warnings that come from a third-party assembly and cannot be fixed are
   suppressed with the reason written down next to them (`docs/290-aot.md`). The solution is
   also **globalization-invariant** (`InvariantGlobalization`), so the native binaries need no
   ICU; because that turns `string.Normalize` into a no-op, NFC is implemented in Core over
   tables generated from CPython's `unicodedata` (`docs/295-invariant-globalization.md`).
9. **Portability.** The three class libraries multi-target `net10.0;netstandard2.0`, so they can be
   packaged for NuGet and consumed from .NET Framework; the two executables and the test project
   stay `net10.0`. The net10.0 asset must remain exactly what it was — the compatibility layer is
   `#if`-dispatched per member (each `#if` naming the *real* minimum version of the API it guards),
   never a rewrite — and the ns2.0 asset must be the same port functionally, verified by running it
   on .NET Framework 4.6.2 (`docs/335-multitarget-netstandard20.md`).

## Parity decisions (confirmed with the user)

- **Imaging**: pure managed C#. Replicate the required OpenCV operations directly (no
  OpenCvSharp). Match OpenCV's algorithms (fixed-point resize/warp, 4-connectivity CCL).
- **PDF**: PDFium itself, bound by the port's own P/Invoke
  (`src/Indra.Ocr.Pdf/Native/PdfiumBindings.cs`) over the native
  `bblanchon.PDFium.{Linux,Win32,macOS}` 156.0.8076 packages. The managed wrapper **PDFiumCore**
  was dropped (user, 2026-10-17) because it has no netstandard2.0 asset — its lowest is
  netstandard2.1, which .NET Framework cannot reference at all — so it could never serve the ns2.0
  target, and one binding for both targets cannot drift. The members the port needs
  (`FPDFText_GetCharAngle`, `FPDFTextObj_GetTextRenderMode`, `FPDF_PageToDevice`, …) are
  transcribed from the headers the Win32 package ships, keeping the C names
  (`docs/335-multitarget-netstandard20.md`).
- **Parity level**: bit-exact for deterministic output (markdown / text / JSON-lines);
  tolerance only for timings and sub-1e-3 floats.

  The `netstandard2.0` asset is a **functional**, not bit-exact, target, and only there: .NET
  Framework has no fused multiply-add and no CRT `expf` behind `MathF.Exp`, and its
  `CharUnicodeInfo` tables are older than .NET 10's, so that target computes the same answers on
  ordinary inputs but not necessarily the same bits
  (`docs/335-multitarget-netstandard20.md`). The net10.0 asset stays the reference.

  **Known, principled deviation — `np.argsort` tie order.** numpy's default `quicksort`
  argsort dispatches to a **CPU-specific SIMD sorting network** (AVX2/AVX512 via
  `x86-simd-sort`). For keys that are *exactly equal* the relative order it returns is an
  implementation detail of that SIMD build, is **not** part of numpy's contract, and
  differs between machines/CPUs. Reproducing it is neither portable nor meaningful, so the
  port uses a **stable** argsort (ties keep index order). Keys in the pipeline are mostly
  distinct floats (identical results); divergence is only possible when keys tie exactly.
  Python `sorted(...)` calls are stable and are matched exactly.
- **Threads**: the deskew search's 32 angles are scored in parallel on the thread count the
  host already resolved — the reference's `cv2.setNumThreads` (`--cores` in one process, 1 in
  a pool worker, `--threads` in the viewer). Same values, same winner, bit for bit; the
  viewer's count is the one timing-only difference (`docs/280-deskew-threads.md`).
- **Async**: only the network boundary is asynchronous — `ModelDownload.DownloadAsync` and
  `ModelFolder.ResolveAsync` are the implementations, with the synchronous `Download`/`Resolve`
  as thin wrappers over them — and a cancelled token is not "the Hub could not be reached". The
  OCR pipeline stays synchronous: ONNX Runtime's .NET binding has no `RunAsync`, and `GrayImage`
  is a non-owning `ref struct` that cannot cross an `await` (`docs/320-async-io.md`).
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
- .NET Framework verification for the `netstandard2.0` asset: `temp/ns20-test` (dual-target
  `net462;net10.0`, the same program against both assets) and `temp/ns20-probe/unicode-equiv`
  (the Unicode shim against the API it stands in for). Both are throwaway and need only
  `Microsoft.NETFramework.ReferenceAssemblies.net462` plus the packages the libraries already
  use (`docs/335-multitarget-netstandard20.md`).
- NativeAOT needs a platform toolchain (Windows: the MSVC linker + Windows SDK from Visual
  Studio's *Desktop development with C++* workload; Linux: clang). It is **not** installed on
  this machine, so the AOT publish is verified where it exists; the analyzers and a trimmed
  self-contained publish are verified here (`docs/290-aot.md`).
- `uv` 0.12.7 available; Python reference env at `tools/pyenv` (gitignored).
- Local models at `resources/models` (det/rec/rec_cjk/order/orient/script/meta.json).
  `mask.onnx` and the math models are absent; handle their absence the way Python does.
- Reference tool invocation used for alignment:
  ```powershell
  $env:PYTHONPATH="<repo>/resources/indra-src"
  tools\pyenv\Scripts\python.exe -m indra_ocr <args>
  ```
