# 090 — orientation + deskew

**Status:** done.

## Goal

Port the page-uprighting half of `indra_ocr/pipeline.py`: the `orient.onnx` quarter-turn decision
(`orientation`), the tilt search (`estimate_skew`), the two rotations that apply them
(`rotate_page`, `np.rot90`) and `unrotate_box`, which maps a straightened box back onto the
turned page — plus `upright`, the function that chains them.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Pipeline/Orientation.cs` | `IOrientModel`, `Orientation` (`OrientInput`, `Turns`, `EstimateSkew`, `SkewThumbnail`, `Sharpness`, `Rotate90`, `RotatePage`, `UnrotateBox`, `Upright`, `RoundTenth`) |
| `src/Indra.Ocr.Core/Onnx/OnnxOrientModel.cs` | `IOrientModel` over an `InferenceSession` |
| `src/Indra.Ocr.Core/Numerics/NpyArange.cs` | `np.arange` for doubles |
| `src/Indra.Ocr.Core/Numerics/Numerics.cs` | `Sum32` / `Mean32` (numpy's float32 pairwise reduction) |
| `tools/scripts/test/gen_orient_fixtures.py` | → `fixtures/orient.json` |
| `src/Indra.Ocr.Tests/OrientationTests.cs` | the parity tests |

## Constants

`ORIENT_SIDE = 768`, `ORIENT_MIN = 0.95`, `DESKEW_MAX = 5.0`, `DESKEW_MIN = 0.3`,
`estimate_skew`'s thumbnail side `800`, and the blank-page cut `mean(ink) < 0.003`.

## `orient_input`

```
s = side / max(h, w)
small = resize(gray, (max(1, round(w*s)), max(1, round(h*s))), INTER_AREA)
out   = zeros((side, side), float32); out[:h', :w'] = 1 - small/255
```

The padding stays 0, which is paper in an ink map. `INTER_AREA` on an *upscale* is not the area
kernel at all — OpenCV documents it as "similar to `INTER_NEAREST`" and dispatches to the
nearest-neighbour path, which the port already does (step `040`); the fixture's `small`/`onepx`
cases run that path.

## `orientation`

```
p = orient.onnx(x = orient_input(gray)[None, None])[0][0]
k = argmax(p)                       # first maximum, like numpy
turns = k if p[k] > 0.95 else 0
```

`p` is float32, so `ORIENT_MIN` is cast to float32 before the comparison (numpy's NEP 50):
`0.95f` is `0.949999988079071`, and a probability of exactly `0.95f` therefore does **not** turn
the page. Both details are pinned in
`Turns_uses_the_first_maximum_and_the_float32_threshold`, and the fixture's `noise` case is a
real near-miss (`p[0] = 0.5016`: above 0.5, below the threshold).

The fixture runs the **real** `orient.onnx` through `OnnxOrientModel` and the probabilities come
out bit-identical to the Python reference's (`CPUExecutionProvider`, `intra_op_num_threads = 1`
on both sides). A stand-in model (the mean ink of each quadrant) covers the same logic when the
model folder is absent, and `turns` — the integer the pipeline acts on — is asserted exactly in
both tests.

## `estimate_skew`

```
ink = (255 - thumbnail)/255                       # float32
if mean(ink) < 0.003: return 0.0                  # a blank page has no rows to straighten
best = max(arange(-5, 5 + 1e-6, 0.5),  key=sharpness)
return max(arange(best-0.5, best+0.5+1e-6, 0.1), key=sharpness)

sharpness(a): r = warpAffine(ink, getRotationMatrix2D((w/2, h/2), a, 1.0), (w, h))
              p = r.sum(1); return ((p[1:] - p[:-1])**2).sum()      # all float32
