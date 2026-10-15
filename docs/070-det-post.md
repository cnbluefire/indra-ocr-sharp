# 070 — `det_post`: LineNet preprocessing and decoding

**Status:** done.

## Goal

Port `indra_ocr/det_post.py`: `page_to_input` (uint8 page → float32 ink map at the network's
input size) and `decode` (raw LineNet output → line boxes in input pixels). Both are pure
numpy/OpenCV in the reference and contain no Torch, which is why they live outside
`det_model.py`.

**Scope.** Inference only. `encode_targets` (line 48) builds *training* targets — it is called
from the training entry point that the repo does not ship, and never from `pipeline.py`,
`document.py`, the CLI or the viewer. It is intentionally **not** ported; nothing in the OCR
path can reach it.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Det/DetPost.cs` | `PageToInput`, `Decode` |
| `src/Indra.Ocr.Core/Numerics/NpyExp.cs` | numpy's float32 `exp` + `Sigmoid` (see below) |
| `tools/scripts/test/gen_det_fixtures.py` | → `fixtures/det_post.json` |
| `tools/scripts/test/gen_numerics.py` | → `fixtures/numerics.json`, section `exp_f32` |
| `src/Indra.Ocr.Tests/DetPostTests.cs` | page/ink and decoder parity |
| `src/Indra.Ocr.Tests/NumericsTests.cs` | `Npy_exp_matches_numpy_float32`, `Npy_sigmoid_matches_numpy_float32` |

## Constants

`CORE_FRAC = 0.4`, `CORE_X_SHRINK = 0.15`, `OUT_STRIDE = 2`, `INPUT_MULTIPLE = 32`. Only
`CORE_X_SHRINK`, `OUT_STRIDE` and `INPUT_MULTIPLE` are used at inference time; `CORE_FRAC`
belongs to `encode_targets` and is kept for reference.

## `page_to_input`

```
scale = long_side / max(h, w)                       # a Python true division -> double
nh, nw = max(1, round(h*scale)), max(1, round(w*scale))
if (nh, nw) != (h, w): resize  (INTER_AREA if scale < 1 else INTER_LINEAR)
ph, pw = ceil(nh/32)*32, ceil(nw/32)*32
out = zeros((ph, pw), f32); out[:nh,:nw] = 1 - gray.astype(f32)/255
```

Every step matters for bit-parity:

- `round()` is Python's **ties-to-even** on the *double* `h*scale` → `Math.Round(x,
  MidpointRounding.ToEven)`. `synth_5x8_ls4` (2.5 → 2) and `synth_3x6_ls1` (0.5 → 0 → clamped
  to 1) pin it.
- The resize is skipped when the rounded size already equals the source size. `synth_64x64_ls64`
  and `synth_17x64_ls64` cover the no-op path.
- The ink map is computed **in float32**: numpy 2's NEP 50 makes the Python float `255.0` a
  weak scalar, so `gray.astype(f32)/255.0` is a float32 division. The fixture's
  `ink_all_bytes_1x256` case runs a page containing all 256 byte values through
  `page_to_input` and stores the resulting floats, so a float64 intermediate (which would
  double-round) cannot slip through. C#: `1f - b / 255f`.
- The padding (right/bottom to `INPUT_MULTIPLE`) stays `0` = white, so the network sees a white
  margin. The fixture asserts the padding is exactly zero.

## `decode`

```
prob  = 1/(1 + exp(-out[0]))                # f32, numpy's exp (see below)
mask  = (prob > thresh).astype(u8)          # strict
n, lab, stats = connectedComponentsWithStats(mask, connectivity=4)
ys    = (row_index + 0.5) * 2               # pixel centres, f32
top   = ys - out[1]; bot = ys + out[2]      # f32
y0    = bincount(lab, top)   / maximum(count, 1)     # f64 / f32 -> f64
y1    = bincount(lab, bot)   / maximum(count, 1)
score = bincount(lab, prob)  / maximum(count, 1)
lh    = max(1.0, y1 - y0); ex = min(0.15*lh, 0.5*lh)
x0    = left*2 - ex; x1 = (left + width)*2 + ex
box   = (x0 - grow_x*lh, y0 - grow_y*lh, x1 + grow_x*lh, y1 + grow_y*lh, score)  # f32
```

Numeric notes, in the order the port reproduces them:

- The strict `>` in the mask: `sigmoid(0)` is exactly `0.5` and is **not** ink
  (`manual_threshold_exact`).
- `ys` is exact (`2y+1` for every realistic `y`), and `top`/`bot` are single float32
  subtractions/additions.
- `np.bincount` with float32 weights accumulates **in double**, sequentially in raster order.
  The port does the same (`sumTop[lab] += ys - out1[i]`), which is why the means are `double[]`.
- `cnt` is `float32` (`bincount(...).astype(np.float32)`) and `np.maximum(cnt, 1)` stays
  float32; the division is then `f64 / f32 → f64`. The port divides `double` by
  `(double)MathF.Max(cnt,1f)`.
- `lh`, `ex` and the box coordinates are all computed in **double**, and only the final tuple is
  cast to float32 when numpy builds the `[N,5]` array (`np.asarray(boxes, np.float32)`).
  Rounding early would change the last ulp.
- `stats` come from the ported `cv::connectedComponentsWithStats` (step `040`), whose label
  order and box semantics were already pinned there; only labels `1..n-1` are visited, so the
  undefined stats row 0 is never read.
- Empty result: Python returns `zeros((0,5), f32)`, the port an empty array (5 columns are
  implied by the caller's stride).
- `grow_x` / `grow_y` / `thresh` / `min_area` are part of the API even though `pipeline.py`
  only ever calls `decode(out)`; the fixture covers them explicitly rather than only through the
  defaults.

## numpy's float32 `exp`

The decoder's sigmoid is `1/(1+np.exp(-x))`, so `np.exp` sits directly on the box outputs. It
had to be ported exactly, because **`np.exp` on float32 is not the CRT's `expf`** and not
`(float)exp((double)x)`: numpy implements its own vectorised kernel (a port of the Intel SVML
one) in `numpy/_core/src/umath/loops_exponent_log.dispatch.c.src` and — for the AVX2+FMA3 build
this machine dispatches (`np.show_runtime()`: baseline `X86_V2`, found `X86_V3`, no AVX512) —
uses it for *every* element, single scalars included. About **38.5 %** of all inputs differ from
`expf` by 1 ulp.

`NpyExp.Exp` reproduces the kernel:

1. mask NaN / `>= XMAX` (88.72283935546875) / `<= XMIN` (-103.97208404541015625) to `0` and run
   the maths anyway;
2. `quadrant = rint(x * LOG2E)` via the magic number `12582912f` (`0x1.8p23`);
3. Cody-Waite reduction: `v = fma(q, C1, v); v = fma(q, C2, v)` (`C1`, `C2` = the two halves of
   `ln 2`);
4. degree-5/2 rational approximation `P0..P5` / `Q0..Q2`, every step a **fused** multiply-add;
5. `poly = num/den`, then `2^q` by adding `q << 23` to the raw bits, with the denormal branch
   for `q <= -125` (`q = max(q,-125)`, divide by `2^-(q+125)` as an integer shift, which AVX2
   turns into 0 for shifts ≥ 32);
6. NaN → `0x7FC00000` (numpy's *positive* quiet NaN, not .NET's `float.NaN`), `>= XMAX` → `+inf`,
   `<= XMIN` → `0.0f`.

**The subtlety: compiler contraction of the quadrant.** The vector code is written as
`q = x * log2e; q = (q + cvt_magic) - cvt_magic`, i.e. a *rounded* float32 product is what the
magic number rounds. numpy's build contracts the multiply into the add, so the unrounded product
goes into the FMA: `q = fma(x, log2e, 12582912) - 12582912`. The two forms differ only when
`float32(x*log2e)` is an exact half-integer, and there they choose different quadrants and hence
different mantissas. It is not a hypothetical: a dedicated corpus of every `x` whose float32
product is exactly `k+0.5` (266 values, one per `k` in `[-150, 131]`) shows **80 mismatches**
with the split form and **0** with the contracted one. `MathF.FusedMultiplyAdd` is a genuine
hardware FMA on this CPU (`Fma.IsSupported`, and `(1+2⁻²³)(1-2⁻²⁴)-1` lands on `2⁻²⁴ - 2⁻⁴⁷`),
so the C# expression keeps the contraction.

Verification beyond the committed corpus: 4.4 M random/edge pairs (`exp_pairs.bin`) and a 9.6 M
pair corpus mixing exact-tie values, bit-random patterns, `linspace` sweeps of both branches,
both range boundaries and near-denormal inputs — **0 mismatches** in all of them. The committed
`numerics.json` `exp_f32` section keeps 5912 of those values (all 266 ties, 19 specials, three
random batches and two sweeps) as a permanent regression test.

`NpyExp.Sigmoid(x) = 1f / (1f + Exp(-x))` is the pipeline's expression, float32 throughout, and
its expected bit patterns are pinned separately in `Npy_sigmoid_matches_numpy_float32`.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → **44 passed** (was 38; +4 `DetPostTests`, +2 in
  `NumericsTests`).
- `fixtures/det_post.json` (generated by `gen_det_fixtures.py`, which imports the real
  `indra_ocr.det_post` and runs the real `det.onnx`):
  - 15 pages — synthetic text-like/ramp/noise/white pages plus crops of
    `temp/inputs/{latin,cjk,two_columns}.png` — with the expected `ph`/`pw`/`scale`, the
    resized u8 page, the full float32 ink map for the small ones, and the decoder's boxes for
    the model's actual logits;
  - 6 hand-built logit tensors for the branches a page rarely hits: nothing above the
    threshold, exactly-at-threshold, areas below `min_area`, 4-connectivity of a diagonal,
    non-default `thresh`/`grow_x`/`grow_y`, and distances wide enough to push a box past the
    page edge (no clamping in `decode`).
  - 48 boxes total, every value bit-identical. A machine without `resources/models` still runs
    all of it: the fixture stores the logits, not the model.
- `fixtures/numerics.json` grew from 21 KB to 84 KB (the `exp_f32` section).

## Deviations

- `encode_targets` is not ported (training-only — see Scope above).
- None otherwise: `page_to_input` and `decode` are bit-exact against the reference on all
  fixture cases, including the model's own logits.
