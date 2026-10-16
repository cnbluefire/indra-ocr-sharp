# 240 — the viewer's parity surface, completed

**Status:** done.

## Goal

Close the viewer's alignment: the command line compared **byte for byte against the reference**
(not only against captured fixtures), and the HTTP surface checked under the concurrency the
reference itself has. The API (step `220`) and the page (step `230`) were already covered by
`viewer_parity.py`; what was missing was the part of the viewer that runs and exits, and the
question the plan raised for `220`: *requests arrive on several threads while PDFium is not
thread-safe — is the engine's one lock enough?*

## Files

| File | Contents |
|---|---|
| `tools/scripts/test/viewer_cli_parity.py` | **new** — the command-line cases (20), on `parity.Harness` |
| `tools/scripts/test/viewer_parity.py` | the `concurrent` case, `Session.parallel`, `Session.record` |
| `src/Indra.Ocr.Viewer/Viewer.cs` | the `ModelError` stream fixed (below) |

## Two harnesses, because a server is not a command

`viewer_parity.py` starts both servers **once** (the models load once each) and drives them with
the same requests; `viewer_cli_parity.py` runs both command lines and compares what they printed.
A case cannot be both: the reference blocks in `serve_forever()`, so a command-line case may only
succeed as far as `--help`/`--version`, or fail before the server starts.

The 20 command cases: `--help`/`-h`/`--version`; the argparse errors (`--nope`, `-x`, the
ambiguous `--t`, `--`, `--port x`, `--threads x`, `--dpi x`, `--max-pages x`, `--mode nope`, a
missing value); `--port=9000` and the abbreviation `--por 9000`; a model folder that lacks the
files (`--model-dir temp`, and the same with `--math`, whose missing list grows by the five math
files); the shared source options; and `--tesseract`.

`sys.argv[0]` is set to `indra-ocr-viewer` for the reference, because argparse prints the program
name in the usage line and in every error prefix — `python -c` would print `-c`.

## What it found

**The viewer prints a `ModelError` to stdout, not stderr.** The reference's `viewer.main` is

```python
except ModelError as e:
    print(f"indra-ocr-viewer: {e}", flush=True)      # stdout
```

while the command line's `cli.main` is `print(f"indra-ocr: {e}", file=sys.stderr)`. The port had
copied the CLI's choice for the viewer, so `--model-dir temp` answered on the wrong stream. The
`nofolder` case is the regression test. (Measured, not guessed: the reference writes
`indra-ocr-viewer: the model folder temp lacks det.onnx, rec.onnx, meta.json` to stdout and
nothing to stderr, exit 1.)

`ViewerError` — the port's own refusal of `--tesseract` — stays on stderr, because the reference's
analogue is an uncaught `ModuleNotFoundError`, whose traceback Python prints there.

## The `concurrent` case

Four `POST /page` requests (two pairs of page 1 and 2) are fired at once, each on its own
connection, against both servers. Both are threaded — the reference is a `ThreadingHTTPServer`,
the port is Kestrel — and the *engine* is what serialises the work, so this is the shape that
checks the single lock is enough: all four answers come back whole and equal on both sides
(compared as a multiset, sorted by page number, since the completion order is not defined).

The console lines of that case are not compared: the reference's `print` is two writes (the text,
then the newline), so under concurrency its own lines can interleave, and there is nothing
deterministic to compare. Everything else about the case is.

## Known deviations

- **The `--tesseract` text.** The reference dies importing `pytesseract` — a Python traceback
  naming a package that is not installed here — and the port says
  `tesseract is not supported by this port` in one line. Both exit 1 with an empty stdout, which
  is what the case checks; the harness masks the text (and only that text), and the comparison is
  out of scope by decision (`docs/230-viewer-page.md`).
- The HTTP-level deviations of steps `220`/`230` (the JSON spelling, the malformed-body wording,
  the transport headers, the JPEG preview, the timings) are unchanged.

## Verification

- `tools/scripts/test/viewer_cli_parity.py` — **20/20**, byte for byte (exit code + stdout +
  stderr, with only the `--tesseract` text masked).
- `tools/scripts/test/viewer_parity.py` — **11/11**: the API cases of `220`, the `page` case of
  `230`, the new `concurrent` case and `stderr`.
- `dotnet test src/Indra.Ocr.slnx` — 155/155; `cli_parity.py` — 77/77.
- The ModelError fix was checked against the reference directly (above) rather than only through
  the harness.