```

Ported pieces:

- **`np.arange`** (`NpyArange`). numpy does *not* compute `start + i*step`: it fills
  `a[0] = start`, `a[1] = start + step` and then walks the array with `delta = a[1] - a[0]`, so
  an inexact step is rounded once and the rounding of the *subtraction* is inherited by every
  value. `arange(b - 0.5, b + 0.5 + 1e-6, 0.1)` therefore contains
  `-1.1102230246251565e-16` where `b == 0`, not `0.0` — and that is the angle the search returns
  for an upright page. This matters because the returned angle is one of the candidate *values*
  (the fixture compares it bit-exactly). Length is `ceil((stop - start)/step)`.
  Verified against numpy 2.5.4: 0 mismatches over both angle grids for every `b` on the 0.5
  grid, and over 4000 random triples.
- **The float32 sums** (`Numerics.Sum32`, `Mean32`). `r.sum(1)`, `(d**2).sum()` and `ink.mean()`
  are float32 reductions, i.e. order-dependent. The port uses numpy's own *pairwise* algorithm
  (`npy_pairwise_sum_FLOAT`: plain sum below 8, eight accumulators up to 128, then bisection) —
  numpy's portable implementation. See the deviation below.
- **`**2`** is exactly `x*x` for float32 in numpy (verified: `d**2 == d*d` over random data), so
  the port squares in place.
- **`getRotationMatrix2D`** rounds its centre to `Point2f` (step `040`); the centre is always a
  half-integer here.
- **`max(..., key=f)`** takes the *first* maximum; `BestAngle` reproduces that (strict `>`).

`warpAffine` here is the float32 `INTER_LINEAR` kernel with the default `BORDER_CONSTANT` 0,
already ported in step `040`. `Sharpness` is public so the fixture can check the whole profile
(it stores the 21 coarse and 11 fine candidates with their scores).

## `rotate_page` / `unrotate_box` / `rot90`

- `rotate_page(gray, degrees)` is `CvWarpAffine.Rotate8u`: `INTER_LINEAR` + `BORDER_REPLICATE`
  about the page centre, same size — bit-exact (the fixture covers ±0.3, 2.5, 15, 45, 90, 179.9,
  −37.5 degrees on five pages, including the replicate border at 45°).
- `unrotate_box` transforms the four corners of the box in **float64** (the reference does
  `M @ corners` with numpy's matmul), takes the bounding box, clips it to the page and rounds to
  one decimal with Python's `round` — correctly rounded to the nearest decimal with ties to even,
  which is .NET's `Math.Round(x, 1, MidpointRounding.ToEven)` (pinned against the Python values
  `0.05→0.1, 0.15→0.1, 1.25→1.2, 2.675→2.7, 1.005→1.0`). 480 boxes are compared exactly.
- `np.rot90(gray, k)` for `k ∈ {0,1,2,3}` is an index map, ported directly
  (`out[i][j] = m[j][W-1-i]` etc., `Rotate90`); `upright` applies it with `-k`.

## `upright`

```
k = orientation(gray) if turn else 0
if k: gray = rot90(gray, -k)              # only odd k swap the dimensions
a = estimate_skew(gray)
if |a| >= DESKEW_MIN: gray = rotate_page(gray, a)
else: a = 0.0
return gray, k, a
```

The port returns `byte[]?`: the caller's own buffer stands for "unchanged" (null), exactly the
case where the reference hands back the same array object. The `turn` flag is `model == null`.
`UnrotateBox` stays in `double` for the boxes — the pipeline (`__call__`) maps them back after
`assemble`, which is step `130`'s job.

## Deviations

- **numpy's SIMD float32 reduction.** For a *contiguous* float32 reduction numpy dispatches to a
  CPU-specific SIMD kernel (8 or 16 lanes, several accumulators, an unrolled loop), not to the
  pairwise function above. The two agree for `n <= 8` and coincide at 128 by construction; above
  that they differ in the last ulp (probes on this AVX2 machine: sizes 130, 200, 800, 640000 all
  differ). The tree is an implementation detail that changes with the CPU and the numpy build —
  the reference loses reproducibility there just as it does with `argsort` (see `AGENTS.md`) — so
  the port fixes the portable numpy algorithm instead and the *input* to the comparison is where
  it could show: `estimate_skew` ranks 32 candidate angles by their sharpness. The fixture
  asserts every candidate's sharpness to within `1e-5` relative (worst observed: **2.3e-7**) and
  asserts the angle the search picks — the value the pipeline prints and acts on — **exactly**,
  on 9 pages, including a random-noise page (mean ink 0.50) whose profile has no real peak.
  `ink.mean() < 0.003` is likewise a float32 reduction, so a page whose mean is within ~1e-7 of
  the cut could take the other branch; the fixture pins the decision on a blank page (mean
  exactly 0) and on 11 real ones.
- **`np.exp`-style kernel differences do not apply here.** `CvWarpAffine.WarpAffine32f` is
  scalar (step `040` noted that OpenCV's FMA vector path can differ in the last bit), which
  feeds the same sharpness tolerance.
- **The reference raises on an empty window.** Not applicable here: every input is a page, and
  `estimate_skew` handles `h == 1` (the difference array is empty, every angle scores 0.0 and the
  first candidate `-5.5` wins) and 1×1 pages through the same code. Both are fixture cases
  (`strip_100`, `onepx_64`).

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → **62 passed** (was 51; +11 `OrientationTests`), 14 s.
- `fixtures/orient.json` (802 KB): 14 pages (real `temp/inputs` pages, their 90/180/270-degree
  turns, a synthetic bar page with a known 3-degree tilt, a noise page and degenerate 3×40 /
  1×1 ones), then
  - 9 `orient_input` maps (768, 300, 128, 96, 64 — down- and upscales, a 1-row thumbnail);
  - 14 `turns` cases against the real `orient.onnx` (3 of them actually turning the page) + 14
    stand-in cases;
  - 12 `estimate_skew` cases: the returned angle, the mean, the blank decision, the thumbnail
    dimensions, the thumbnail ink map (stored for the six that fit) and the full sharpness
    profile of both grids;
  - 16 `rotate_page` cases bit-exact;
  - 480 `unrotate_box` boxes (random + off-page + degenerate + both corner orders);
  - 20 `round` cases for Python's `round(x, 1)` (ties-to-even on the decimal, both signs, `-0.0`);
  - 14 `upright` cases (turn × deskew, with the model, the stand-in and `turn = False`),
    comparing the undone turns, the skew, the resulting dimensions and the page pixels.
- Also covered: `Rotate90` against numpy's index maps for all four turns (and the round trip).
