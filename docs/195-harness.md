# 195 — the CLI alignment harness

**Status:** done.

## Goal

Make the byte-for-byte comparison against the Python reference the project's *official*
harness rather than a script that grew case by case: one place that runs both sides on the
same input, compares everything they produce, reports the first difference of a failure,
and can run the cases in parallel. The machinery is deliberately separate from the CLI's
cases, because the viewer's HTTP harness (`viewer_parity.py`, written in step `220`, extended
in `240`) needs the same report and diff with different commands and a different comparison.

## Files

| File | Contents |
|---|---|
| `tools/scripts/test/parity.py` | the machinery: `Case`, `Result`, `Report`, `Harness`, `diff`, `parse`, `select` |
| `tools/scripts/test/cli_parity.py` | the CLI cases (77) and the two commands |
| `tools/scripts/test/parity_sweep.py` | the CLI side over the reference's own material (step `260`); reuses `cli_parity.py`'s commands, environment and mask |
| `tools/scripts/test/parity_selftest.py` | the machinery's own check: a mismatch must fail |
| `tools/scripts/parity.ps1` | the short way in (the local interpreter, `PYTHONPATH` set) |
| `docs/195-harness.md` | this file |

## Shape

```
Case(name, argv, outputs=False, env={}, skip_if=None)
                                     one input on both sides; @OUT@ is each side's own dir

Harness(reference, port, env, cwd, work, mask=None, jobs=1, timeout=300)
  run(cases) -> exit code
    per case: empty its output dirs, run the reference, run the port, compare, report

Report(stream): one line per case, the diff under a failure, the summary
```

`env` is extra environment for **both** sides of that one case and `skip_if` returns why the
case cannot run on this machine (a model folder that was not downloaded) or `None`; a skipped
case is reported as `SKIP name (reason)` and counts as neither a pass nor a failure. Step `200`
added both for the math cases, whose reference needs `PYTHONUTF8=1` (its `math_meta.json` is
read in the locale encoding) — per case, so the other cases keep the reference's own locale
behaviour.

A harness supplies only:

- `reference` / `port` — callables turning a case's argv into a command line;
- `env` and `cwd` — what both sides run in;
- `mask` — a `bytes -> bytes` function for what is allowed to differ;
- `work` — where a case's `--out` directories live (`temp/cli_parity/<case>/ref|port`).

A side that outruns `timeout` is killed and reported as having produced nothing, so a hung
case (a deadlocked worker pool, say) fails the run instead of hanging it.

## Usage

```powershell
tools\scripts\parity.ps1                        # all 77 cases
tools\scripts\parity.ps1 --list                 # the cases and their argv
tools\scripts\parity.ps1 pool-pdf pool-mixed    # by name
tools\scripts\parity.ps1 "pool-*"               # by glob
tools\scripts\parity.ps1 --serial               # one case at a time
tools\scripts\parity.ps1 --jobs 2               # two at a time
tools\scripts\parity.ps1 --help
```

The exit code is 0 only when every selected case matched; a name that matches nothing is an
error, so a typo cannot turn into a green run with no cases.

## Self-check

`tools/scripts/test/parity_selftest.py` runs the machinery against synthetic sides — both
are the local interpreter, told to emit chosen bytes and exit codes — and asserts the
verdicts: identical sides pass; a differing exit code, stdout, stderr, a differing file or a
file present on one side only all fail; a hung side fails; a case the harness itself cannot
handle fails just that case. It exists so a bug in the harness cannot quietly turn every
real case green, which is exactly what one of its first runs caught (both sides erroring
identically looked like a pass until the check also demanded empty stderr).

## What is compared

- The exit code, stdout, stderr and — for a case with `outputs=True` — every file the
  command wrote under its own `--out` directory.
- **Byte for byte**, including the CRLF the reference's text-mode streams carry on Windows.
- **Masked**: the wall-clock timings only — the `timings` object of a JSON line and an
  `indra-ocr --timings` stderr line. They are the one thing the two sides are allowed to
  differ on, together with the sub-1e-3 floats already covered by the parity decisions.

Both sides see the same environment otherwise:

- `PYTHONIOENCODING=utf-8`, because the port always writes UTF-8 while the reference's
  *redirected* streams would take the locale encoding (GBK on this machine).
- `COLUMNS=80`, so the help's wrapping is fixed on both sides.
- The reference runs the way the installed console script does —
  `python -c "import sys; from indra_ocr.cli import main; sys.exit(main())"` — and not
  `python -m indra_ocr`, whose `__main__.py` drops `main()`'s return value and always
  exits 0.

## Inputs

- `temp/inputs` (the images, the TIFF, a born-digital PDF) and `temp/pdf_samples` (the
  PDFium samples) come from `tools/scripts/gen_inputs.py` and
  `tools/scripts/test/pdf_samples.py`; the harness runs them when a marker file is missing,
  so a fresh checkout needs no preparation beyond `temp/`.
- `temp/cli_inputs` is built by the harness itself from fixed bytes: a six-page PDF (fpdf2,
  a pinned creation date) so `convert_many` takes the pool path, an empty file, a truncated
  PNG, a file that is not a PDF, and a directory.
- Every case writes only under its own `temp/cli_parity/<case>/` directory, and the harness
  empties those first, so a stale file from an earlier run cannot make a case look equal.

## Parallelism

Cases are independent, so `Harness` runs them on a thread pool (`--jobs N`, default one per
core capped at 8). A case still runs its two sides one after the other, so a running case is
one process — plus the worker processes a pool case starts by itself (up to `usable_cores()`
of them, six for `pool-pdf`). Memory, not CPU, is the limit: every process loads the model
set, and a pool case's workers all load it at once. On this machine (8 cores) the heaviest
mix measured is the eleven `pool-*` cases at `--jobs 8`: **3.2 GB peak working set across 51
processes**, comfortably inside 8 GB. `--serial` is there for a smaller machine.

## Verification

- `tools/scripts/parity.ps1` — all **77 cases** match: 18 parser/error cases, 54 real
  conversions (images, TIFF, the PDF samples, the text-layer modes and the eleven `pool-*`
  cases) and 5 `--out` cases — one of them `out-pool`, so twelve cases exercise the worker
  pool in all. Timings masked, everything else byte-identical.
- `--serial` (234 s), `--jobs 4` (165 s) and the default `--jobs 8` (147 s) all give the same
  77/77: a case's own output directory is what makes running them together safe. The pool
  cases already use the cores internally, so the wall clock does not fall much further.
- `tools/scripts/test/parity_selftest.py` — 9/9: identical sides pass; a differing exit code,
  stdout, stderr, a differing file, a file on one side only, a hung side and an output
  directory the harness cannot read all fail (only the last fails just its own case).
- `--list`, exact names, globs, `--help` and an unknown name were all checked by hand (the
  unknown name exits non-zero rather than reporting a green run over zero cases).

## Notes

- The harness is Python on purpose: it drives both the reference (which *is* Python) and
  the port, and its comparison is a byte comparison, not a port of anything. No production
  code depends on it.
- `compare_outputs.py` (the earlier structural JSON comparison with float tolerances) is
  still there for one-off checks; the harness supersedes it for the command line, because it
  compares the raw bytes instead of parsing them.
