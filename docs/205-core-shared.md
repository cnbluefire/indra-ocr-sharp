# 205 — shared helpers move into Core

**Status:** done. **Superseded by [245](245-host-layout.md)** for the helpers: they are the CLI's again (the viewer
references the CLI), under C# names. `ModelFolder` and the new `PathHelper` are the part
that stays in Core. (The four Python-spelling classes became one, `ConsoleOutput`, in step
`247`.)

## Goal

Both hosts need the same things: the spelling of Python's paths, numbers, floats and JSON,
and the model-folder policy (argument → `INDRA_OCR_MODEL_DIR` → the Hugging Face cache).
The viewer is the second host, so those helpers cannot live in the CLI any more. They move
into `Indra.Ocr.Core`, which both `Indra.Ocr.Cli` and `Indra.Ocr.Viewer` reference.

## What moved

| From (`Indra.Ocr.Cli`) | To (`Indra.Ocr.Core`) | Namespace |
|---|---|---|
| `Py/PyPath.cs` | `Py/PyPath.cs` | `Indra.Ocr.Py` |
| `Py/PyFloat.cs` | `Py/PyFloat.cs` | `Indra.Ocr.Py` |
| `Py/PyJson.cs` | `Py/PyJson.cs` | `Indra.Ocr.Py` |
| `Py/PyNumber.cs` | `Py/PyNumber.cs` | `Indra.Ocr.Py` |
| `Py/PyText.cs` (incl. `PyConsole`) | `Py/PyText.cs` | `Indra.Ocr.Py` |
| `Onnx/ModelFolder.cs` | `Onnx/ModelFolder.cs` | `Indra.Ocr.Onnx` |

`PyConsole` writes the console's bytes (stdout/stderr as UTF-8, `os.linesep`), which is what
both hosts need; the worker-process pool's own protocol is not in this set and stays in the
CLI.

## Why this supersedes the earlier decision

`docs/180-cli.md` recorded "the model-folder lookup lives in the CLI, because the CLI is the
only host that has one". That was true while the CLI was the only host. The reference has
the opposite shape: `viewer.py` imports `models.model_dir` and `cli.add_source_args` /
`cli.source_options` from the same modules the command line uses. Two hosts that must agree
on `--model-dir`, on the environment variable, on the cache path and on the download can only
agree by sharing the code.

Core's rule is that it carries **no image-codec and no PDF dependency** — not that it is
free of helpers. These files touch neither; they spell Python's values and find a folder.

## Files

- `src/Indra.Ocr.Core/Py/*.cs` — the five helper types, namespace `Indra.Ocr.Py`.
- `src/Indra.Ocr.Core/Onnx/ModelFolder.cs` — `ModelFolder.Resolve(PyPath?, bool math)`,
  public, namespace `Indra.Ocr.Onnx`.
- `src/Indra.Ocr.Cli/Cli.cs`, `Argparse/ArgumentParser.cs` — `using Indra.Ocr.Py;`.
- `src/Indra.Ocr.Tests/ArgparseTests.cs` — `using Indra.Ocr.Py;`.

## Verification

- `dotnet build src\Indra.Ocr.slnx` — 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` — 151 tests green (the CLI's parser tests, which compare
  the help text against the reference's captured bytes, are the ones that would notice).
- The CLI alignment harness — 77/77, byte-identical (the helpers are what spells the output).
