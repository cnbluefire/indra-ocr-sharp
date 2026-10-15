# 095 — adaptive detect

**Status:** done.

## Goal

Port `pipeline.OrtOCR._detect_at` and `pipeline.OrtOCR.detect`: run LineNet over the page, repeat
the run at a larger long side when the detected lines come back too short to read, then scale the
boxes back to page pixels and clip them.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Det/Detect.cs` | `IDetModel`, `Detect` (`DetectAt`, `AdaptiveLongSide`, `Run`) |
| `src/Indra.Ocr.Core/Onnx/OnnxDetModel.cs` | `IDetModel` over an `InferenceSession` |
| `tools/scripts/test/gen_detect_fixtures.py` | → `fixtures/adaptive_detect.json` |
| `src/Indra.Ocr.Tests/DetectTests.cs` | the parity tests (+ the deterministic stand-in) |

## Constants

`ADAPTIVE_MIN_H = 9.0`, `ADAPTIVE_TARGET_H = 12.0`, `ADAPTIVE_MAX_SIDE = 2048`; the network works
at half the input resolution (`det_post.OUT_STRIDE = 2`) and its input is padded to a multiple of
`INPUT_MULTIPLE = 32`.

## `_detect_at`

```
x, s = page_to_input(gray, long_side)          # step 070
out  = det.run(None, {"x": x[None, None]})[0][0]   # [3, H/2, W/2]
return decode(out), s                          # step 070; [N, 5] in *input* pixels
```

`DetectAt` composes exactly those two ports and returns the scale too, so a caller can compare the
boxes of the two passes or check the model contract. The `IDetModel` seam is LineNet's signature —
`[1, 1, H, W]` float32 ink map in, `[1, 3, H/2, W/2]` logits out — and `OnnxDetModel` asserts the
returned shape instead of trusting `OUT_STRIDE`.

## `detect`

```
boxes, s = _detect_at(gray, long_side)
if adaptive and len(boxes) >= 8:
    med_h = median(boxes[:, 3] - boxes[:, 1])              # float32
    if med_h < 9.0:                                        # ADAPTIVE_MIN_H
        ls = int(min(2048, long_side * 12.0 / max(med_h, 1.0))) // 32 * 32
        if ls > long_side:                                 # ADAPTIVE_MAX_SIDE
            boxes, s = _detect_at(gray, ls)
if len(boxes):
    boxes[:, :4] /= s
    boxes[:, [0, 2]] = boxes[:, [0, 2]].clip(0, w)
    boxes[:, [1, 3]] = boxes[:, [1, 3]].clip(0, h)
return boxes                                               # [N, 5] in page pixels
```

`AdaptiveLongSide` is that branch on its own: it takes the first pass's boxes and returns the long
side the second pass should use — the input `long_side` when the first pass stands — plus the
median it saw (`NaN` when there were fewer than eight boxes). `Run` calls it only when `adaptive`,
which keeps `adaptive=False` a single pass.

Port notes:

- **`median` stays float32.** `boxes[:, 3] - boxes[:, 1]` is a float32 array and `np.median` of it
  is float32 (widened only when compared). `Numerics.Median` already computes the mean of the two
  middle elements in float32 (`(a + b) / 2f`), which is what `np.mean` does for a 2-element
  float32 array. Verified with a probe (`np.median(float32)` keeps the dtype; the even case equals
  the float32 average bit-for-bit).
- **`med_h < 9.0`** compares the widened float32 median against the double constant, so a median of
  exactly `9.0` does not fire; `!(median < 9.0)` also keeps a `NaN` median from firing, like numpy.
- **`int(min(2048, …)) // 32 * 32`** truncates toward zero and then floors to the 32 grid — both are
  plain integer operations for the positive values here; `Math.Min` is applied in `double`, so the
  int `2048` and a float candidate compare as Python compares them (`min(2048, 4096.0) == 2048`,
  `min(2048, 3000.5) == 2048`) and the cap survives the truncation.
- **`max(med_h, 1.0)`** floors the divisor, so a median below one input pixel (very short lines)
  saturates the candidate at `long_side * 12`, capped at 2048.
- **`boxes[:, :4] /= s` is float32.** `s` is a Python float, but the array is float32 and numpy's
  NEP 50 does not let a scalar widen it: the port casts `s` down to `float` before dividing. The
  probe confirms `(a / s).dtype == float32` and that it equals `a / float32(s)` bit-for-bit.
- **`np.clip(0, w)`** with integer bounds, on columns `[0, 2]` against the width and `[1, 3]`
  against the height; `Math.Clamp` keeps `NaN` in place exactly like numpy (`NaN` fails both
  comparisons). The clip only runs when there is at least one box.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → **67 passed** (was 62; +5 `DetectTests`), 14 s.
- `fixtures/adaptive_detect.json` (85 KB) holds 8 pages and 20 cases (10 real + 10 stand-in), each
  recording the first pass's boxes and scale, the median, the long side the second pass used and
  the final boxes, so a mismatch names the pass it came from.
- The generator does not call `detect` first: it hand-writes the branch, then asserts the hand
  written result against the **real** `OrtOCR.detect` bit-for-bit (the method is reached through a
  stub object carrying only `det`, `long_side`, `adaptive`). Every recorded expectation is
  therefore the reference's own output.
- The **real** `det.onnx` cases (skipped only if `resources/models` is absent) were chosen from a
  probe of the page × long side grid to hit every branch:
  - `canvas_*` — a 1600×2400 canvas of small text, the only page whose lines are short enough in
    input pixels: `long_side 256` → second pass at 544, `long_side 1024` → 1408 (both uncapped and
    a different jump each time);
  - `rot90_512` (median 9.38) and `rot90_1024` (median 14.55) — medians at or above
    `ADAPTIVE_MIN_H`, so no second pass, one of them only just;
  - `latin_256`, `two_columns_1024`, `cjk_1024`, `small_512` — 3-5 boxes, the eight-box gate;
  - `blank_256` — no boxes at all, the `if len(boxes)` guard;
  - `small_512` — two boxes clipped at the crop's border;
  - `canvas_1024_static` — `adaptive=False`, one pass.
  All of them run the real session in the C# test and the boxes come out bit-identical to the
  Python values (boxes0, scale0, median, second-pass long side and the final boxes).
- The **stand-in** cases need no model file and run everywhere: a deterministic model that paints
  `bars` horizontal bars, core logit 10 (sigmoid → above the threshold) and both distance channels
  at `dist`, so every box decodes exactly `2 * dist` tall and the median is a chosen constant.
  They pin the 2048 cap (`long_side 1024`, `dist 3`), the "candidate equals the first pass"
  boundary (`long_side 2048`), an uncapped 1536 (`dist 1`), the `dist` values 1/3/5/15 that decide
  firing, a 32-unaligned `long_side` (300), the < 8 box and 0 box gates, `adaptive=False`, and the
  clipping (`edge` spans the full output width, so all ten boxes cross the page). The C# stand-in
  is the same integer arithmetic as the generator's, so those comparisons are exact too.
- `Adaptive_long_side_gates_and_bounds` also checks the thresholds directly on synthetic boxes:
  7 boxes is never enough; an exact median of 9.0 does not fire; `long_side 1024` and a median of
  8.9 gives 1376 (`1024 * 12 / 8.9 = 1380.7` → 1380 → 43 × 32); a median below 1.0 saturates at
  the cap; 6.0 at `long_side 2048` is the boundary that no longer grows.

## Deviations

None. `detect` is arithmetic on the boxes `decode` produced (step `070`, bit-exact), so every case
is compared exactly.
