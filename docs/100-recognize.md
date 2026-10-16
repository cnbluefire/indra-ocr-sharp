# 100 — recognize

**Status:** done.

## Goal

Port `pipeline.OrtOCR.recognize` and `pipeline.OrtOCR.hanzi_lines`: crop every line box, route the
lines that hold Chinese characters to the CJK recognizer, run both recognizers over width-similar
batches, collapse the CTC output, and give every kept token the page x of its timestep. The math
override (`self.math`) is step `200`.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Rec/Recognize.cs` | `IRecModel`, `IScriptModel`, `RecOutput`, `RecToken`, `RecognizedLine`, `Recognizer` |
| `src/Indra.Ocr.Core/Onnx/OnnxRecModel.cs` | `IRecModel` over an `InferenceSession` (rec / rec_cjk / rec.int8) |
| `src/Indra.Ocr.Core/Onnx/OnnxScriptModel.cs` | `IScriptModel` over an `InferenceSession` |
| `tools/scripts/test/gen_recognize_fixtures.py` | → `fixtures/recognize.json` |
| `src/Indra.Ocr.Tests/RecognizeTests.cs` | the parity tests (+ the deterministic stand-ins) |

## Constants and seams

`MIN_CONF = 0.5`, `height = meta.json["rec_height"]` (32), `batch = 64`, and `PAD_X = 0.3` /
`WIDTH_MULTIPLE = 8` from step `080`'s `RecPreproc`. `IRecModel` is the recognizer's signature —
`x` f32 `[B, 1, 32, W]` and `widths` int64 `[B]` in, `ids` int64 `[B, T]`, `conf` f32 `[B, T]` and
`lengths` int64 `[B]` out — and covers both recognizers, which is why the CJK one needs no second
interface. `IScriptModel` is `script.onnx`: the same two inputs, one probability per line.

## `recognize`

```
crops = crop_lines(gray, boxes[:, :4], height, mask)       # step 085 (mask.onnx or None)
out   = [("", 0.0, [])] * len(crops)
groups = [(rec, vocab, range(len(crops)), False)]
if script is not None and crops:
    hanzi = hanzi_lines(crops, batch)
    groups = [(rec, vocab,  [j for j, h in ... if not h], False),
              (rec_cjk, vocab_cjk, [j for j, h in ... if h], True)]
for sess, vocab, sel, as_chars in groups:
    for part in width_batches([crops[j].shape[1] for j in sel], batch):
        idx = [sel[i] for i in part]
        arr, widths = pad_batch([crops[j] for j in idx])
        best, pmax, lengths = sess.run(None, {"x": arr, "widths": widths})
        stride = arr.shape[-1] / max(best.shape[1], 1)
        for k, j in enumerate(idx):
            n = int(lengths[k])
            seq, pm = best[k, :n], pmax[k, :n]
            keep = (seq != 0) & concatenate([[True], seq[1:] != seq[:-1]])
            h = max(1.0, y1 - y0)
            ix0 = floor(x0 - PAD_X * h); cw = ceil(x1 + PAD_X * h) - ix0
            f = cw / crops[j].shape[1]
            t = nonzero(keep)[0]
            xs = ix0 + (t + 0.5) * stride * f
            conf = float(pm[keep].mean()) if keep.any() else 0.0
            out[j] = (vocab.decode(...), conf, toks) if conf >= MIN_CONF else ("", conf, [])
