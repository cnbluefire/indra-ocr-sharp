# 155 — OrtOCR (the whole-page driver)

**Status:** done.

## Goal

Port `indra_ocr/pipeline.py`'s `OrtOCR` class: the object that owns a model folder and runs a
whole page through the pipeline — turn it upright (`orient.onnx` + the deskew search), detect its
visual lines, recognize them, assemble the reading order and the layout blocks, and map the boxes
back to the page as it was handed in. Plus `pipeline.InputError`, the "this file cannot be read"
exception the document layer (step 160) and the CLI turn into an error line.

The *steps* were ported earlier (`Detect` `070`, `RecPreproc`/`Recognize` `080`/`100`,
`LineMask` `085`, `Orientation` `090`, `OrderPost`/`ReadingOrder` `110`/`115`/`120`, `LayoutPost`
`130`, `Assembler` `130`); this step is the driver that sequences them the way `__call__` does,
and the `self`-state they read from it.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Pipeline/OrtOcr.cs` | `OrtOcr` — the model folder, the flags, and `Detect` / `Recognize` / `HanziLines` / `Orientation` / `Upright` / `Assemble` / `Decode` / `ReadingOrder` / `GutterCuts` / `SplitLine` / `IsSpace` / `Call` |
| `src/Indra.Ocr.Core/Pipeline/InputError.cs` | `InputError` |
| `src/Indra.Ocr.Core/Pipeline/Orientation.cs` | `UnrotateBox` (float64 line/block boxes) |
| `src/Indra.Ocr.Core/Numerics/Numerics.cs` | the `n2 % 8` split rounding (below) |
| `src/Indra.Ocr.Tests/OrtOcrTests.cs` | the parity test (1) |
| `src/Indra.Ocr.Tests/fixtures/pipeline.json` | the fixture (10 cases, 925 KB) |
| `tools/scripts/test/gen_pipeline_fixtures.py` | its generator |

## Shape

```
Call(gray, turn):                          # __call__
  upright, k, skew = Upright(gray, turn)   # orientation + estimate_skew + rotate_page
  t[orient_ms]
  boxes = Detect(upright)                  # [N, 5]
  t[det_ms]
  rec = Recognize(upright, boxes)
  t[rec_ms]
  page = Assemble(upright.w, upright.h, boxes[:, :4], rec, t)
  page.rotation, page.skew = 90 * k, round(skew, 2)
  if skew:  lines' boxes and blocks' bboxes -> UnrotateBox(.., skew, wh)
