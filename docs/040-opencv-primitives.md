# 040 — OpenCV primitives (resize, warpAffine, connected components)

**Status:** done.

## Goal

Reproduce the four OpenCV operations the OCR pipeline is built on, in pure managed C# and
**bit-exactly** for the shapes the pipeline uses:

| pipeline call site | operation |
|---|---|
| `det_post.page_to_input`, `pipeline.orient_input`, `pipeline.estimate_skew`, `rec_preproc`, `line_mask.resize_window` | `cv2.resize` (8-bit, `INTER_AREA` / `INTER_LINEAR`) |
| `pipeline.rotate_page` (8-bit pages), `pipeline.estimate_skew` (float32 ink map) | `cv2.warpAffine` (`INTER_LINEAR`) |
| `pipeline.estimate_skew`, `pipeline.rotate_page` | `cv2.getRotationMatrix2D` |
| `det_post.decode` | `cv2.connectedComponentsWithStats(connectivity=4)` |

The strategy was to model each algorithm in Python first, validate the model bit-exactly
against `cv2`, and only then port it. The validated models live in `temp/` (`ref_resize.py`,
`exp_warp6.py`, `exp_ccl.py`); the OpenCV 5.x sources the models were derived from are in
`temp/opencv/`.

## Files

- `src/Indra.Ocr.Core/Imaging/CvMath.cs` — `cvRound`/`cvFloor`/`cvCeil`, `saturate_cast`,
  `DBL_EPSILON`, the resize weight constant, and the SIMD dispatch description
  (`WarpLanes` / `WarpUnroll` / `WarpUsesFma`).
- `src/Indra.Ocr.Core/Imaging/CvResize.cs` — `Resize8u`, `Resize32f`.
- `src/Indra.Ocr.Core/Imaging/CvWarpAffine.cs` — `RotationMatrix2D`, `InvertAffine`,
  `WarpAffine8u`, `WarpAffine32f`, `CvBorder.Interpolate`.
- `src/Indra.Ocr.Core/Imaging/CvConnectedComponents.cs` — `LabelComponents4`,
  `ComputeStats`.
- `tools/scripts/test/gen_cv_fixtures.py` → `fixtures/cv_{resize,warp,rotation,ccl}.json`.
- `src/Indra.Ocr.Tests/CvPrimitivesTests.cs`, `src/Indra.Ocr.Tests/B64.cs`.

`InputArray`/`OutputArray` are gone: images arrive as the borrowed `GrayImage` view and every
result goes into a caller-provided `Span<byte>`/`Span<float>`. `dstStride` is exposed because
the detector resizes into a white-padded square canvas.

## Rounding primitives

- `cvRound` is **round-half-to-even** (the x86 default rounding mode), *not*
  `Math.Round`'s default of away-from-zero, and `cvFloor` is the mathematical floor.
- `saturate_cast<uchar>(float)` = `cvRound` then clamp to `[0, 255]`;
  `saturate_cast<short>(float)` clamps to `[-32768, 32767]`.
- `DBL_EPSILON` is `2.220446049250313e-16`. (`double.Epsilon` is the smallest *subnormal* —
  using it here would silently break the integer-scale detection below.)

## resize

The dispatch is OpenCV's, in this order:

1. `inv_scale = dst/src`, `scale = 1/inv_scale` (the reciprocal, **not** `src/dst`);
2. `iscale = cvRound(scale)`; `is_area_fast = |scale - iscale| < DBL_EPSILON` per axis;
3. `INTER_LINEAR && is_area_fast && iscale == 2` is promoted to `INTER_AREA` (at exactly 2x the
   two agree, and the area kernel is cheaper);
4. `INTER_AREA && scale_x >= 1 && scale_y >= 1` takes an area kernel, everything else the
   generic bilinear one.

