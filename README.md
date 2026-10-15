# indra-port-to-csharp

A C# port of the Python **indra-ocr** package (`resources/indra-src`), aiming for 1:1
behavioural parity. ONNX inference uses `Microsoft.ML.OnnxRuntime` (NuGet).

## Layout

| Path | Purpose |
|---|---|
| `src/Indra.Ocr.Core` | Core pipeline (models, detection, recognition, reading order, layout, text layer, PDF). |
| `src/Indra.Ocr.Cli` | `indra-ocr` console app. |
| `src/Indra.Ocr.Viewer` | `indra-ocr-viewer` local web viewer. |
| `tests/Indra.Ocr.Tests` | Unit/parity tests. |
| `docs/` | Step specifications. |
| `tools/scripts/` | Helper scripts; `tools/scripts/test/` for alignment tests. |
| `temp/` | Scratch / generated fixtures (gitignored). |
| `resources/` | Upstream Python source + models (read-only, gitignored). |

## Build

```powershell
dotnet build Indra.Ocr.slnx
```

## Reference tooling (for alignment)

```powershell
tools/scripts/setup-pyenv.ps1          # once
tools/scripts/run-original.ps1 --help  # original indra-ocr
```

See `AGENTS.md` (requirements) and `PLAN.md` (execution plan).
