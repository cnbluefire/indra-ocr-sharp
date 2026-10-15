# indra-port-to-csharp

A C# port of the Python **indra-ocr** package (`resources/indra-src`), aiming for 1:1
behavioural parity. ONNX inference uses `Microsoft.ML.OnnxRuntime` (NuGet).

## Layout

| Path | Purpose |
|---|---|
| `src/Indra.Ocr.slnx` | The .NET solution. |
| `src/Directory.Build.props`, `src/Directory.Packages.props` | Shared build settings + central package versions. |
| `src/Indra.Ocr.Core` | Core pipeline (models, detection, recognition, reading order, layout, text layer). Consumes grayscale pixels only (`GrayImage`); no image-codec/PDF dependency. |
| `src/Indra.Ocr.Pdf` | PDF rasterisation + text layer via PDFium (the only PDFium dependency). |
| `src/Indra.Ocr.Cli` | `indra-ocr` console app. |
| `src/Indra.Ocr.Viewer` | `indra-ocr-viewer` local web viewer. |
| `src/Shared/` | Source files linked into several projects (e.g. the ImageSharp image decoder, shared by Cli + Viewer). |
| `src/Indra.Ocr.Tests` | Unit/parity tests (xUnit). |
| `docs/` | Step specifications. |
| `tools/scripts/` | Helper scripts; `tools/scripts/test/` for alignment tests. |
| `temp/` | Scratch / generated fixtures (gitignored). |
| `resources/` | Upstream Python source + models (read-only, gitignored). |

All .NET code lives under `src/`. `Pdf`, `Cli` and `Viewer` reference `Core`; `Core` depends
on none of them.

## Build

```powershell
dotnet build src/Indra.Ocr.slnx
dotnet test  src/Indra.Ocr.slnx
```

## Reference tooling (for alignment)

```powershell
tools/scripts/setup-pyenv.ps1          # once
tools/scripts/run-original.ps1 --help  # original indra-ocr
```

See `AGENTS.md` (requirements) and `PLAN.md` (execution plan).