**Area fast** (integer scale) sums each `iscaleX * iscaleY` block. A 2x2 block is rounded with
`(s00 + s01 + s10 + s11 + 2) >> 2`; other factors use `cvRound(sum * (1.f/area))`. Right-edge
blocks that fall off the source average only the samples that exist. Rows past the last source
row stay zero (the port zeroes them explicitly — the caller's buffer is not assumed clean).

**Area general** (fractional scale) is `computeResizeAreaTab` + `ResizeArea_Invoker`: each
output row takes the `beta`-weighted sum of the source rows it overlaps, and each output column
the `alpha`-weighted sum of source columns. Both accumulate in **float32** and the row sum is
built as `sum[dx] = beta*buf[dx]` then `sum[dx] += beta*buf[dx]` — the port starts from a zeroed
accumulator, which is exact because `0 + x == x`. The store rounds with `cvRound`.

**Generic bilinear** (8-bit) uses `INTER_RESIZE_COEF_BITS = 11` fixed-point weights:
`fx = (float)((dx+0.5)*scale - 0.5)`, `sx = cvFloor(fx)`, `fx -= sx`, then
`alpha[k] = saturate_cast<short>(cbuf[k]*2048)`. Columns outside `[0, width-1]` are clamped
(`sx < 0 → fx = 0, sx = 0`; `sx >= width-1 → fx = 0, sx = width-1`), rows are clamped to
`[0, height-1]`, and the result is

```
((b0 * (S0 >> 4)) >> 16) + ((b1 * (S1 >> 4)) >> 16) + 2) >> 2
```

with `S = S[sx]*a0 + S[sx+1]*a1` in `int`. `area_mode` (an `INTER_AREA` call that scales *up*)
uses a different coefficient setup: `sx = cvFloor(dx*scale)`,
`fx = (dx+1) - (sx+1)*inv_scale`, `fx = fx <= 0 ? 0 : fx - cvFloor(fx)`.

## warpAffine

`cv::warpAffine` takes the **forward** matrix and inverts it, in float64:

```
d = M0*M4 - M1*M3;  d = d != 0 ? 1/d : 0
A11 = M4*d, A22 = M0*d;  M1 *= -d, M3 *= -d;  M0 = A11, M4 = A22
b1 = -M0*M2 - M1*M5;  b2 = -M3*M2 - M4*M5
```

The kernel then narrows the six doubles to float and maps every output pixel back:

- **vectorized block** (`x < uf * floor(width/uf)`): `M_x = fl32(y*M1 + M2)`,
  `sx = fma(M0, x, M_x)`;
- **scalar tail**: `sx = ((x*M0) + (y*M1)) + M2`.

`uf = vlanes_32 * 2`, so on AVX2 it is **16** (`Vector<float>.Count = 8`); an AVX-512 machine
would use 32 and legitimately produce different last bits. Both paths then do
`ix = cvFloor(sx)`, `fx = sx - ix`, fetch the four neighbours (direct when
`(unsigned)ix < width-1 && (unsigned)iy < height-1`, otherwise through
`borderInterpolate`, with the `BORDER_CONSTANT` early-out when
`(unsigned)(ix+1) >= width+1 || (unsigned)(iy+1) >= height+1`), and interpolate:

- vectorized: `v0 = fma(fx, p01-p00, p00)`, `v1 = fma(fx, p11-p10, p10)`,
  `res = fma(fy, v1-v0, v0)`;
- scalar: `v0 = p00 + fx*(p01-p00)` etc.

8-bit stores `saturate_cast<uchar>(res)`; float32 stores the value unchanged. **The FMA/scalar
split is not cosmetic** — the two round differently, and modelling it was what took the float32
warp from ~18% mismatching pixels to zero.

`BORDER_REPLICATE`, `BORDER_REFLECT`, `BORDER_REFLECT_101`, `BORDER_WRAP` and
`BORDER_CONSTANT` are implemented (`borderInterpolate`); `BORDER_TRANSPARENT` is rejected
because it needs a pre-initialised destination, which nothing here does.

## getRotationMatrix2D

```csharp
angle = angleDegrees * (Math.PI / 180.0);
alpha = Math.Cos(angle) * scale;
beta  = Math.Sin(angle) * scale;
m = [alpha, beta,  (1-alpha)*cx - beta*cy,
     -beta, alpha, beta*cx + (1-alpha)*cy];
```

The centre is a `Point2f` in OpenCV, so the API takes `float` centres to make the narrowing
explicit (the pipeline passes half-integers, which are exact in float32). On this machine
`Math.Cos`/`Math.Sin` agree bit-for-bit with the reference's `cos`/`sin` over all 651 fixture cases,
including the exact `np.arange(base-0.5, base+0.5+1e-6, 0.1)` doubles the skew search probes — but
the *library* behind them is the host's, not the port's: the reference's own C++ calls the same one,
so the two always move together, while glibc and the Windows CRT round `cos(π/4)` one ulp apart. The
fixture was captured on Windows, so `Rotation_matrix_matches_cv2` compares the six values within the
relative ulps that last bit is worth, and nothing wider (`docs/380-test-hosts.md`).

## connectedComponentsWithStats (4-connectivity)

Classic two-pass union-find over non-zero pixels (`left`/`up` neighbours only), then a second
pass that renumbers the roots into **raster order of first appearance**. Two properties of the
OpenCV contract are load-bearing and reproduced:

- the returned count **includes the background**, which is label 0, so foreground labels are
  `1 .. count-1`;
- the label *numbering* is observable: `det_post.decode` iterates
  `for i in range(1, n)` over the stats rows, so which component is label 1 decides the order of
  the emitted boxes.

`ComputeStats` fills `left, top, width, height, area` in OpenCV's `CC_STAT_*` order.

### Known deviation — the background stats row

OpenCV leaves **scratch values** in stats row 0 (the algorithm uses that row as working
memory): a mask with background yields `[0, 0, width, height, background_area]`, an
all-foreground mask yields `[-1, 2147483647, 0, 0, 0]`. Nothing reads it — the decoder only
reads labels `1 .. count-1` — so the port leaves row 0 zeroed instead of reproducing an
algorithm-specific scratch layout. This is the same class of deviation as the `np.argsort`
tie order recorded in `AGENTS.md`.

### Known deviation — float32 resize

OpenCV's float32 vertical resize pass contracts to an FMA chain (`v_muladd`) in its vectorized
columns, so `Resize32f` (scalar formula) differs from `cv2.resize` by up to ~5e-7 on values of
order 1 — within the project's sub-1e-3 float tolerance, and 2048 "ulps" near zero purely
because of cancellation. This path is only reached through `line_mask.mask_windows`, i.e. with
`mask.onnx`, which the shipped model set does not include; it has no effect on the shipped
JSON/markdown output.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → 25 passed.
- `CvPrimitivesTests` asserts bit-parity against fixtures generated by
  `tools/scripts/test/gen_cv_fixtures.py` (opencv-python-headless 5.0.0):
  - 651 `getRotationMatrix2D` cases (including the dense 0.1-degree skew grid);
  - 10 8-bit resize cases covering the 2x2 area kernel, the non-2x2 area-fast kernel, the
    fractional area tables, a 2x linear promotion, upscales and fractional downscales;
  - 10 8-bit + 4 float32 warp cases over both border modes, widths that are and are not
    multiples of the 16-column unroll (53, 64, 129, 200);
  - 14 connected-component masks (random densities, text-like strokes, a diagonal, degenerate
    1-pixel axes) comparing the count, every label and every stat.
- A throwaway fuzz run (80 random resizes, 40 random 8-bit warps, 20 random float32 warps, 30
  random masks) was bit-exact for every case, then deleted.