```

Port notes:

- **Cropping** is step `085`'s `LineMask.CropLines`, called with the same `mask` seam (the shipped
  model set has no `mask.onnx`, so it takes the plain `RecPreproc.CropLine` path).
- **Batching** is step `080`'s `WidthBatches` + `PadBatch`; `sel` indexes the crops and `part`
  indexes `sel`, so a group's batch is `[sel[i] for i in part]` — the port keeps both levels.
- **`T` comes from the model**, not from the crop width: `stride = padded_width / T` is what maps
  a timestep back to pixels, and `lengths[k]` is the line's own valid-timestep count. numpy's
  `seq[k, :n]` silently clamps at `T`, which the port reproduces with `Math.Min(lengths, T)`.
- **The CTC collapse** is `(seq != 0) & concatenate([[True], seq[1:] != seq[:-1]])`: the first
  timestep of every run of equal ids, and never the blank. `kept` holds the *timestep indices*,
  because the x mapping needs them (not the position within the kept run).
- **The x mapping stays in double.** The box is float32 widened to `double` (`float(v)` in Python),
  `h`, `ix0`, `cw` and `f` are doubles, `t + 0.5` is a double, and the product associates as
  Python's does: `ix0 + ((t + 0.5) * stride) * f`. `stride` itself is an integer division of the
  padded width by `T`.
- **Tokens** are `(id, x)` for the Latin recognizer, but `(vocab_cjk.chars[id - 1], x)` for the CJK
  one: its ids mean nothing in the other vocabulary, so like the text layer's tokens they are
  characters — hence `RecToken.Character`.
- **`RecognizedLine.Confidence` is a `double`** because the reference computes a float32 mean and
  widens it (`float(pm[keep].mean())`); that widened value is what the JSON output carries.
- **The `MIN_CONF` gate** replaces the text and the tokens of a low-confidence line with `""` and
  `[]`, but keeps the confidence. Lines the pipeline never fills in stay `("", 0.0, [])`
  (`RecognizedLine.Empty`), exactly like the shared tuple in the reference's `out` list.
- **`hanzi_lines`** runs the script model over the *same* crops with the same batching and takes
  `p[k] > 0.5`, so a probability of exactly `0.5` is not hanzi. The port exposes it publicly
  (`Recognizer.HanziLines`) so the fixture can pin the routing.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → **70 passed** (was 67; +3 `RecognizeTests`), 14 s.
- `fixtures/recognize.json` (193 KB) holds 7 pages and 16 cases (8 real + 8 stand-in); each case
  records its boxes (from the real detector, so the test does not need `det.onnx`), the recognized
  lines (text, confidence, and every token's id/character and x) and the hanzi decisions.
- Real cases (skipped only without `resources/models`) run `rec.onnx`, `rec_cjk.onnx` and
  `script.onnx` through the `OcrModel` set's own sessions (`intra_op_num_threads = 1`, as the
  reference) and match every text, confidence, token and x:
  - `latin` (5 lines, 109 tokens), `two_columns` (5 / 79), `canvas` (24 / 361) — the Latin
    recognizer, including a 24-line page that exercises `pad_batch` across one batch;
  - `widths` (18 / 242) — a page whose lines are ~4× apart in width, so `width_batches` has to
    split them; one of its lines is gated (`conf 0.4894 < MIN_CONF`);
  - `cjk` (3 lines, 2 routed to the CJK recognizer) and `mixed` (a page carrying both scripts, 1
    of 3 routed) — the hanzi routing with both groups non-empty;
  - `handmade` — four hand-made boxes on the Latin page (a normal one, one with `y0 == y1` so `h`
    collapses to 1.0, one starting left of the page and one entirely outside it); two are gated and
    the script model classifies three as hanzi, so the CJK recognizer reads them;
  - `blank` — no boxes at all.
- Stand-in cases need no model file and run everywhere: a deterministic recognizer
  (`T = max(1, W/4)`, a blank every third timestep, the two others sharing an id, dyadic
  confidences) over a synthetic vocabulary, plus a script classifier returning 1.0 / 0.5 / 0.25 by
  batch position. They pin the CTC collapse (`standin_plain`, `standin_highconf`), the gate
  (`standin_gated`: all three lines below 0.5; `standin_plain`: one line at *exactly* 0.5, which
  passes), an all-blank model, `lengths > T` (`standin_overlong`, which clamps), the CJK
  characters and the mixed routing (`standin_cjk`, `standin_cjk_high`) and an empty box list. Their
  confidences are exact: the kept values are `0.75`/`0.25`/`0.5`, whose partial sums are all
  representable, so the mean is the same in any summation order — these comparisons are bit-exact.
- The fixture records the deviation statistics it measured: **0 of 61** real kept means differ from
  numpy's SIMD mean, and the closest real confidence to `MIN_CONF` is **2.245e-3**.

## Deviations

- **The confidence is a float32 mean** (`pm[keep].mean()`), i.e. numpy's contiguous pairwise
  reduction, which the port now reproduces bit for bit (`Numerics.Mean32`; step `155` added the
  `n2 % 8` rounding the transcription had lost — see `docs/090-orientation-deskew.md`). `pm[keep]`
  is a boolean-masked gather, so it is contiguous, and the `MIN_CONF` gate and the `"conf"` the
  CLI prints are exact. (This used to be listed as a deviation: a probe over uniform random
  float32 arrays in `[0.55, 0.95]`, sizes 1-200, found 9.6% of means differing, worst **2.4e-7**
  relative — all of it the missing rounding. `fixtures/pipeline.json` now compares every line's
  confidence exactly, on 10 cases over the example pages.)
- Nothing else: the crops, the batching, the ids, the token x positions and the texts are compared
  exactly.
