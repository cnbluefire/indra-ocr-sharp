# 010 — Solution, projects, build props, NuGet refs

Status: done

## Goal

Create the three-project C# solution that mirrors the Python package layout, with ONNX
Runtime referenced via NuGet, and helper scripts to run the original Python tools.

## Deliverables

- `Indra.Ocr.slnx` (the .NET 10 SDK emits `.slnx`) with three projects.
- `Directory.Build.props` — `net10.0`, nullable, implicit usings, latest LangVersion,
  deterministic builds.
- `Directory.Packages.props` — central package versions:
  - `Microsoft.ML.OnnxRuntime` **1.31.0** (matches the Python `onnxruntime` 1.31.0).
  - `PDFiumCore` **156.0.8076** (pulls in `bblanchon.PDFium`).
  - `SixLabors.ImageSharp` **2.1.13** (last Apache-2.0 release).
- `src/Indra.Ocr.Core` (classlib, assembly `Indra.Ocr.Core`).
- `src/Indra.Ocr.Cli` (console, **assembly name `indra-ocr`**).
- `src/Indra.Ocr.Viewer` (`Microsoft.NET.Sdk.Web`, **assembly name `indra-ocr-viewer`**).
- `tools/scripts/setup-pyenv.ps1` — (re)create the gitignored python reference env.
- `tools/scripts/run-original.ps1` — run `indra-ocr` from the local source tree.
- `tools/scripts/run-original-viewer.ps1` — run `indra-ocr-viewer` (blocking; background it).

## Decisions / notes

- **ImageSharp version.** v3/v4 use the Six Labors Split License, and v4 fails Release
  builds without a licence key. Pinned to **2.1.13** (Apache-2.0) to avoid a licensing
  blocker. Verified it still decodes/encodes the formats we need (PNG/JPEG/BMP/TIFF) when
  step 030 is implemented.
- Stub `Main`/`Program` only; real logic lands in later steps. The viewer stub starts a web
  host (blocking) — never run it unbackgrounded during automation.

## Verification

- `dotnet build Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `tools/scripts/run-original.ps1 --version` → `indra-ocr 0.3.2`.
- `dotnet run --project src/Indra.Ocr.Cli` → exits 1 with the placeholder message.
