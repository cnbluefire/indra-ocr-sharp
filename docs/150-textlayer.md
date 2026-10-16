# 150 — textlayer

**Status:** done.

> **The types moved to `Indra.Ocr.Text` and the `ITextPage` seam was deleted by
> [`165`](165-project-split.md).** `TextLayer`/`TextChar`/`TextLine`/`TextLayerStats`/
> `TextLayerReader` now live in `src/Indra.Ocr.Core/Text/`, and `ExtractRuns` is
> `PdfPage.ExtractRuns` (the PDFium half), which `ReadTextLayer` calls lazily through a `Func`.

## Goal

Port `indra_ocr/textlayer.py`'s policy: read a page's text-layer runs and decide whether the
layer can be trusted in place of OCR. The four checks follow the rules that select the
recognizer's own training labels — invisible text (render mode 3) marks an OCR layer over a
scan, replacement / private-use / control characters mark a broken font map, and a run with no
ink under it marks hidden or misplaced text.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Document/TextLayer.cs` | `TextChar`, `TextLine`, `TextLayerStats`, `TextLayer`, `TextLayerReader` (`BadCharCount`, `ExtractRuns`, `NoInkFraction`, `ReadTextLayer`) |
| `src/Indra.Ocr.Core/Text/UnicodeText.cs` | `CollapseSpaces` (promoted from `Assembler`; `" ".join(s.split())`) |
| `src/Indra.Ocr.Tests/TextLayerTests.cs` | the parity tests (4) |

The two content-order checks that also live in `textlayer.py` (`order_backtracks`,
`pdf_order_coherent`) are in `Assembler` (step 130) — their only runtime caller — and step 150
builds on them rather than re-implementing them.

## Shape

```
extract_runs(page):                     # one pass over the text page
  c = chr(FPDFText_GetUnicode(i))
  "\r" / "\n"      -> a run break
  "\x02"           -> "-"               # pdfium's hyphen-at-line-break mark
  not c.isspace()  -> count it; count bad chars; drop it if not upright
  keep             -> FPDFText_GetCharBox (l, r, b, t) or TextPageError
  then: box each character's four corners through _device_affine, drop the
  characters whose centre is off the page, and cut the text at the breaks:
  a run's box is the ink characters' bounding box, its text is the segment
  with whitespace collapsed
invisible_text_frac(page) > 0.05         -> "invisible text {inv:.0%} (OCR layer)"
no runs                                  -> "no text layer"
bad > 0.005 * max(chars, 1)              -> "broken font map"
no_ink_frac(gray, runs, scale) > 0.03    -> "{empty:.0%} of text runs have no ink"
otherwise                                -> trusted
```

## Port notes

- **Uprightness is a property of the rendered frame, not the character.** A character is kept
  when its baseline maps to a left-to-right direction through the affine's linear part:
  `d = L @ (cos a, sin a)`, upright unless `d[0] <= 0` or `|d[1]| > 1e-3 * max(|d[0]|, 1e-9)`.
  So a `/Rotate 90` page drops every character (pdfium reports angle 0 for them; the affine is
  what swaps the axes), which is why the rotated sample's verdict is "no text layer". The test
  is cached per angle, as in the reference.
- **`chr` is the UTF-16 code unit, not the code point.** `FPDFText_GetUnicode` hands out code
  units, so lone surrogates are normal and must survive as one-character strings (they are
  counted as bad characters, category `Cs`). Astral code points would come back as a surrogate
  pair; the helper handles both.
- **`BadCharCount` is `unicodedata.category`.** The replacement character, `Co`, `Cs`, `Cn`,
  and `Cc` other than a tab. .NET's `CharUnicodeInfo` reports the same categories as Python's
  `unicodedata` 16.0.0 here; the fixture pins 19 cases down (including astral private use, an
  unassigned point, formats and a tab, which is *not* bad).
- **The bad-character cache is per character, the upright cache per angle** — the same two
  dicts, keyed the same way (a one-character string; a float32 angle widened to double). They
  only save work, so their absence would change nothing observable.
- **The box transform is float64 and evaluated left to right**
  (`A[0,0]*x + A[0,1]*y + A[0,2]`), with the four corners `(l,b) (r,b) (l,t) (r,t)` and the
  bounding box of the results — `min`/`max` are exact, so the reduction order is irrelevant.
  Neither numpy nor .NET contracts `a*b + c` into an FMA, so the arithmetic matches bit for
  bit.
- **A missing character box throws only for a character the policy keeps**:
  `TextPageError("Failed to get charbox.")`, raised after the character is appended, exactly
  where the reference raises it (the reference's own type is `pypdfium2.PdfiumError`; the string
  `PdfiumError` is restored where `_safe` prints it, step 160). `ITextPage.ReadChars` records
  `HasBox` instead of throwing, so a missing box for a skipped character stays silent, as in
  Python.
- **The ink check reads the render it is handed** (`gray < 160`), over the run's box scaled by
  the same `scale`, with `int()` truncation and `+1` on the far edge. A run's box always
  contains the centre of one of its characters and the visibility filter kept that centre on
  the page, so the slices are always well formed — the `max(0, ...)` / `min(H/W, ...)` clamps
  never produce a negative slice bound (which numpy would read as an offset from the end).
- **`{x:.0%}` is `round(x * 100, 0)` then `"%"`** — Python multiplies by 100 first and then
  rounds half to even (`Numerics.RoundHalfEven`), which is what the "invisible text 50% (OCR
  layer)" and "100% of text runs have no ink" messages need.
- **`round(empty, 4)`** for the recorded `no_ink` is Python's `round` (half to even), not
  `Math.Round`'s default.

## Verification

- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **116 passed** (was 112; +4 `TextLayerTests`), 14 s.
- Every sample page's `extract_runs` result is compared against the reference's: the runs (box,
  text, and every character with its box), the stats (`chars`, `rotated`, `bad`, `off_page`,
  and the presence or absence of `no_ink`), and `read_text_layer`'s verdict (`trusted`, the
  reason string, its stats, and its line count) on the same `check_scale` render.
- Coverage of the checks: trusted text (plain, fractional page, cropped page, image page,
  encrypted page), "no text layer" (a rotated page, and a page with no text), "invisible text
  50% (OCR layer)", "broken font map" (8 bad characters out of 11), and the no-ink verdict
  (forced with a white render: "100% of text runs have no ink").
- `BadCharCount` is checked on 19 recorded cases.

## Deviations

- **The stats are a typed record, not a dict.** The reference's `stats` dictionary grows a key
  at a time (`off_page` in `extract_runs`, `no_ink` in `read_text_layer`); here they are
  properties, and `NoInk` stays null until the ink check runs. The tests assert that the
  presence of each key matches, so the difference is invisible to a caller.
- **`_device_affine` is called once, not twice.** The reference calls it before the loop (for
  `L`) and again after (for `A`); the second call is deterministic and identical, so one call
  serves both.
- **`math.cos`/`math.sin` are .NET's.** Both call the platform libm; the uprightness test is a
  `1e-3`-tolerant comparison, so a last-bit difference could only matter for an angle whose
  mapped direction sits exactly on the threshold. The recorded angles (0, and the `/Rotate 90`
  page's 0) are far from it.
