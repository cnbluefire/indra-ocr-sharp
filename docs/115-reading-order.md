# 115 — reading_order

**Status:** done.

## Goal

Port `pipeline.OrtOCR.reading_order` — the driver that feeds the order model and picks between
the plain pairwise decode and the layout-head block path — and give it its model seam:
`IOrderModel`, backed by `OnnxOrderModel` over an ONNX Runtime session, exactly like the
detector, the recognizer, the mask, the orientation classifier and the script classifier before
it.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Order/ReadingOrder.cs` | `IOrderModel`, `OrderOutput`, `LayoutHeads`, `ReadingOrderResult`, `ReadingOrder.Run` |
| `src/Indra.Ocr.Core/Onnx/OnnxOrderModel.cs` | `IOrderModel` over an `InferenceSession` (`order.onnx`) |
| `tools/scripts/test/gen_order_fixtures.py` | added the `reading_order` section of `fixtures/order_post.json` |
| `src/Indra.Ocr.Tests/ReadingOrderTests.cs` | the parity tests (+ the deterministic stand-ins) |

## The model

`order.onnx` declares

```
feats  f32  [1, N, 16]     ->  a     f32  [1, N, N]
mask   bool [1, N]             cls   f32  [1, N, 10]
                               same  f32  [1, N, N2]
                               tab   f32  [1, 3, N, N2]
```

with `N2 == N` (checked at n = 1…24). The reference reads the outputs **by position**
(`outs[1]` is `cls`, `outs[2]` is `same`), so the port does too, and `OrderOutput.HasLayoutHeads`
is the reference's `len(outs) >= 3`. `feats` is the only real input — the reference always passes
an all-true `mask` of the same length, which the adapter builds itself.

## `reading_order`

```
if len(boxes) < 2 or self.order is None:
    order = order_xycut(boxes[:, :4]) if len(boxes) else []
    return order, None, None
f    = line_features(boxes[:, :4], wh, texts)[None]
outs = self.order.run(None, {"feats": f, "mask": np.ones((1, len(boxes)), bool)})
if len(outs) >= 3:
    cls, same = outs[1][0], outs[2][0]
    tab = outs[3][0] if len(outs) > 3 and self.table_heads else None
    return decode_blocks_order(outs[0][0], same, boxes[:, :4], cls), (cls, same, tab), outs[0][0]
return decode_order(outs[0][0]), None, outs[0][0]
```

Port notes:

- **The short-page / missing-model guard comes first** and never runs the model: a page of zero
  or one line is decoded by XY-cut whether or not the model exists, and with no model every page
  is. `OrderXyCut` already returns `[]` / `[0]` for those sizes, so the port calls it
  unconditionally in that branch.
- **`IOrderModel.Run` takes the features, not the boxes** — the reference computes
  `line_features` and hands the array to the model — which lets a test stand in for the model
  without also standing in for the feature computation.
- **`LayoutHeads.Table` is the reference's `tab` gate**: `OrderOutput.Table` carries the model's
  fourth output whenever the model has one, and `ReadingOrder.Run` only hands it on when
  `tableHeads` is set (the CLI's `--table-heads`, step `180`; `False` by default).
- **`ReadingOrderResult.Logits` is the model's own `a` array** (no copy), because step `130`'s
  `assemble` uses it for the PDF content-order check (`logits[np.ix_(pdf, pdf)]`). It is `null`
  exactly when the model did not run, which is what `assemble`'s `logits is None` test sees.

## Verification

- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **81 passed** (was 79; +2 `ReadingOrderTests`), 15 s.
- `fixtures/order_post.json` gained 22 `reading_order` cases: every real page from
  `recognize.json` — `latin` (5 lines), `cjk` (3), `two_columns` (5), `mixed` (3), `widths` (18),
  `canvas` (24), `blank` (0) and `handmade` (4 off-page / degenerate boxes) — each with and
  without the model, plus `table_heads` variants of `latin` (n=5) and `blank` (n=0), and synthetic
  `single_line` (n=1), `empty` (n=0), `two_lines_nomodel` (n=2 without a model) and `two_lines` /
  `two_lines_table` (n=2 with it).
  Every case's expectation is the *real* `OrtOCR.reading_order` (called with a stub carrying only
  `order` and `table_heads`), and the real cases run the real `order.onnx` through
  `OnnxOrderModel`: the reading order, the class logits, the same-block logits, the table logits
  and the pairwise logits all match **bit-exactly** (14 cases with the model, 10 with layout
  heads, 2 with table logits). Where the model's order differs from the geometry baseline it
  differs clearly — `widths` `[0,2,1,3,4,6,8,5,11,…]` against XY-cut `[0,2,4,6,8,11,…]`, `canvas`
  `[0,1,4,2,3,…]` against `[0,4,1,5,2,…]` — so the fixture pins the model path, not just the
  fallback.
- The stand-in cases need no model file: a fixed-logit `IOrderModel` plus one that *throws* if it
  is called. They pin the delegation — `n < 2` and `model == null` → `OrderXyCut` with no heads
  and no logits; fewer than three outputs → `DecodeOrder`; three outputs →
  `DecodeBlocksOrder` with `LayoutHeads(cls, same, null)`; four outputs → `tab` reaches the
  caller only with `table_heads` on; the model is called exactly once per page of two or more
  lines — and that `Layout`/`Logits` are the model's own arrays, not copies.

## Deviations

- None of its own. The decoding it drives inherits `docs/110-order-post.md`'s deviations
  (`np.argsort` tie order, `lp`'s last ulp, the float32 sum tree above n = 128); the logits it
  returns are the model's raw output and are compared exactly.