```

`Upright` returns the page as a new `GrayPixels` only when the page actually changed (turned or
deskewed) and `null` otherwise — the reference returns the *same array object* then, and the
driver's `uprightPage is null ? gray : uprightPage.View` reproduces "the caller's buffer is still
the page" without copying.

## Port notes

- **The class holds only the model folder and the flags**, exactly like the reference: `threads`,
  `rec_int8`, `adaptive`, `table_heads`, `providers`, `furniture`, `math`, plus `det_long_side` /
  `rec_height` from `meta.json`. Every step is delegated to the class that already owns it, so
  `OrtOcr` has no algorithm of its own beyond the sequencing and the two box mappings.
- **The method names collide with the type names.** The reference's methods are `detect`,
  `orientation`, `reading_order`; the C# classes are `Detect`, `Orientation`, `ReadingOrder`. The
  methods keep the reference's names (`Detect`, `Orientation`, `ReadingOrder`) and the two types
  get aliases (`using Detector = Indra.Ocr.Det.Detect; using Orient =
  Indra.Ocr.Pipeline.Orientation;`) so both are reachable.
- **`assemble` takes `[n, 4]`, the detector gives `[N, 5]`.** The reference hands its `[N, 5]`
  array straight to `assemble`, which only ever reads `boxes[:, :4]` (and re-shapes to `(-1, 4)`
  after the gap split) — the fixture generator does the same slice before calling it, and
  `Assembler.Run`'s `boxes` is the four-wide span (step `130`). `OrtOcr.Quads` does the drop, and
  `Call` passes `Quads(boxes)`.
- **`unrotate_box` needs float64.** The line boxes and block bboxes are `list[float]` (float64) in
  the reference, so `Orientation.UnrotateBox` got a `double` overload (the float one stays for the
  text-layer callers) and the `skew != 0` gate is the reference's `if skew:` — a skew that rounded
  to `0.0` never reaches the mapping, so `-0.0` cannot leak into it.
- **The timing keys are the output, the values are not.** `Call` sets `orient_ms`, `det_ms`,
  `rec_ms` and passes the dictionary on; `Assembler.Run` adds `order_ms` and recomputes `total_ms`
  as the sum of the others. `fixtures/pipeline.json` records the *keys* (the CLI prints them) and
  asserts `total_ms` is the sum of the rest.
- **`math=True` throws.** Step `200` is the last step of the plan. The reference raises
  `FileNotFoundError` when the folder has no math models, which the shipped model set does not, so
  the constructor throwing there is faithful for every model set we have.
- **`InputError` carries the reference's wording** because it is what a caller prints.

### The pairwise-sum bug this step found

`fixtures/pipeline.json` compares every line's confidence exactly, and `table_pmc` line 122 came
back 2 ulp low. The cause was `Numerics.PairwiseSum32`: numpy's `npy_pairwise_sum_FLOAT` rounds
the recursion's split point down to a multiple of 8 (`n2 -= n2 % 8`), and the transcription had
lost that line. Restoring it makes `Numerics.Sum32`/`Mean32` reproduce numpy's contiguous float32
sum/mean **bit for bit** — verified over 404 sizes (1..400, 512, 1000, 4096, 65536, 640000) and
2-D row sums, pinned by `fixtures/numerics.json` (`sum32`), and it is what makes the confidence
comparison exact. The "numpy uses a CPU-specific SIMD reduction" note in `docs/090`, `docs/100`,
`docs/110` and `PLAN.md` was a misreading of this bug and is corrected there; the eight
accumulators of the pairwise tree *are* AVX2's eight float32 lanes.

## Verification

- `tools/scripts/test/gen_pipeline_fixtures.py` regenerates `fixtures/pipeline.json`
  byte-identically (925 225 bytes), and `OrtOcrTests` passes against it: `detect` bit-exact
  (`[N, 5]` float32), the recognizer's texts, confidences and every token (id or character, and
  the float64 page x), and the page's lines (box/text/conf), markdown, blocks, source, note,
  rotation, skew and timing keys.
- The ten cases cover: the order model's markdown on four pages, `furniture`, the `table_heads`
  cell re-split, `adaptive=False` (188 boxes → 189 lines), the CJK path, the orientation model
  (a page turned `rot90` comes back with `rotation=90` and byte-identical markdown), the
  `turn=False` path (460 boxes → 474 garbage lines: the orient model is not asked), and the deskew
  path (`rotate_page(gray, 2.0)` → `skew=-2.00`, which is what exercises `UnrotateBox`).
- 121 tests green, build clean.

## Deviations

- **Derived inputs are reproduced, not stored.** Three cases feed the pipeline something other
  than the file on disk (`np.rot90(gray, 1)` and `rotate_page(gray, 2.0)`). Those operations are
  bit-exact in the port (`Orientation.Rotate90` step `090`, `RotatePage` step `040`), so the
  fixture records the derivation (`derive`) instead of embedding megabytes of pixels, and the test
  reproduces it and checks the sha256 of the result. Everything else about the case (the option
  set, the detector's boxes, the page) is recorded as the reference produced it.
- **The inputs and the model folder are external.** The example pages live under
  `resources/indra-src/assets/examples` and the models under `resources/models`; neither is in
  git, so the test returns early when either is missing (like the other `TestModels`-based tests)
  and the fixture keeps the expectations reviewable. The recorded sha256 of the file *and* of the
  grayscale the reference decoded from it guard against a re-downloaded page silently changing the
  comparison.
