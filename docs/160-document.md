# 160 — document Converter

**Status:** done.

## Goal

Port `indra_ocr/document.py`: the layer that turns a *document* — a PDF or an image file — into a
sequence of OCR pages. For a PDF that is the text layer where it can be trusted and OCR
everywhere else (`pdf_page` / `_text_page`), plus `page_scale`, the error paths (`_safe`, the
"cannot open" wording) and the dispatch the file's own bytes decide (`is_pdf`). For an image file
it is "every frame is a page".

The module's three run-level algorithms (`readable`, `right_to_left`, `assign_runs`) are
recognition-result code, so they live in Core with the text layer (step `150`); the PDFium half
of `extract_runs` stays with the PDF backend (step `140`/`165`).

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Pdf/PdfConverter.cs` | `PdfConverter` — `PageScale`, `PdfPage`, `TextPage`, `Convert` |
| `src/Indra.Ocr/Images/ImageConverter.cs` | `ImageConverter` — `Convert` |
| `src/Indra.Ocr.Core/Text/TextLayerRuns.cs` | `readable`, `right_to_left`, `assign_runs` |
| `src/Indra.Ocr.Core/Text/TextLayerReader.cs` | `read_text_layer` (step `150`) |
| `src/Indra.Ocr.Core/Pipeline/SafePage.cs` | `document._safe` → `SafePage.Read` / `From` |
| `src/Indra.Ocr.Core/Pipeline/OrtOcr.cs` | `IsPdf` (`is_pdf`) |
| `src/Indra.Ocr.Tests/DocumentTests.cs` | the parity tests (7) |
| `src/Indra.Ocr.Tests/fixtures/document.json` | the fixture (94 899 bytes) |
| `tools/scripts/test/gen_document_fixtures.py` | its generator |

`Converter` is not one class here: the PDF half and the image half are the two decoder projects'
own (`PdfConverter`, `ImageConverter`), and the host picks with `OrtOcr.IsPdf` — the split of step
`165`.

## Shape

```
Convert(path, max_pages, password):        # convert
  not path.is_file()  -> InputError("{path}: no such file")
  not is_pdf(path)    -> read_images(path)[: max_pages or None]
                         yield i, SafePage.Read(() -> ocr(gray))
  doc = PdfDocument(path, password)        # PdfiumError -> InputError
  n = len(doc) if not max_pages else min(len(doc), max_pages)
  yield i, SafePage.Read(() -> PdfPage(doc, i))

PdfPage(doc, index):                       # pdf_page
  note = "mode=ocr"
  if mode != "ocr":
    t[text_ms]; layer = read_text_layer(page, render(1.0), 1.0)
    unreadable = layer.reason != BROKEN_FONT_MAP and not readable(ocr, layer.lines)
    if layer.lines and (layer.trusted or mode == "text" or unreadable):
      return TextPage(page, layer, t)
    note = layer.reason
    turn = not (layer.reason == BROKEN_FONT_MAP and rotated < 0.5 * chars)
  else:
    turn = True
  t[render_ms]; gray = render(page_scale(page))
  out = ocr(gray, turn); out.timings = {**t, **out.timings}
  out.timings["total_ms"] = sum(v for k, v in timings if k != "total_ms")
  out.note = f"{note}; {out.note}"

TextPage(page, layer, t):                  # _text_page
  t[render_ms]; gray = render(page_scale(page))
  t[det_ms]; boxes = ocr.detect(gray)
  toks, lost = assign_runs(layer.lines, boxes[:, :4], k)
  per box: text = ocr._decode(toks) -> (text, 1.0, toks) or "empty"
  source = "text"
  if empty and image_ocr and readable(ocr, layer.lines):
    t[rec_ms]; for the empty boxes: recognize -> replace when r[0].strip()
  keep = boxes with text; assemble(..., content_order=pdf_order)
  out.source = source
  out.note += f"; {lost} chars outside detected lines" if lost
