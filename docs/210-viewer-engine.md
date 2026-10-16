# 210 — the viewer engine

**Status:** done.

## Goal

Port `viewer.Engine` — the state behind `indra-ocr-viewer`: the uploads it is holding, the
recognizers it has built, and the two operations the HTTP layer exposes, `upload` and
`page`. No HTTP yet (that is step `220`); the engine is what the API will call.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Viewer/Engine.cs` | the port of `viewer.Engine` |
| `src/Indra.Ocr.Viewer/Requests.cs` | `ViewerDefaults`, `PageOptions`, `PageRequest` |
| `src/Indra.Ocr.Viewer/Responses.cs` | `UploadResponse`, `PageResponse`, `JsonPage`, … |
| `src/Indra.Ocr.Viewer/ViewerError.cs` | the exception the API turns into `{"error": …}` |
| `tools/scripts/test/gen_viewer_fixtures.py` | writes `fixtures/viewer.json` from the reference |
| `src/Indra.Ocr.Tests/ViewerEngineTests.cs` | the parity test |
| `src/Indra.Ocr.Tests/fixtures/viewer.json` | the recorded responses (12 pages, 3 error texts) |

## Shape

```
Engine(PyPath? modelPath, int threads, bool tesseract, ViewerDefaults defaults)

  UploadResponse Upload(ReadOnlySpan<byte> data)        -> { id, pages }
  PageResponse   Page(string id, int n, PageOptions o)  -> the page the browser draws
