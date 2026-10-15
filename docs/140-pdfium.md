# 140 — PDFium backend

**Status:** done.

## Goal

The `Indra.Ocr.Pdf` project: open a PDF (path or bytes, with an optional password) and expose
exactly the PDFium surface `indra_ocr.textlayer` and `indra_ocr.document` use — page count, page
size in canvas units, a grayscale render at a scale, the text layer's characters, the
page-to-render affine, and the share of invisibly drawn text. Plus the seam in Core
(`IPdfDocument` / `IPdfPage` / `PdfChar`) so `Core` keeps its no-PDFium rule, and the owning
`GrayPixels` buffer the pipeline views as a `GrayImage`.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Imaging/GrayPixels.cs` | owning, tightly packed grayscale buffer + `View` |
| `src/Indra.Ocr.Core/PdfText/PdfSurface.cs` | `PdfiumError`, `PdfChar`, `IPdfPage`, `IPdfDocument` |
| `src/Indra.Ocr.Pdf/Pdfium.cs` | library init + `FPDF_GetLastError` text (pypdfium2's `ErrorToStr`) |
| `src/Indra.Ocr.Pdf/PdfDocument.cs` | `Open(path/bytes, password)`, `PageCount`, `PageSize`, `LoadPage` |
| `src/Indra.Ocr.Pdf/PdfPage.cs` | `ReadChars`, `DeviceAffine`, `InvisibleTextFraction`, `Render` |
| `tools/scripts/test/pdf_samples.py` | the sample PDFs (fpdf2 + pikepdf), shared by 140/150 |
| `tools/scripts/test/gen_pdf_fixtures.py` | `fixtures/pdf.json` |
| `src/Indra.Ocr.Tests/PdfTests.cs` | the parity tests (8) |

## Shape

```
# document.Converter.convert
doc = pdfium.PdfDocument(path, password=password)      # len(doc), doc[i], page.get_size()
gray = page.render(scale=page_scale(page), grayscale=True).to_numpy()
# textlayer.read_text_layer
tp = page.get_textpage(); tp.count_chars(); FPDFText_GetUnicode/GetCharAngle/GetCharBox
L = textlayer._device_affine(page)                     # FPDF_PageToDevice, S = 1000
textlayer.invisible_text_frac(page)                    # page.get_objects(max_depth=4), render mode 3
```

## Port notes

- **The PDFium build is the reference's build.** pypdfium2 5.14.0 bundles PDFium **156.0.8076**
  (`pypdfium2_raw/version.json`), the same build as the `PDFiumCore` 156.0.8076 package, so
  rendering is bit-exact rather than merely close. Verified: every recorded render matches byte for
  byte.
- **Documents are loaded from memory, not by path.** pypdfium2 passes a path as explicit UTF-8
  bytes; the managed wrapper marshals `string` in the process's ANSI code page, which would break
  non-ASCII paths on Windows. `Open` therefore reads the file and calls
  `FPDF_LoadMemDocument64`, keeping the bytes pinned for the document's lifetime (PDFium does not
  copy them — pypdfium2 keeps its input buffer alive the same way). `Open(path)` and
  `Open(bytes)` are then the same code path, and the tests need no file.
- **The failure message is pypdfium2's, not the C header's.** `_open_pdf` checks the *page count*
  (a null handle fails identically) and reports
  `f"Failed to load document (PDFium: {ErrorToStr.get(err_code)})."`. `ErrorToStr` says "File
  access error" / "Data format error", while `fpdfview.h`'s comments say "File not found or could
  not be opened" / "File not in PDF format or corrupted" — the pypdfium2 strings win, because
  `document.Converter` puts the message in the CLI output and looks for `"password"` in it.
- **`PdfiumError` is named `PdfiumError` on purpose.** `document._safe` reports a failing page as
  `f"{type(e).__name__}: {e}"`, so the exception's type name is part of the output.
- **Render geometry** is `page.render(scale, grayscale=True)` with the defaults:
  `src_w = ceil(width * scale)`, `src_h = ceil(height * scale)` (no rotation, no crop), a
  `FPDFBitmap_CreateEx(width, height, FPDFBitmap_Gray, buffer, stride=width)` over our own buffer,
  `FPDFBitmap_FillRect(0, 0, w, h, 0xFFFFFFFF)` (white; the byte order does not matter for
  255/255/255/255), then `FPDF_RenderPageBitmap(bitmap, page, 0, 0, src_w, src_h, 0, flags)` with
  `flags = FPDF_ANNOT | FPDF_GRAYSCALE = 9`. PDFium honours the stride it is given (verified with
  odd widths), so the buffer is already packed; a copy guards against a PDFium that pads, and
  `GrayPixels` is always tight.
- **`FPDFBitmap_Gray` is 1**, not 8 (`8` is the `FPDF_GRAYSCALE` *flag*); the wrapper exposes it as
  `FPDFBitmapFormat.Gray` but the raw value is what `FPDFBitmapCreateEx` wants.
- **`ReadChars` is `textlayer.extract_runs`' input loop, minus the policy.** It reports every
  character's code, angle, and box — in `FPDFText_GetCharBox`'s own order (left, right, bottom,
  top) — and records whether PDFium gave a box at all (`HasBox`). The reference raises
  `PdfiumError("Failed to get charbox.")` only for characters it keeps, so the *decision* stays in
  Core (step 150) and a missing box for a character the policy skips stays silent, as it does in
  Python.
- **`DeviceAffine` is `FPDF_PageToDevice` sampled at 1000 device units per point** and divided
  back, with `round(W * 1000)` / `round(H * 1000)` as the device size. The columns are
  `ex - o`, `ey - o`, `o` (`np.stack([...], 1)`), i.e. the x basis, the y basis and the origin; a
  page with a crop box gets its `-crop_left` / `+crop_bottom` offset there, a `/Rotate 90` page the
  axis swap.
- **`InvisibleTextFraction` walks the object tree to depth 4** the way
  `page.get_objects(max_depth=4)` does: the page's objects in order (`FPDFPage_CountObjects` /
  `FPDFPage_GetObject`), descending into `FPDF_PAGEOBJ_FORM` objects *after* yielding them
  (`FPDFFormObj_CountObjects` / `FPDFFormObj_GetObject`), and counting the
  `FPDF_PAGEOBJ_TEXT` objects whose `FPDFTextObj_GetTextRenderMode` is
  `FPDF_TEXTRENDERMODE_INVISIBLE` (3).
- **`IPdfPage`/`IPdfDocument` live in Core** (`Indra.Ocr.PdfText`), with `PdfDocument`/`PdfPage`
  as the PDFium implementation — the same seam shape as `IOrderModel`/`OnnxOrderModel`. Step 150
  adds the text-layer policy against the interface, step 160 the `Converter`, and both stay
  testable without PDFium.

## Verification

- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **112 passed** (was 104; +8 `PdfTests`), 15 s.
- `fixtures/pdf.json` (77 KB) holds 8 sample documents (13 pages) and 5 unopenable-document cases.
  The PDFs travel base64-encoded inside the fixture (nothing binary in git); every page records its
  size, rotation, renders at the sample's scales (raw bytes, zlib+base64), every character (code,
  angle, `has_box`, box), the affine, the invisible-text share — and the reference
  `read_text_layer` result (runs, stats, verdict) that step 150 consumes.
- Sample coverage: two pages of Helvetica text (the second with a fractional page size), an
  embedded grayscale image, a `/Rotate 90` page, a page whose crop box is smaller than its media
  box, a page with an invisible (render mode 3) content stream, a page with no text at all, a page
  whose font has a broken `/ToUnicode` map (private-use + control characters), and an AES-256
  encrypted page. Renders are recorded at scale 0.25 and 1.0 everywhere (plus 0.255 for the odd
  width and the fractional page).
- Unopenable-document cases (empty, garbage, truncated, locked without a password, locked with a
  wrong password) assert the exact pypdfium2 message — "Data format error" / "Incorrect password
  error" — which is what the CLI's "cannot open the PDF" line and its `password`-in-message test
  depend on.
- The tests compare renders byte for byte, sizes/boxes as raw doubles (signed zero included), the
  affine entry by entry, and the character angles as float32.

## Deviations

- **The render is copied into a managed buffer.** The reference views PDFium's buffer through
  `to_numpy()`; `GrayPixels` owns a `byte[]` with the same bytes. `document._render` forces the
  array contiguous anyway, so nothing observable changes.
- **A missing character box does not throw in `ReadChars`**; it is recorded and left to the policy
  (see above). `ReadChars` is the only place that can observe it.
- **`PdfDocument.Open(string path)` reads the whole file first** (see above). The observable
  difference would be a PDFium that refuses a path for reasons other than its bytes — none exists
  for the pipeline's use.
- **The `locked` sample's bytes change on every regeneration**: pikepdf randomises the encryption
  parameters (the /ID, the key salts), so that one `pdf` field is not reproducible. Its page data
  is, and the committed fixture is stable; the other seven samples regenerate byte-identically
  (`deterministic_id=True`).
