# 230 — the viewer's page (`viewer.PAGE` / `viewer.LOGO`)

**Status:** done.

## Goal

Serve the page the browser loads: the reference's 22 kB of HTML/CSS/JS and the 8 kB base64
Datalab mark in its header, byte for byte. The API was aligned on its own in step `220`; this is
the last route that was still a placeholder.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Viewer/ViewerPageAssets.Generated.cs` | `viewer.PAGE` and `viewer.LOGO` as C# literals — **generated**, do not edit |
| `src/Indra.Ocr.Viewer/ViewerPage.cs` | `Render()`: the logo substituted into the page |
| `tools/scripts/test/gen_viewer_page.py` | writes both the assets and the fixture from the reference module |
| `src/Indra.Ocr.Tests/fixtures/viewer_page.html` | the body `GET /` gives, logo already in it (30 425 bytes) |
| `src/Indra.Ocr.Tests/ViewerPageTests.cs` | the Python-free check |

## Shape

```
ViewerPage.Render()  ->  PAGE.replace("__LOGO__", LOGO)
```

- **The page and the logo are taken from the reference module, not retyped.** 22 kB of HTML and
  8 kB of base64 are one typo away from a broken page, so `gen_viewer_page.py` imports
  `indra_ocr.viewer` and writes them into `ViewerPageAssets.Generated.cs`. The file is committed;
  re-run the script when the reference's page changes. This is the same arrangement as the other
  generators under `tools/scripts/test/`.
- **The page is a raw string literal** whose closing `"""` sits at column 0, so the compiler
  strips nothing and the content is verbatim. That needs the page to have no `"""` and no
  blank/whitespace-only/trailing-space line — it has none of those, and the generator is written
  against the module, so a future page that breaks the rule would fail the build rather than
  serve something wrong.
- **The line endings are LF, and deliberately so.** The reference serves its page exactly as the
  string is spelled in its own source file — but CPython *normalises the line endings inside
  string literals*, so a checkout's CRLF never reaches the answer. The generated asset is written
  with LF and `Render()` folds any CRLF a checkout put back into the file, so the answer is LF
  whatever the working copy looks like. (This is why the page needs no `PyText.Translate`: unlike
  the CLI's streams, nothing here goes through a text-mode translation.)
- The logo is emitted as concatenated C# string literals, the way the reference wraps its own.

## The page itself

| Piece | What it is |
|---|---|
| `GET /`, `GET /index.html` | `text/html; charset=utf-8`, the page above |
| the mark | `data:image/png;base64,…` in the header's `<img class="mark">` |
| `/info` | fills in `#ver` and unhides the Tesseract checkbox — always hidden here (see below) |
| `POST /upload` | the drop zone, `fetch` with the file's `ArrayBuffer` |
| `POST /page` | one page at a time, drawn as it finishes |
| the panes | Preview / Markdown / Lines / JSON (the tabs the response's fields drive) |

The page is pure static content: it makes the same requests the API already serves, so nothing in
it needed porting beyond the text.

## Out of scope

**The Tesseract comparison is not implemented** (user, 2026-10-16). The reference imports
`pytesseract` when `--tesseract` is given — a Python package driving the `tesseract` binary, and
neither is present here, so the reference itself dies at startup with `ModuleNotFoundError`. The
port keeps the flag (it is in the reference's `--help`), refuses it at startup with exit 1 (step
`210`) and therefore never answers `"tesseract": true` from `/info`, so the page's checkbox stays
hidden — exactly as it does for a reference that has no Tesseract to offer. No further work
planned.

## Verification

- `dotnet test src/Indra.Ocr.slnx --filter ViewerPageTests` — 2/2: the page equals the recorded
  body (line endings normalised for the comparison, the way the help fixture is), it starts and
  ends where the reference's does, the logo is in place, `__LOGO__` is gone and there is no CR.
- `tools/scripts/test/viewer_parity.py page` — the `page` case fetches `/` and `/index.html` from
  both servers and compares the bodies **byte for byte** (the two are the same 30 425 bytes); the
  full harness is 10/10.
- `dotnet test src/Indra.Ocr.slnx` — 159/159; `cli_parity.py` — 77/77 (untouched by this step).
