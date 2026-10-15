# 015 — Test scaffolding

Status: done

## Goal

Stand up the verification harness used by every later step: a C# test project, JSON
fixture plumbing, a Python reference-output comparator, and deterministic test inputs.

## Deliverables

- `src/Indra.Ocr.Tests` (xUnit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1) referencing
  `Indra.Ocr.Core`. `fixtures/**` is copied to the output dir.
- `src/Indra.Ocr.Tests/Fixtures.cs` — loads JSON fixtures (arrays of floats/ints/bytes,
  2D flattening) that record Python-reference inputs and expected outputs.
- `tools/scripts/test/refutil.py` — helpers for writing fixtures from Python.
- `tools/scripts/test/compare_outputs.py` — compares a reference artifact with a port
  artifact. Deterministic text must match exactly; JSON-lines are compared structurally
  with timing fields ignored (`*_ms`, `timings`) and a float tolerance (default `1e-3`).
  BOM-tolerant so PowerShell redirection works.
- `tools/scripts/gen_inputs.py` — writes deterministic inputs to `temp/inputs/`:
  `latin.png`, `latin_rot90.png`, `latin_tilt.png`, `two_columns.png`, `cjk.png`,
  `blank.png`, `multi.tiff` (3 frames), `digital.pdf` (born-digital text layer, 2 pages).
- `tools/scripts/py.ps1` — runs a python script with the reference interpreter and
  `PYTHONPATH=resources/indra-src`.

## Verification

- `dotnet test src/Indra.Ocr.Tests` → 1 passed.
- Reference pipeline runs on the fixtures:
  `run-original.ps1 temp/inputs/latin.png --model-dir resources/models` produces markdown
  with 5 lines / 3 blocks.
- Two JSON runs of the same input compare equal through `compare_outputs.py`.

## Notes

- The generated inputs are synthetic; the recognizer output is imperfect but that is fine
  here — parity, not accuracy, is what these fixtures test. The born-digital PDF exercises
  the text-layer path once step 160 lands.
