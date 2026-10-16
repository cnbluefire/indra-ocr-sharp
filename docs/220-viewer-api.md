# 220 — the viewer's HTTP layer (minimal API + source-generated JSON)

**Status:** done.

## Goal

Serve what the browser asks for, with the reference's answers: the five routes
`indra-ocr-viewer`'s handler knows, over ASP.NET Core's minimal API, with the JSON written by a
source-generated `System.Text.Json` context (so the project can be AOT-converted later). The
page itself (`viewer.PAGE`) is step `230`; this step is the API, aligned on its own.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Viewer/ViewerParser.cs` | the reference's `_main` parser (`viewer.py`'s docstring as the description) and `ViewerArgs` |
| `src/Indra.Ocr.Viewer/Viewer.cs` | the host: the parser, the engine, the routes, the banner, `main`'s exit codes |
| `src/Indra.Ocr.Viewer/ViewerJson.cs` | the source-generated context (`camelCase` + the `[JsonPropertyName]`s that keep snake_case) |
| `src/Indra.Ocr.Viewer/ViewerPage.cs` | `PAGE`/`LOGO` — a placeholder in this step, the real page in `230` |
| `src/Indra.Ocr.Viewer/Program.cs` | `return Viewer.Run(args);` |
| `src/Indra.Ocr.Viewer/Requests.cs` | `ViewerDefaults`/`PageOptions`/`PageRequest` (step `210`), now serialised |
| `src/Indra.Ocr.Viewer/Responses.cs` | `UploadResponse`/`PageResponse` (step `210`) + `InfoResponse`, `ErrorResponse` |
| `src/Indra.Ocr.Tests/ViewerArgparseTests.cs` | the command line against the reference's captured text |
| `tools/scripts/test/gen_viewer_help.py` | captures `fixtures/viewer_help.txt`, `viewer_version.txt` |
| `tools/scripts/test/viewer_parity.py` | the HTTP harness (the machinery step `240` will extend) |

## Routes

| Route | Answer |
|---|---|
| `GET /`, `GET /index.html` | `viewer.PAGE.replace("__LOGO__", LOGO)`, `text/html; charset=utf-8` |
| `GET /info` | `{"tesseract": bool, "version": "0.3.2", "defaults": {…}}` |
| `POST /upload` | the raw bytes of a PDF or an image → `{"id": …, "pages": …}` |
| `POST /page` | `{"id", "n", "opts"}` → the page the browser draws |
| anything else | `404` `not found`, `text/plain` |

Anything that throws inside `/upload` or `/page` becomes `error: {message}` on the server's
console and `400` with `{"error": …}` — the reference's handler catches `Exception`, not just its
own errors, so the port catches the same. `"tesseract"` reports whether a Tesseract backend could
be loaded; the port refuses `--tesseract` at startup (step `210`), so a running server answers
`false`.

## The JSON

The answers are records whose properties are declared in the reference's insertion order, and the
generated context writes them with the camelCase policy plus the explicit `[JsonPropertyName]`s
that keep the reference's `snake_case` keys (`pdf_order`, `image_ocr`, `max_pages`,
`model_order`). `PageResponse.Tesseract` is the one conditional key
(`[JsonIgnore(Condition = JsonWhenWritingNull)]`); `json.markdown` must be written as `null`
when the order model produced none, so it has no such attribute.

**The bytes are not the reference's `json.dumps`.** `json.dumps` puts a space after every `,` and
`:` and escapes non-ASCII (`ensure_ascii=True`); `System.Text.Json` writes neither, and spells a
double with the shortest round-trip form (`556`) where Python's `float.__repr__` writes `556.0`.
The choice of `System.Text.Json` is the plan's (the AOT-friendly one), so the comparison is
**semantic**: `viewer_parity.py` parses both answers and re-dumps them, which still compares every
key, every key's *position* and every value — with the numbers compared as values, not spellings.
The one part of the CLI's output where the bytes *are* the target is the `--format json` body
inside `json`, and that is `PyJson`'s, not this serializer's, so it matches exactly.

## What the transport cannot match

The reference serves with `ThreadingHTTPServer`/`BaseHTTPRequestHandler`: HTTP/1.0, a
`Server: BaseHTTP/0.6 Python/3.14.7` header, a `Date:` header and a closed connection after every
answer. Kestrel speaks HTTP/1.1 with keep-alive and adds neither header (the server header is
switched off, and so is every logger — the reference's `log_message` is a no-op, so there is no
access log on either side). What is compared is therefore the **status**, the **content type**
and the **body**, not the header set. The banner's port and the timings are the other two things
that cannot match by construction.

## Decisions

- **`Content-Type` is written by hand.** `Results.Bytes`/`Results.Json` spell it
  `application/json; charset=utf-8`; the reference sends `application/json`, and the harness
  compares the header, so the port writes the status, the content type, the length and the body
  itself (a five-line `IResult`).
- **A missing `id` or `n` is Python's `KeyError` text** — `'id'`, `'n'`, quotes included —
  because that is what the reference's `except Exception` puts in `{"error": …}`.
- **`MapFallback` is the 404.** The reference answers `not found` for every path it does not
  know; the port registers the five routes and one fallback.
- **The page is a placeholder in this step.** `ViewerPage.Render()` answered with a one-line note
  so the API could be exercised on its own; step `230` replaces it with the reference's HTML and
  logo and adds the `page` case to the harness (`docs/230-viewer-page.md`).

## Known deviations

- **The JSON spelling** (above): whitespace, non-ASCII escaping and the way an integral double is
  written. Semantic comparison only.
- **A malformed request body.** The reference's `json.loads` says
  `Expecting property name enclosed in double quotes: line 1 column 2 (char 1)`; the port's
  `System.Text.Json` says `'n' is an invalid start of a property name. Expected a '"'. Path: $ |
  LineNumber: 0, BytePositionInLine: 1.` The status and the shape match; the parser's wording does
  not. The harness exercises the path and excludes that one body and console line.
- **`json.loads("null")`** is `None`, and the reference then raises
  `TypeError: 'NoneType' object is not subscriptable`; the port treats it as a missing `id` and
  answers `'id'`.
- **The transport** (above): HTTP version, `Server:`/`Date:`, keep-alive.
- **`--tesseract` is refused at startup** (step `210`), so `/info` always answers
  `"tesseract": false` and the page's Tesseract tab never appears. The comparison is out of scope
  by decision, not pending (`docs/230-viewer-page.md`).
- **`--math` reaches `OrtOcr`'s missing-model failure** on both sides, but the message names the
  Python exception and path on the reference and the .NET one here; not compared.

## Verification

- `--help` and `--version` are **byte-identical** to the reference's (including the CRLF the
  reference's text-mode stream carries): both files hash the same as
  `fixtures/viewer_help.txt` / `viewer_version.txt`, captured by `gen_viewer_help.py` from the
  reference's own `main()` with `sys.argv[0] = "indra-ocr-viewer"` (the name the console script
  has).
- `dotnet test src/Indra.Ocr.slnx --filter ViewerArgparseTests` — 6/6: the help, the version, the
  error wording (including `ambiguous option: --t takes --threads, --tesseract, --tesseract-lang`),
  the defaults and the option reading.
- `tools/scripts/test/viewer_parity.py` — **11/11**: `info`, `page` (added in `230`), `not-found`,
  `errors`, `bad-json`, `upload-and-page` (all six inputs), `page-options` (default, `mode text`,
  `mode ocr`, `model_order`, `image_ocr false`, `dpi 100`, `furniture`), `page-2`, `concurrent`
  (added in `240`), `five-uploads` and `stderr`.
  Each case compares the status, the content type and the body of every request, and the console
  lines that case made the servers write. The harness starts both servers once (the models load
  once each) and drives them with the same requests; the six inputs are the ones the C# engine
  tests use, so the two harnesses cover the same documents.
- `dotnet test src/Indra.Ocr.slnx` — 155/155; `cli_parity.py` — 77/77 (step `215` moved the parser
  into Core and the command line did not shift).
