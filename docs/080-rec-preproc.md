# 080 — `rec_preproc`: the line crop rule and line batching

**Status:** done.

## Goal

Port `indra_ocr/rec_preproc.py`: `crop_line` (line box → recognizer input) and the two batching
helpers used by `OrtOCR.recognize`, `pad_batch` and `width_batches`.

`line_mask.py` — the CenterMask step that also feeds `crop_line` — is its own step (`085`),
because it needs a model (`mask.onnx`) that is not in the shipped model set.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Rec/RecPreproc.cs` | `CropLine`, `PadBatch`, `WidthBatches` |
| `tools/scripts/test/gen_recpreproc_fixtures.py` | → `fixtures/rec_preproc.json` |
| `src/Indra.Ocr.Tests/RecPreprocTests.cs` | the three parity tests |

## Constants

`LINE_HEIGHT = 32`, `PAD_Y = 0.2`, `PAD_X = 0.3`, `WIDTH_MULTIPLE = 8`. Training and inference
share the crop rule, so the text scale and the margins match; recognition training re-exports
them from here.

## `crop_line`

```
h  = max(1.0, y1 - y0)
X0, X1 = x0 - pad_x*h, x1 + pad_x*h      # pad_x of the *height*, not of the box width
Y0, Y1 = y0 - pad_y*h, y1 + pad_y*h
ix0, iy0 = floor(X0), floor(Y0);  ix1, iy1 = ceil(X1), ceil(Y1)
cw, ch   = max(1, ix1 - ix0), max(1, iy1 - iy0)
crop     = full((ch, cw), 255)           # white outside the page
crop[...] = gray[clipped to the page]
out_w    = max(8, min(max_width, round(cw * height / ch)))
resize(crop, (out_w, height), INTER_AREA if ch > height else INTER_LINEAR)
ink      = 1 - resized / 255             # float32
```

Parity notes:

- The box is converted from float32 to **double** (`float(v)`), and the whole geometry
  (`h`, the padding, `floor`/`ceil`) is computed in double. The port takes `ReadOnlySpan<float>`
  and widens, so the caller cannot accidentally hand it doubles.
- `max(1.0, y1 - y0)` means an inverted or degenerate box still gets a one-pixel-tall band; a
  reversed box (`x1 < x0`) produces `cw = 1` or a small negative-extent crop that is then
  clamped by `max(1, …)`. Both are in the fixture instead of being assumed away.
- `round()` is Python's ties-to-even on the double `cw*height/ch` → `MidpointRounding.ToEven`.
  The `_ratio_tie` case (`cw*33/ch` exactly `.5`) pins it.
- The interpolation choice is `ch > height` — **not** a scale comparison, unlike
  `page_to_input`. An upscale therefore uses `INTER_LINEAR`, a shrink `INTER_AREA`.
- `out_w` is clamped below by 8 and above by `max_width` (default 4096); a one-pixel-tall box
  would otherwise explode through the aspect ratio (the `_flat` case, run with a small
  `max_width` so the fixture stays small).
- The ink map is again the float32 `1 - v/255`; the earlier `1 - gray/255` proof (step `070`)
  covers the arithmetic, and here the input is the resize output.

## `pad_batch`

Right-pads every line to `ceil(max_width / multiple) * multiple` columns, so a batch is one
`[B, 1, height, width]` tensor, and reports the original width of each line. Padding is zeros,
i.e. paper, which is invisible to the recognizer. The port takes the line height explicitly
(the Python reads it from `shape[0]`) and writes the widths into a caller-provided array.

## `width_batches`

Lines are grouped narrowest-first into batches of similar width: a batch closes at `max_batch`
lines, when the next line is wider than `max_ratio` times the batch's **first** line (widths
below `min_width` count as `min_width`), or when `lines * width` would pass `max_area`
(64 × 640 px). ONNX Runtime keeps its largest allocation, and 64 lines of 4096 px held 5 GB on
large scanned pages.

- Python's `sorted(..., key=widths[i])` is **stable**; the port sorts indices with an index
  tiebreak, so equal widths keep their input order. That is observable: the first line of a
  batch sets its width window, so a reshuffle changes the batching.
- All comparisons are integer (or `1.3 * width` in double) exactly as in the reference.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → **48 passed** (was 44; +4 `RecPreprocTests`).
- `fixtures/rec_preproc.json` (155 KB):
  - 5 pages (crops of `temp/inputs/{latin,cjk,two_columns}.png`, an all-edges-ink page and a
    blank one), stored once and referenced by the cases;
  - 70 `crop_line` cases: every page's **real** detector boxes plus artificial ones — off-page
    on each side, entirely outside the page, `h → 1.0`, reversed, sub-pixel, `max_width`-clamped,
    zero padding, `height` 16/32/64, and the `floor`/`ceil`/rounding alignments. All bit-exact,
    including `out_w`;
  - 4 `pad_batch` and 11 `width_batches` cases (each limit exercised, plus the tie/duplicate
    order).
