# 215 — the argparse engine and the shared options move into Core

**Status:** done.

## Goal

Move the argparse port out of the CLI and into `Indra.Ocr.Core`, and lift the option group the
two commands share into one place. The viewer is a *second host*: it has its own parser, but its
`--help`, `--version` and error texts are part of the surface that has to match the reference, so
it needs the same parser engine — and it needs the same four source options the command line has.

The CLI and the viewer are siblings (neither references the other), so anything they share has to
live in Core. Core's constraint is *"no image codec and no PDF dependency"*, not *"no helpers"* —
`Py*` and `ModelFolder` already moved there in step `205` for the same reason.

## Files

| File | Change |
|---|---|
| `src/Indra.Ocr.Core/Argparse/ArgumentParser.cs` | moved from `src/Indra.Ocr.Cli/Argparse/`, namespace `Indra.Ocr.Argparse` |
| `src/Indra.Ocr.Core/Argparse/Arguments.cs` | moved, same namespace |
| `src/Indra.Ocr.Core/Argparse/HelpFormatter.cs` | moved, same namespace |
| `src/Indra.Ocr.Core/Argparse/SourceArgs.cs` | **new** — the shared option group |
| `src/Indra.Ocr.Cli/CliParser.cs` | calls `SourceArgs.AddTo(ap)`; its private `AddSourceArgs` is gone |
| `src/Indra.Ocr.Cli/Cli.cs` | reads the group with `SourceArgs.Options(a)` instead of the inlined `source_options` |
| `src/Indra.Ocr.Cli/Argparse/` | deleted (the directory itself moved) |

The three engine files kept their contents; only the namespace changed, and the two call sites
plus the viewer's parser now `using Indra.Ocr.Argparse;`.

## Shape

```
ArgumentParser(prog, description)          the engine: AddArgument / ParseArgs / FormatHelp
Namespace                                  what ParseArgs hands back: Int / Long / Float / Str /
                                           Flag / PathOrNull
ArgparseExit                               stdout + stderr + status — what argparse's sys.exit
                                           carries (0 for --help/--version, 2 for a bad argument)

SourceArgs.AddTo(ap)                       --mode {auto,ocr,text}, --model-order,
                                           --no-image-ocr   (the reference's add_source_args)
SourceArgs.Options(a) -> (mode, pdfOrder, imageOcr)   (the reference's source_options)
```

`SourceArgs` is what the reference spells twice — `cli.add_source_args` and `cli.source_options`
are shared by `cli._main` and `viewer._main`. Before this step the CLI carried the first as a
private helper and the second inline in `Cli.cs`; both are now one place, and the viewer's parser
adds the group in the same position, which is what makes its `--help` line up.

## Decisions

- **The engine is Core's.** It has no dependency of its own (a string, a `Namespace`, a
  formatter) and both hosts need it; the alternative — a copy per host — would be two parsers to
  keep in step, and the help text is a parity target.
- **`Options` returns a tuple, not three lookups.** The reference's `source_options(a)` returns a
  dict the caller unpacks; a tuple keeps the same single call and the same reading order.
- **The namespace stays `Indra.Ocr.Argparse`** (not `Indra.Ocr.Cli.Argparse`), because it is no
  longer the CLI's.

## Verification

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings.
- `dotnet test src/Indra.Ocr.slnx` — 157/157 (the CLI's `ArgparseTests` still compare the help
  text, the error wording and the namespace against the reference's captured text).
- `tools/scripts/test/cli_parity.py` — **77/77** byte-identical after the move, so the command
  line's surface did not shift with the code.
- The viewer's `--help` and `--version`, printed by the moved engine, are byte-identical to the
  reference's (`docs/220-viewer-api.md`).