```

- `%PDF-` as the first five bytes decides PDF or image; a PDF is written to the engine's own
  temporary directory (the reference hands pypdfium2 the bytes, then reopens the temp file per
  request) and its page count comes from the document; anything else goes through
  `ImageFiles.ReadImage`, and a decode failure becomes
  `not a PDF or a readable image` — the reference's `ValueError` text, which is what `cv2`'s
  `None` produces. `ReadImage` now translates the codec's own exceptions into an `InputError`
  with that wording (the way the file path already did), so the engine — and the API above it —
  reports the reference's message without naming ImageSharp's exception types.
- The id is `uuid4().hex[:12]`, i.e. the first twelve hex digits of a fresh GUID.
- Five uploads are kept; the oldest falls off, and its temp file goes with it.
- One recognizer per `(math, furniture)`, built on first use, warmed up with a 200×200 white
  page so the first request is not the one that pays for the allocations. The default pair is
  built eagerly in the constructor, so a bad `--model-dir` fails at startup — the reference
  builds the same recognizer before it starts serving.
- **One lock around the whole of `page`**: PDFium is not thread-safe (the reason the command
  line spreads pages over worker *processes*), and the reference serialises its own `page` the
  same way.

## What a page response contains

The reference builds a plain dict; the port builds `PageResponse`, whose properties are
declared in the reference's insertion order so a serializer cannot reorder them:

| Field | Where it comes from |
|---|---|
| `page` | `n + 1` (1-based; the nested `json.page` is 0-based, as the CLI writes it) |
| `w`, `h` | the preview's size, i.e. the page turned upright, then shrunk |
| `img` | `data:image/jpeg;base64,` + the JPEG at quality 82 |
| `lines` | `{box, text, conf}`, `box` scaled onto the preview |
| `blocks` | `{label, box, text}`, `box` scaled onto the preview |
| `markdown` | `page.markdown` if the order model produced one, else `page.text` |
| `source`, `note`, `rotation`, `skew` | the page's own |
| `json` | the page as `--format json` writes it (minus `file`): raw boxes, raw Markdown, raw timings |
| `timings` | `render_ms` first, then every `TIMING_KEYS` entry (zero when the step did not run), then what the pipeline filled in — the reference's dict order, which the JSON keeps |
| `tesseract` | always absent: see below |

The preview is `page.render(scale=conv.page_scale(page), grayscale=True)` — the converter's own
scale, so the boxes land on the picture — turned upright, shrunk so its longest side is
`MAX_SIDE = 1600`, and JPEG-encoded:

- the turn is `np.rot90(gray, -(rotation // 90))`; `Orientation.Rotate90` takes the same
  quarter-turn count (negative meaning clockwise) and swaps the sides for odd counts;
- the shrink is `s = min(1.0, 1600 / max(h, w))`, and `cv2.resize(..., fx=s, fy=s, INTER_AREA)`
  resolves to `dsize = (cvRound(w·s), cvRound(h·s))` — the same `CvResize.Resize8u` +
  `Interpolation.Area` the pipeline uses. A page already inside 1600 px is not resized at all
  (`s == 1.0`), and `scaled()` then returns the boxes unchanged.
- the boxes are `round(v·s, 1)`, i.e. `Numerics.RoundHalfEven`, and the same `s` — not the
  resize's internal scale — which is what the reference does.
- the JPEG itself is the one part of the response that cannot match: Python encodes with
  OpenCV's libjpeg and the port with ImageSharp. The test checks the prefix and that it is a
  JPEG; the pixels are compared by eye, not by bytes.

`page` prints the reference's console line — `page {n+1}: {source}, {N} lines, {M} blocks,
{total_ms:.0f} ms ({mode}[, math])` — through `PyConsole`, after the lock, as the reference
does.

## Decisions

- **`--tesseract` is refused at startup.** The reference imports `pytesseract`, a Python
  package that drives the `tesseract` binary; neither is present on this machine, so the
  reference itself dies at startup with `ModuleNotFoundError` when the option is given. The
  port has no equivalent and says so (`ViewerError: tesseract is not supported by this port`,
  exit 1) rather than silently ignoring the flag. Recorded as a deviation, and **out of scope by
  decision** (user, 2026-10-16) — not something a later step picks up.
- **Serialisation is step `220`.** The engine returns records; the JSON spelling (and the
  `source`-generated serializer) is the next step's business. The records are declared in the
  reference's key order so that step can keep it.
- **An unknown upload is `'abc123'`** — the text of Python's `KeyError`, quotes included —
  because that is what the reference's handler puts in `{"error": …}`.
- **A bad mode is `mode must be one of ('auto', 'ocr', 'text')`**, the reference's
  `str(MODES)`.
- **`dpi` follows `float(opts.get("dpi") or 200.0)`**: absent *or* `0` means 200.
- `image_ocr` defaults to `true`; `model_order` is inverted (`pdf_order = not model_order`).

## Verification

`fixtures/viewer.json` is written by driving the reference's own `viewer.Engine` over three
sample PDFs (`text`, `rotated`, `image`) and three synthetic PNGs — the same dense page, and
that page turned by 90° and 180°, which is what makes the orientation model sure enough
(`p[k] > 0.95`) to turn it, so the engine's own rotation path is exercised — through twelve
option sets (default, `dpi 100`, `mode text`, `mode ocr`, `model_order`, `image_ocr false`,
page 2, the turned pages). `ViewerEngineTests` uploads each input, reads the page and compares
the response with the record **field by field**, ignoring the JPEG bytes and the timing
*values* (the keys and their order are compared).

The error texts are recorded too: a page asked for a mode outside `MODES`, a page of an
unknown upload, and an upload that is neither a PDF nor a readable image.

- `dotnet test src/Indra.Ocr.slnx --filter ViewerEngineTests` — 4/4: the twelve pages, the two
  page errors, the upload error, and the five-upload limit.
- The comparison was checked against a deliberately wrong fixture (`"rotation": 90` →
  `91`): it failed with `png-turn-90.rotation: expected 91, got 90` and
  `png-turn-90.json.rotation: expected 91, got 90`, so the test can fail and says where.
  (A stale copy of that experiment in `bin/` also produced two spurious full-suite failures:
  `PreserveNewest` compares timestamps, and restoring the fixture preserved the *older* one.)
- The generator was run twice: `defaults`, `inputs`, `uploads`, `cases` and `errors`
  came out identical, so the fixture — the synthetic PNGs included — is reproducible.

## Known deviations

- The JPEG preview is not byte-comparable (above).
- `--tesseract` is refused (above).
- An **empty upload**: the reference answers with OpenCV's own assertion text, complete
  with its build path and version; that is an implementation detail, so the port answers
  `not a PDF or a readable image` instead.
- The model folder is resolved by `ModelFolder.Resolve` (`docs/205-core-shared.md`), the same
  code the command line uses.
