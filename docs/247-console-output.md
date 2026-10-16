# 247 — one console class

**Status:** done.

## Goal

Step `245` left the CLI holding four small classes for what the two hosts write: `FloatFormat`
(Python's `repr` / `format(x, '.Nf')`), `PythonJson` (`json.dumps`'s spelling), `PythonText`
(`\n` → `os.linesep`) and `ConsoleOutput` (the two UTF-8 streams). They are all the console's,
none of them is a thing a caller holds, and they all serve one parity target — so they are one
class, `Indra.Ocr.ArgumentParsing.ConsoleOutput`, and the `Indra.Ocr.Cli.PythonInterop` namespace
is gone.

## Shape

`src/Indra.Ocr.Cli/ArgumentParsing/ConsoleOutput.cs`, in four sections:

| Section | Members | Was |
|---|---|---|
| the two streams | `Write`, `WriteError` | `ConsoleOutput` |
| Python's text streams | `Translate` | `PythonText` |
| Python's float spelling | `Repr`, `Fixed` | `FloatFormat` |
| Python's `json.dumps` spelling | `Str`, `Num`, `Bool`, `Null`, `Array`, `Object` | `PythonJson` |

`Cli.cs`, `Viewer.cs` and `Engine.cs` lose their `using Indra.Ocr.Cli.PythonInterop;`, and the
call sites read `ConsoleOutput.Fixed(...)` / `ConsoleOutput.Translate(...)` /
`ConsoleOutput.Object(...)`. No member is renamed and no behaviour changes: the bodies move as
they are, and the remarks that explain each Python function move with them.

## Decisions

- **One class, not four.** They share a purpose (writing what the console shows) and a target
  (Python's spelling of it). `ArgumentParsing` is where the CLI's Python-facing surface already
  lives, so the namespace goes there rather than staying a namespace of its own.
- **The member names stay.** `Str`/`Num`/`Bool`/`Null`/`Array`/`Object`/`Repr`/`Fixed`/`Translate`
  are what the call sites read as, and the class doc says which Python function each one spells;
  prefixing them (`JsonStr`, `FloatRepr`) would rename every call site for no new information.

## Verification

- `dotnet build src\Indra.Ocr.slnx` — 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` — **177** tests green (unchanged: the console is not a test
  subject of its own; the harness below is what covers it).
- `tools/scripts/test/cli_parity.py` — **83/83**; `viewer_cli_parity.py` — **20/20**;
  `viewer_parity.py` — **11/11**. Both hosts' console bytes go through this class, so these are
  the cases that would notice a change: the `--format json` lines, the `ms` timings
  (`Fixed`), the CRLF translation and the viewer's `total_ms` line.
