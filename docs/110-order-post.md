# 110 — order_post

**Status:** done.

## Goal

Port `indra_ocr.order_post` — the torch-free half of the reading-order stage: the line features
that feed the order model, the pairwise-order decoder, the two geometry baselines, the
average-linkage block clustering and the layout-head block decoder — plus the float32 `np.log`
kernel `line_features` needs. The model seam (`IOrderModel`/`OnnxOrderModel`), the `reading_order`
driver and the table heads are step `115`.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Order/OrderPost.cs` | `LineFeatures`, `DecodeOrder`, `OrderTopDown`, `OrderXyCut`, `Cluster`, `DecodeBlocksOrder`, `BlockClasses`, `GridClassIds` |
| `src/Indra.Ocr.Core/Numerics/NpyLog.cs` | numpy's vectorised float32 `log` (`NpyLog.Log`), bit-exact |
| `src/Indra.Ocr.Core/Text/UnicodeText.cs` | `IsDigit`/`IsDigitCodePoint`, `IsUpperCodePoint`, `IsSpace`/`IsSpaceCodePoint`, `CodePointValues` |
| `src/Indra.Ocr.Core/Numerics/Numerics.cs` | `Log1p` branch fix (see Deviations) |
| `tools/scripts/test/gen_order_fixtures.py` | → `fixtures/order_post.json`, `fixtures/unicode_predicates.json` |
| `src/Indra.Ocr.Tests/OrderPostTests.cs` | the parity tests |

## Scope

Ported: `line_features`, `decode_order`, `order_top_down`, `order_xycut`, `_cluster`,
`decode_blocks_order`, and the two pieces of module data `decode_blocks_order` reads
(`BLOCK_CLASSES` and `GRID_LABELS`, the latter as `{5, 9, 8}`).

Not ported, like `det_post.encode_targets` before them — they are training/eval-only and no
pipeline path calls them: `kendall_tau`, `successor_acc`, `block_class`, `label_order`.

## `line_features`

`[N, 4]` boxes and the page `(W, H)` to `[N, 16]` float32: 10 geometry columns then 6 text
columns. The float32 bookkeeping is the whole difficulty:

- `w = max(x1 - x0, 1e-3)`, `h = max(y1 - y0, 1e-3)` in **float32** (numpy's weak 1e-3 scalar),
  and `med_h = np.median(h)` keeps numpy's float32 reduction (the two middle values averaged in
  float32).
- Columns 0–6 are float32 array math against a Python scalar, i.e. NEP 50 weak scalars — every
  division and the `(x0 + x1) / (2W)` grouping stay in float32. Column 9 is different:
  `med_h / H * 20.0` is computed on **Python floats** (`float(np.median(...))` widens first) and
  only then stored into the float32 `np.full`; the port computes it in double and casts.
- Columns 7–8 are the only `np.log` calls: `np.log(h / med_h)` and `np.log(w / h) / 4.0` — numpy's
  own float32 kernel, not `logf` (see `NpyLog` below).
- The text block is `math.log1p(len) / 5.0` and `digits / len` in **double** (Python ints/`len`),
  then stored into the float32 block; the port keeps the double and casts once.
- The markers are exactly `t[0] in "•-–▪◦*·→"` (the first *code point*, and a code point not a
  `char`: the list is all BMP, but a surrogate-pair first character must still not match) and
  `t[:3].rstrip(".)").isdigit() and len(t) > 3`, `t.endswith("-")`, `t[-1] in ".!?:;"`,
  `t[0].isupper()`. `t[:3]` slices by code point, `len(t)` counts code points, `rstrip(".)")`
  is `TrimEnd('.', ')')`, and the two string predicates come from `UnicodeText`.
- `texts` may be shorter than the box list (the reference's `enumerate` then leaves the remaining
  rows zero) or `None` (whole text block zero), and a text that strips to `""` keeps its zeros.

## `decode_order`

Start from `argsort(-lp.sum(1) + lp.sum(0))`, then up to 4 sweeps of insertion moves, stopping
early when a sweep changes nothing.

- `lp = -np.logaddexp(0.0, -a)` is the **float32** `logaddexp` loop, i.e. the CRT's `expf` and
  `log1pf`, *not* the vectorised `np.exp`/`np.log` kernels. `np.logaddexp` special-cases `x == y`
  (here `a == 0`), so that lane is `-(0 + ln 2f)` exactly. `MathF.Exp` is bit-identical to this
  machine's `expf` (probe: 0/60000); the CRT's `log1pf` is *not* correctly rounded, which is the
  one place `lp` can differ from the reference (see Deviations).
- `lp.sum(1)` is a contiguous reduce → numpy's float32 pairwise sum (`Numerics.Sum32`);
  `lp.sum(0)` is a strided reduce → numpy accumulates each column **sequentially** (probe: 0
  differences up to n=200). The port does exactly those two.
- `np.argsort` is stable in the port (documented deviation, `AGENTS.md`): the keys are sums of
  distinct logits, so ties are not expected but are resolved by index rather than by numpy's SIMD
  sort.
- The gains are `concat([0.0], cumsum(before)) + concat(cumsum(after[::-1])[::-1], [0.0])`: the
  `cumsum`s are float32, the concatenation with the Python `0.0` promotes to **float64**, and
  `argmax` runs on float64. A probe over float32 inputs of length 1…1000 (300 trials each) found
  numpy's `cumsum` bit-identical to a plain sequential accumulation, so the port accumulates in
  float32 and adds into doubles (`np.insert` afterwards — the port rebuilds the order array).
- `argmax` is the first maximum (`>`), and a move happens only if
  `gains[k] > gains[cur] + 1e-6`.

## Geometry baselines

- `order_top_down`: the key is `(round(cy / max(median_h * 0.5, 1e-3)), x0)`. `median_h * 0.5` is
  float32 (×0.5 is exact), the division is float32, and Python's `round` is round-half-to-even on
  the exact value — the port rounds the (widened) float32 quotient with
  `MidpointRounding.ToEven`. `sorted` is stable, so the port adds the index as a final tiebreaker.
- `order_xycut`: recursive; at each step `best_cut` sorts the starts (`np.argsort` — a tie here is
  a real (if unlikely) exposure of the stable-sort deviation), walks the running `end`, and takes
  the widest `lo - end` gap over both axes, preferring the y axis on `gy >= gx`. `best_gap` starts
  as the Python float `0.0` and the gaps are float32; `end = max(end, hi)` is float32. With no gap
  on either axis it falls back to `order_top_down` of the subset.

## `_cluster` and `decode_blocks_order`

- `_cluster` is average linkage on the float64 copy of the "same block" logits: repeatedly merge
  the pair with the highest mean logit while that mean is `> 0` (`best_v[a] <= 0` stops). All of
  it is float64 with the reference's own operation order — `S[a] += S[b]` then `S[:, a] += S[:, b]`,
  the mean `S[a] / (size[a] * size)`, `-inf` on the diagonal and on dead groups, and the two
  incremental `best`/`best_v` updates (rows whose best partner was `a` or `b` are recomputed, then
  any row `row > best_v` upgrades) — so the result is bit-identical, not merely close. `argmax`
  over `best_v` and over each row is the first maximum. Returned member lists are sorted, and the
  blocks come out in ascending surviving-index order.
- `decode_blocks_order`: blocks first, then inside each block `order_top_down` when the block's
  summed class logits peak on a grid label (`{5, 9, 8}`, i.e. Table / Table-Of-Contents / Form)
  and `order_xycut` otherwise (`cls[b].sum(0)` is the strided float32 reduce again), then the
  blocks themselves by `decode_order` over the block-to-block mean logit.
- That mean matrix is `G a G^T` with `G` the membership matrix scaled by `1 / block size`, and the
  reference takes `np.triu(..., 1)` **before** antisymmetrising: `ab = triu(M, 1) - triu(M, 1).T`,
  i.e. `ab[k, l] = M[k, l]` for `k < l` and `-M[l, k]` for `k > l`. The naive `M - M.T` is *not*
  the same matrix (the fixture's two-column layout caught exactly this), so the port builds the
  triangle. The port computes `M` as two passes (row sums, then column sums) instead of one BLAS
  product; both stay in float64 and round to float32 once, and the fixture's portable replay shows
  the difference never changes the decoded order.
- `cls` is documented as `[n, 10]` and is required: passing a shorter block would read past it, so
  the port throws. (A caller without layout heads calls `decode_order`, as the reference does.)

## `NpyLog` — numpy's float32 `log`

`np.log` on a contiguous float32 array never reaches the C library: numpy's
`loops_exponent_log.dispatch` runs its own vectorised log (an SVML port) for *every* element,
including one-element arrays and the masked tail. On this AVX2+FMA3 machine it differs from the
CRT's `logf` in ~4% of inputs by 1 ulp, and the features it computes feed the order model, so the
last ulp is observable. `NpyLog` reproduces `simd_log_FLOAT` (`fma_get_exponent`/`fma_get_mantissa`,
the 2^100 denormal pre-scale, the `f <= sqrt(1/2)` mantissa doubling, the P/Q polynomials with
hardware FMA) and its edge outputs: `-inf` for either zero, `-NPY_NANF` (`0xFFC00000`) for
anything negative *including* `-inf`, `NPY_NANF` (`0x7FC00000`) for NaN of either sign, `+inf` for
`+inf`. Validated bit-exactly against numpy over 121 012 values spanning denormals, negatives,
zeroes, infinities, NaNs and all lengths 1…100 (masked tail included).

## Unicode predicates

`line_features` asks only three Unicode questions, and all three are answered for **all** code
points against Python:

- `isdigit`: `Nd` plus the `Numeric_Type=Digit`-but-not-`Nd` code points (superscripts, circled
  numbers, …), 888 code points in 90 ranges (Unicode 16.0.0).
- `isupper` on a single code point: `Lu` plus the five `Other_Uppercase` ranges (Roman numerals,
  circled/squared Latin letters), 1978 code points in 656 ranges. Only the one-code-point form is
  needed (`t[0].isupper()`); the multi-character `str.isupper` would additionally have to count
  `Other_Lowercase` code points as cased, so no approximate version is offered.
- `isspace`: the `Zs`/`Zl`/`Zp` separators plus `\t`–`\r`, `0x1C`–`0x1F` and `0x85`, 29 code points
  in 10 ranges. **`char.IsWhiteSpace` is not Python's set** — it misses `0x1C`–`0x1F` — so
  `IsSpaceCodePoint` adds them. This also fixes a latent gap in step `050`'s `IsSpace`, which the
  vocabulary's `t.isspace()` check uses.

`CodePointValues` (code point values, an unpaired surrogate yielding its own value like Python's
`ord`) backs the three string forms: `char.ConvertToUtf32` throws on a lone surrogate, and Python
does not.

## Verification

- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **79 passed** (was 70; +9 `OrderPostTests`), 15 s.
- `fixtures/order_post.json` (459 KB) holds 20 feature cases, 27 `decode_order`, 15 `top_down`,
  14 `xy_cut`, 15 `cluster` and 21 `decode_blocks_order` cases, plus a 2008-value `np.log` corpus.
  The real cases use the boxes and texts recorded in `recognize.json` (real detector + real
  recognizer output, so the test needs no image inputs) and the `a`/`cls`/`same` logits the real
  `order.onnx` produced for them (so it needs no model file either); the synthetic cases cover
  single/empty boxes, zero-size and whole-page boxes, off-page boxes, exact ties, overlapping boxes
  with no gap, three-column and grid layouts, every text-feature marker (bullets, `–`/`▪`/`◦`/`·`/
  `→`, numbered and superscript digits, astral upper-case, whitespace-only and empty strings), and
  random logit matrices at n = 1…128 plus antisymmetric ones.
- The generator replays every `decode_order`/`decode_blocks_order` case with a *portable*
  simulation of the port (CRT `expf` via ctypes + correctly rounded `log1p` + numpy's portable
  pairwise sum + a stable argsort) and records the result in the fixture: **0 of 27** and **0 of 21**
  cases differ. That is the evidence that the documented deviations below are not reachable on this
  fixture.
- `fixtures/unicode_predicates.json` pins the three code point sets; `Unicode_predicates_match_python`
  checks `IsDigitCodePoint`, `IsUpperCodePoint`, `IsSpaceCodePoint`, `IsDigit`, `IsSpace` and
  `CodePointCount` for all 1 114 112 code points.

## Deviations

- **`np.argsort` tie order** (the standing deviation, `AGENTS.md`): `DecodeOrder`'s initial ranking
  and `OrderXyCut`'s `best_cut` use a stable sort. The keys are float32 sums of distinct logits, so
  a tie needs an exact coincidence; numpy's SIMD sort would resolve it by an implementation detail.
- **`lp`'s last ulp.** `np.logaddexp`'s float32 path calls the CRT's `log1pf`, which is not
  correctly rounded (probe: 6 773 of 60 000 arguments differ from the correctly rounded value by
  1 ulp, ~11%). The port uses a correctly rounded `log1pf` (`(float)Numerics.Log1p(x)`, validated
  0/100 006 against the CRT's double `log1p`). A probe measured how much that can matter: over
  random `n × n` float32 logit matrices the decoded order differed in **1 of 480** matrices, all at
  n = 300 (antisymmetric matrices, as `decode_blocks_order` builds them: 0 of 150); at n = 300,
  perturbing only `lp` gave 1 of 25 different orders while perturbing only the sums gave 0 of 25.
  A different order only becomes a visible difference when it flips a `> 1e-6` gain comparison. The
  fixture's own 27 `decode_order` and 21 block cases (n ≤ 128, i.e. the whole real pipeline's range)
  agree exactly.
- **Float32 sums above n = 128 are exact too.** `Numerics.Sum32` is numpy's `pairwise_sum_FLOAT`,
  whose recursion halves the array with the split rounded down to a multiple of 8 (step `155`
  restored that rounding; see `docs/090-orientation-deskew.md`), so `lp.sum(1)`/`cls[b].sum(0)`
  match numpy bit for bit. The probe above already put the effect on the decoded order at 0 of 25
  at n = 300; that is now a property of the port rather than a coincidence.
- **`Numerics.Log1p` was wrong** before this step: its two return branches were swapped, so the
  bit-loss correction was applied when there were no lost bits (and skipped when there were),
  giving up to 5e-4 relative error for tiny `x`. Fixed; `(float)Numerics.Log1p((double)x)` is now
  bit-identical to the CRT's `log1p` on 100 006 values. `LogAddExp` (which uses it) is unchanged in
  behaviour for its own inputs.
- `ArgMax`/`ArgSort` do not special-case NaN the way numpy's `argmax` does (a NaN would win there).
  The order and class logits are model outputs and finite, so the branches are unreachable; the
  fixture's cluster cases are all finite.
