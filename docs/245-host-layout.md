# 245 — host layout: the parser and the helpers go back to the CLI

**Status:** done.

## Goal

The viewer is a second host that must agree with the command line on `--help`, `--version`, the
error wording, the exit codes and the Python spelling of paths, numbers, floats, newlines and
JSON. Steps `205` and `215` put that shared code into `Indra.Ocr.Core` because the two hosts were
siblings and Core was the only place both could reach.

This step reverses that: the shared code lives in `Indra.Ocr.Cli`, and `Indra.Ocr.Viewer`
references `Indra.Ocr.Cli`. That is the reference's own shape — `indra_ocr.viewer` imports
`add_source_args`/`source_options` from `indra_ocr.cli`, and `cli.py`/`viewer.py` both import from
the same modules — and it keeps Core what it is meant to be: the pipeline and the algorithms.

The C# name rules for the move:

- the `Arg` abbreviation is spelled out (`ArgAction` → `ArgumentAction`, `ArgType` →
  `ArgumentType`, `SourceArgs` → `SourceArguments`), the namespace is `Indra.Ocr.ArgumentParsing`;
- the `Py` prefix is gone: `PyFloat` → `FloatFormat`, `PyJson` → `PythonJson`, `PyNumber` →
  `ArgumentNumber`, `PyText` → `PythonText`, `PyConsole` → `ConsoleOutput` (step `247`
  merged those four classes into that one);
- argparse's `Namespace` → `ParsedArguments` (a type called `Namespace` in C# reads badly),
  `Nargs` → `ArgumentArity` (the enum) with `Argument.Arity` (the property), `ArgparseExit` →
  `ArgumentParserExit`;
- `PyPath` **does not exist**: a path is a plain `string`, and `Indra.Ocr.PathHelper` (Core) is
  the one place that knows Python's spelling of one.

## What moved

| From | To | Namespace |
|---|---|---|
| `Core/Argparse/ArgumentParser.cs` | `Cli/ArgumentParsing/ArgumentParser.cs` | `Indra.Ocr.ArgumentParsing` |
| `Core/Argparse/Arguments.cs` | `Cli/ArgumentParsing/Arguments.cs` | `Indra.Ocr.ArgumentParsing` |
| `Core/Argparse/HelpFormatter.cs` | `Cli/ArgumentParsing/HelpFormatter.cs` | `Indra.Ocr.ArgumentParsing` |
| `Core/Argparse/SourceArgs.cs` | `Cli/ArgumentParsing/SourceArguments.cs` | `Indra.Ocr.ArgumentParsing` |
| `Core/Py/PyNumber.cs` | `Cli/ArgumentParsing/ArgumentNumber.cs` | `Indra.Ocr.ArgumentParsing` |
| `Core/Py/PyFloat.cs` | `Cli/ArgumentParsing/ConsoleOutput.cs` (`Repr` / `Fixed`) | `Indra.Ocr.ArgumentParsing` |
| `Core/Py/PyJson.cs` | `Cli/ArgumentParsing/ConsoleOutput.cs` (`Str` … `Object`) | `Indra.Ocr.ArgumentParsing` |
| `Core/Py/PyText.cs` (incl. `PyConsole`) | `Cli/ArgumentParsing/ConsoleOutput.cs` (`Translate`, `Write`) | `Indra.Ocr.ArgumentParsing` |
| `Core/Py/PyPath.cs` | deleted — `Core/PathHelper.cs` (new) | `Indra.Ocr` |

> Step `247` merged the four `PythonInterop` classes into one class in `ArgumentParsing`
> (`ConsoleOutput.cs`); the table records where step `245` first put them.

`ArgumentNumber` sits with the parser because it *is* argparse's `type=int`/`type=float`;
`PathHelper` sits in Core because `ModelFolder` (`models.model_dir`) needs `expanduser()` and the
normalisation for `INDRA_OCR_MODEL_DIR`, and Core cannot reference the CLI. Since step `370` it also
carries the *flavour* — `pathlib` is the host's on Windows and on POSIX, so `PathHelper` is too
(`docs/370-path-flavour.md`).

## Shape

```
Indra.Ocr.Cli
  ArgumentParsing/   ArgumentParser, Argument (Arity/Action/Type), ParsedArguments,
                     ArgumentError, ArgumentParserExit, ArgumentNumber, HelpFormatter,
                     SourceArguments          -- the argparse port
                     ConsoleOutput            -- the two streams, \n -> os.linesep, float
                                                 repr / .Nf, json.dumps (one class
                                                 since step 247)
  Cli.cs, CliParser.cs, Worker/          -- the command line

Indra.Ocr.Core
  PathHelper         str(Path(s)), .stem, Path(...) / name, expanduser()
  Onnx/ModelFolder   models.model_dir (argument -> INDRA_OCR_MODEL_DIR -> the Hub cache)
```

`Indra.Ocr.Viewer` now has `<ProjectReference Include="..\Indra.Ocr.Cli\Indra.Ocr.Cli.csproj" />`.
An `OutputType=Exe` project is a normal assembly, so this is a project reference like any other;
each host keeps its own `Program`/entry point and its own assembly name (`indra-ocr.dll`,
`indra-ocr-viewer.dll`).

## Decisions

- **The viewer references the CLI.** The alternative — a copy per host, or a sixth project — costs
  either two parsers to keep in step (the help text is a byte-for-byte parity target) or a project
  the five-project layout does not have. The reference itself has `viewer.py` import from
  `cli.py`, so the dependency is the ported one, not a new one.
- **A path is a `string`.** `PyPath` was a `readonly struct` wrapping one string, with its own
  equality, `Name`, `Stem`, `Join`, `Or` and `ExpandUser`. Only two operations were ever used
  outside it (`--out`'s `.stem` and `Path / name`), so the type is gone and `PathHelper` keeps
  the four functions. `ParsedArguments.StrList` replaces `PathList`, `Str` replaces `PathOrNull`.
- **`ModelFolder.Resolve` takes `string?`**, not a path type: the `--model-dir` value comes from
  argparse already normalised, the environment variable is normalised here, and both go through
  `PathHelper.ExpandUser`.
- **The names follow C#.** `Arg`/`Py` prefixes and `Namespace` are what the ported code read
  like; the port reads like C# instead, and the mapping is recorded here so a diff against the
  reference stays readable.

## Verification

- `dotnet build src\Indra.Ocr.slnx` — 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` — **177** tests green (unchanged: `ArgparseTests`,
  `ViewerArgparseTests` and `ProductSmokeTests` are the ones that read the moved types).
- `tools/scripts/test/cli_parity.py` — **83/83** byte-identical.
- `tools/scripts/test/viewer_cli_parity.py` — **20/20**; `viewer_parity.py` — **11/11** (the
  viewer now runs against the CLI's assembly, so these are the cases that would notice a
  mismatch).
- The end-to-end `--math` runs (`temp/inputs/math.png` with `temp/math-models`,
  `temp/inputs/math.pdf`) are byte-identical to the reference with `PYTHONUTF8=1`, exit 0,
  empty stderr.