```

## Port notes

- **The converter is two classes because the decoders are two projects.** `document.convert`'s
  branch on the file's own bytes is `OrtOcr.IsPdf` at the call site, and each half lives with the
  decoder it needs: `PdfConverter` (PDFium, page rasterisation, the text layer) and
  `ImageConverter` (ImageSharp frames). Neither project references the other, and `Core` holds
  everything both share. The `File.Exists` check must come **before** `IsPdf`, because the
  dispatch reads the file.
- **`max_pages` is `min(len(doc), max_pages)` for PDFs and a slice for images** — and the image
  half really does decode the whole file first: `read_images` returns a `List<GrayImageData>`
  (one entry per frame, each owning its pixels) and only then is the list sliced, exactly like
  `cv2.imreadmulti`'s list in the reference.
- **`SafePage.Read` replaces `Converter._safe`.** It wraps the page builder in `try/catch` and
  turns a failure into the empty page whose `note` is `f"{type(e).__name__}: {e}"`. The
  exception's *type name* is part of the output, which is why the PDFium error type keeps its
  Python name (`PdfiumError`, step `165`).
- **`int index = i` before the lambda.** Python's `lambda: self.pdf_page(..., i)` reads the loop
  variable when `_safe` calls it (immediately, so the current value); a C# `for` variable is
  shared, so the index is copied into a local first.
- **The timings merge is `{**t, **out.timings}`.** The converter's own keys (`text_ms`,
  `render_ms`) come first, the pipeline's follow, a shared key keeps its *position* (Python dict
  update and .NET's `Dictionary` both do that) and `total_ms` is recomputed as the sum of all the
  others. The test asserts the key *order* as well as the total.
- **`note` is composed, not replaced.** `f"{note}; {out.note}"` on the OCR path (the text-layer
  verdict, or `mode=ocr`, in front of the pipeline's own note) and
  `f"; {lost} chars outside detected lines"` appended on the text path.
- **Text-layer lines get confidence 1.0.** `rec[i] = (text, 1.0, tk)`: the text is PDFium's, so
  there is no recognizer confidence. `_decode` still collapses the tokens (the CTC collapse over
  the layer's characters), and a line whose decode is empty is a candidate for the image-OCR
  fallback.
- **`source` is `"text"` and becomes `"hybrid"`** as soon as one empty box is filled from the
  recognizer, and only then is `rec_ms` recorded — the reference only starts the clock when the
  fallback runs. The gate is `image_ocr and readable(ocr, tl.lines)`: a layer in a script the
  recognizer cannot read is not re-OCR'd.
- **`turn` on the OCR path reuses the text-layer stats.** A page whose layer has a broken font map
  still places its glyphs, so when most of its characters are upright the page is upright and
  `orient.onnx` is not asked (`turn = False`); every other failing layer asks the orientation
  model. `mode="ocr"` never reads the layer, so `turn = True`.
- **The `mode` error is `repr`'d** (`mode must be auto, ocr or text, not 'x'`), so
  `PdfConverter` carries a small Python-`repr` helper for the one message that needs it.
- **`page_scale` is `min(dpi/72, sqrt(100e6 / max(w*h, 1)))`** — the large-page cap. It is a pure
  function of the page size and the dpi, so the fixture checks it on its own (11 cases).
- **`assign_runs` compares in float32.** The centre is computed in double (the box midpoints are
  float64 in the reference) and then cast to float32 for the containment test, because numpy
  casts the Python float scalar to the float32 array's dtype (NEP 50) — a centre a hair below a
  box edge still lands inside when it rounds onto it. `argmin` over the *containing* boxes picks
  the first minimum, which is what `np.flatnonzero(...)[np.argmin(area[inside])]` returns.
- **`right_to_left` asks for the bidi class of a one-character string only** (`len(t[0]) == 1`),
  which matters for the astral tokens the recognizer can produce. The bidi table is step `145`.
- **The `_read_math` branch is unreachable** (`getattr(self.ocr, "math", None) is not None`):
  `OrtOcr` refuses `math=True` until step `200`, and the shipped model set has no math models.
  The port notes the branch where it sits instead of porting it twice.

## Verification

- `tools/scripts/test/gen_document_fixtures.py` regenerates `fixtures/document.json`
  byte-identically (94 899 bytes) from the Python reference.
- `DocumentTests` (7) passes against it: `page_scale` (11 cases, bit-exact doubles), `right_to_left`
  (13), `assign_runs` (13: every token's character, float64 page x, content index and the lost
  count), `readable` (18), and the end-to-end cases below — every page's lines (box/text/conf),
  markdown, blocks (label/bbox/text), source, note, rotation, skew and timing **keys**, with
  `total_ms` asserted to be the sum of the others.
- 16 PDF cases over 9 sample documents: the trusted text layer (`text`), the same page in
  `mode=ocr` and `mode=text`, `pdf_order=False`, `dpi=100.0`, `image_ocr=False`, `max_pages=1`,
  the image-only page, the `/Rotate 90` page, the cropped page, the invisible-text page, the
  no-text page (and `mode=text` on it), the broken-font-map page and the encrypted page (with its
  password).
- 1 image case (an example page under `resources/`) checks the image path end to end, and
  `error_cases` (2) pins the exact `InputError` wording for an unopenable and a locked PDF; the
  tests add "not a PDF or a readable image" and "no such file" for the image decoder.
- Full suite green (see `docs/165-project-split.md` for the count after the split), build clean.

## Deviations

- **`_safe` is a static helper, not a method.** It only needs the exception, and the two
  converters call it the same way.
- **The sample PDFs live inside the fixture** (base64, from `tools/scripts/test/pdf_samples.py`,
  shared with steps `140`/`150`), so nothing binary is committed and the tests write the files
  they convert. The example page and the model folder are external material, so those tests
  return early when either is missing, like the other `TestModels`-based tests; the fixture
  records the page's sha256 to catch a re-downloaded page.
- **Timings are the only non-deterministic output** and the fixture records only the keys and the
  `total_ms` identity.
