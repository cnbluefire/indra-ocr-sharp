# 085 — `line_mask`: CenterMask windowing

**Status:** done.

## Goal

Port `indra_ocr/line_mask.py`: cut a window around each line box, mask everything that is not
the centre line with the CenterMask model (`mask.onnx`), and hand the recognizer crops that went
through the same masking step as training.

The shipped model set has **no** `mask.onnx` (like the math models), so `OrtOCR.mask` is `None`
and `crop_lines` reduces to plain `crop_line`. The code has to behave identically either way,
and the mask path has to be right for a folder that *does* ship the model — so it is ported in
full and tested with a stand-in model (below).

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Rec/LineMask.cs` | `IMaskModel`, `MaskedWindow`, `LineMask` (`WindowBox`, `WindowRect`, `MaskWindows`, `MaskedWindows`, `CropLines`) |
| `src/Indra.Ocr.Core/Rec/RecPreproc.cs` | `CropLine` overload taking the box in double |
| `src/Indra.Ocr.Core/Onnx/OnnxMaskModel.cs` | `IMaskModel` over an `InferenceSession` |
| `tools/scripts/test/gen_line_mask_fixtures.py` | → `fixtures/line_mask.json` |
| `src/Indra.Ocr.Tests/LineMaskTests.cs` | the parity tests |

## Constants

`WINDOW_H = 96`, `WINDOW_ABOVE = 1.0`, `WINDOW_SIDE = 0.5` (in box heights), `KEEP = 0.5`.

## Geometry: `window_box` / `_window`

```
h  = max(1.0, y1 - y0)
X0 = max(0, x0 - 0.5*h)   Y0 = max(0, y0 - 1.0*h)        # clipped to the page
X1 = min(W, x1 + 0.5*h)   Y1 = min(H, y1 + 1.0*h)
x0', y0', x1', y1' = int(round(X0)), int(round(Y0)), ...  # Python round: ties to even
```

- The padding is a fraction of the **height**, so a tall box gets a wider window.
- Rounding is `CvMath.Round` (ties-to-even), not truncation — the port shares it with the
  resize code.
- The clip can collapse a window for a box at (or outside) the page edge: see the deviation
  below.

## `mask_windows`

For every window:

```
W    = max(32, round(wd * 96 / max(h,1) / 8) * 8)
shrink = 96 / max(1, h)                     # resize_window's own scale test
ink  = 1 - resize(window, (W, 96), INTER_LINEAR if shrink >= 0.5 else INTER_AREA) / 255
```

`resize_window` compares the **target** height with the source height, so it shrinks a tall
window with `INTER_AREA` and grows a short one with `INTER_LINEAR` — the opposite convention to
`page_to_input`, which compares scales; both approximations are reproduced as written.

The windows then go through the model in groups of 16, sorted by ink width and zero-padded to
the widest in the group (`[B, 1, 96, Wm]`). Each probability row is **upscaled bilinearly back
to the window's own size before the `>= 0.5` threshold** — the source comments that
thresholding at mask resolution and upscaling with nearest neighbours breaks the stroke edges
(2 CER on 804 lines with 0.30 → 0.36). The upscale of the float32 probability map is the
`Resize32f` path from step `040`.

## `masked_windows`

```
paper = uint8(percentile(window[::4, ::4], 90))       # every 4th row and column
window[~keep] = paper if window.size else 255
box_in_window = [b0 - x0, b1 - y0, b2 - x0, b3 - y0]  # Python floats -> double
```

- The paper level is the 90th percentile grey on a 1/16 subsample, truncated to a byte
  (`np.uint8` truncates toward zero; `(int)` in the port). `np.percentile`'s linear
  interpolation is the one already pinned in step `020`.
- The shifted box stays in **double**: the reference keeps a list of Python floats, and
  `crop_line` then reads them back as floats. Rounding the shift to float32 first would change
  the crop geometry, so `RecPreproc.CropLine` gained a `ReadOnlySpan<double>` overload (the
  float32 overload widens and delegates, so the detector path is unchanged).

## `crop_lines`

`crop_line` of every window with its shifted box, or of the page directly when there is no mask
model. The port returns `List<float[]>` (a crop is `height × (crop.Length / height)`); the
recognizer derives the widths from that, exactly like `[c.shape[1] for c in crops]`.

## Deviations

- **Degenerate windows.** A box that the page clip collapses (`x1 <= x0` or `y1 <= y0`) makes
  the reference **raise**: `cv2.resize` rejects the empty window and `np.percentile` rejects the
  empty subsample. `OrtOCR.detect` clips boxes to the page, so this needs a zero-width box
  exactly on a page edge — but it is reachable, and a port cannot raise `cv2.error`. The port
  degrades: the keep mask is empty (nothing is kept), the window is empty, and `crop_line` gets
  a 1×1 **white** page, which yields exactly the all-white crop that the reference's
  `np.full((ch, cw), 255)` would have produced had the resize not failed. Covered by
  `Mask_windows_handles_a_window_the_page_clip_collapsed`.
- **`np.argsort` tie order.** The windows are sorted by ink width before batching; numpy's
  quicksort dispatches to a CPU-specific SIMD network, so equal widths can come back in any
  order. The port sorts **stably** (see `AGENTS.md`). The grouping only decides which windows
  share a call and therefore how much zero padding each row carries; a fully convolutional mask
  model's values inside a window do not depend on the padding, so the kept pixels are unaffected
  either way.

## `OnnxMaskModel`

The adapter over `InferenceSession`: `x` f32 `[B, 1, 96, W]` in, the first output out. The
object borrows the session (the model set owns it) and validates the output length. It cannot be
exercised on this machine — no `mask.onnx` — so it is covered by the same code path the
recognizer will drive in step `100`, and the trace of the reference is recorded here:
`load_mask(path, threads=1)` builds a `SessionOptions` with `intra_op_num_threads = 1`, i.e. the
mask model is single-threaded even when the rest of the pipeline is not.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → **51 passed** (was 48; +3 `LineMaskTests`).
- `fixtures/line_mask.json` (113 KB): 4 pages (crops of `temp/inputs/{latin,cjk}.png`, an
  ink-band page and a grey page with noise), stored once; 4 cases covering 18 lines:
  - `page_latin_256x96_flip` / `page_cjk_200x80_flip` — the real detector boxes, clipped the way
    `detect` clips them; the stand-in keeps 78–93 % of each window;
  - `page_bands_96x72_parabola` — hand-placed boxes touching every page edge and reaching
    outside it, on a binary page where the stand-in keeps **nothing** (the whole window is
    whitened: the boundary case of `masked_windows`);
  - `page_grey_96x72_parabola` — the same boxes on a grey+noise page, where the stand-in keeps
    84–100 % and the paper level actually varies.
  Each case pins the keep masks, the whitened window pixels, the box inside the window (as
  doubles) and the final recognizer crops, all bit-exact.
- `Window_box_clips_to_the_page_and_uses_the_box_height` covers the geometry directly,
  including the degenerate box (`y1 < y0` → height 1.0).
