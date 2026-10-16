# 200 — MathReader

**Status:** done.

## Goal

Port `math.py` — the math line classifier (`mathcls.onnx`) and **MathRec**, the attention decoder
that reads a line as text with inline LaTeX (`math_enc.onnx`, `math_dec_init.onnx`,
`math_dec_step.onnx`) — and wire it into the three places the reference calls it:

- `pipeline.OrtOCR.recognize`'s tail: lines the classifier flags are read again by MathRec and a
  confident reading that holds LaTeX replaces the recognized line;
- `document.PdfConverter._read_math`: the same for **text-layer** lines, whose text is right but
  whose LaTeX is missing, with the line keeping its place in the PDF content order;
- `pipeline.OrtOCR.__init__`'s `math=True`: `MathReader.load` over the same folder, raising
  `FileNotFoundError` when the math models are not there.

`--math` is the CLI/viewer option that turns it on (`docs/180-cli.md`, `docs/215-argparse-core.md`).

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Maths/MathReader.cs` | `MathTensor`, `IMathModel`, `MathReading`, `MathReader` |
| `src/Indra.Ocr.Core/Onnx/OnnxMathModel.cs` | `IMathModel` over an `InferenceSession` |
| `src/Indra.Ocr.Core/Onnx/MathModels.cs` | `Graphs`/`RequiredFiles`/`Load` (the folder half of `MathReader.load`) |
| `src/Indra.Ocr.Core/Rec/Recognize.cs` | the math hook in `Recognizer.Run` |
| `src/Indra.Ocr.Core/Pipeline/OrtOcr.cs` | `math=True`, the shared `SessionOptions`, the `Math` property |
| `src/Indra.Ocr.Pdf/PdfConverter.cs` | `ReadMath` (`_read_math`) and the `math_ms` timing |
| `tools/scripts/test/gen_math_fixtures.py` | → `fixtures/math.json` |
| `src/Indra.Ocr.Tests/MathReaderTests.cs` | the parity tests (+ the four stand-in graphs) |

## The four graphs

| Graph | In | Out |
|---|---|---|
| `mathcls.onnx` | `x` f32 `[B,1,32,W]`, `widths` i64 `[B]` | `p` f32 `[B]` |
| `math_enc.onnx` | `x` f32 `[B,1,64,W]`, `widths` i64 `[B]` | `memory` f32 `[B,T,192]`, `mask` f32 `[B,T]` |
| `math_dec_init.onnx` | `memory` f32 `[B,T,192]` | `cross_k`, `cross_v` f32 `[3,B,8,T,24]` |
| `math_dec_step.onnx` | `token` i64 `[B]`, `pos` i64 `[]`, `self_k`/`self_v` f32 `[3,B,8,P,24]`, `cross_k`/`cross_v` f32 `[3,B,8,T,24]`, `mask` f32 `[B,T]` | `logits` f32 `[B,652]`, `new_k`/`new_v` f32 `[3,B,8,P+1,24]` |

The classifier reads the **recognizer's** crop (32 rows) and MathRec the metadata's `height` (64),
so a math line is cropped twice with two different heights. `652` is `4 + len(tokens)`:
`math_meta.json` has 648 tokens and the reader prefixes four empty strings for the special ids
(`BOS` 1, `EOS` 2, the first real token at 4).

`pos` is a **rank-0** int64 tensor — `MathTensor.Int64(new[] { (long)t })` with no dimensions, which
`DenseTensor<long>` accepts and ONNX Runtime reads as the scalar the graph declares.

## Constants and seams

`MATH_MIN = 0.5` (classifier), `MATH_MIN_CONF = 0.9` (a reading replaces a line), `EARLY_STOP_MARGIN
= 0.1` (after 8 tokens a line this far below `MATH_MIN_CONF` stops decoding), `BOS/EOS/N_SPECIAL =
1/2/4`, `batch = 64`.

- **`IMathModel`** is the seam, like `IRecModel` is for the recognizer: `InputNames` plus
  `Run(IReadOnlyDictionary<string, MathTensor>)`. `InputNames` mirrors the reference's
  `self.inputs = {f: {i.name for i in v.get_inputs()} ...}` — the exports drop unused inputs (the
  init graph does not read the `mask`), and the reference filters its feed by those names before
  every call. The port does the same (`MathReader.Feed`), so a re-export cannot break it and a test
  can hand the reader four stand-in graphs.
- **`MathTensor`** carries the values and the shape (f32 or i64). The shape has to travel with the
  values because the decoder's `logits` shape is what says how wide the vocabulary is, and because
  the reference reads the crop height off the array's shape — a `float[]` has none, which is why
  `Flags`/`Probabilities` take it as an argument.
- **`MathReader.Create`** is public (the reference's `MathReader.__init__` minus the sessions), so
  the fixture's stand-ins can build a reader without model files.
- **`MathReader` lives in `Indra.Ocr.Maths`, not `Indra.Ocr.Math`.** A namespace member called
  `Math` captures every unqualified `Math.` in the assembly — an enclosing namespace's own members
  win over `using System;` — so `HelpFormatter.cs` and `DetPost.cs` stopped compiling
  (`CS0234: the type or namespace name 'Max' does not exist in the namespace 'Indra.Ocr.Math'`).
  The reason is in the class's `<remarks>`.
- **`Recognizer` carries the hook** (`MathReader? math = null`), which makes `Rec` and `Maths`
  reference each other. The reference's `math.py` imports `line_mask`/`rec_preproc` too, so the
  cycle is the reference's own shape; splitting the hook out would have meant a callback for two
  lines of code.
- **`OnnxMathModel` owns its session** and disposes it — the opposite of `OnnxRecModel`, which
  borrows one from `OcrModels`. The math sessions have no other owner, so `MathReader.Dispose`
  disposes the four graphs. `OrtOcr` builds one `SessionOptions` (`OnnxSessions.CreateOptions`) for
  the OCR and the math sessions alike — as the reference does — owns it, and passes it to
  `OcrModels.Load` and `MathModels.Load`.
- **`MathModels.Load`** returns `null` unless all five files (`mathcls`, `math_enc`,
  `math_dec_init`, `math_dec_step`, `math_meta.json`) are present, exactly like
  `MathReader.load`; `OrtOcr(math: true)` then throws
  `FileNotFoundException($"math=True needs the math models in {onnxDirectory}")`. `models.model_dir`
  downloads those five only when `--math` was asked for (`docs/170-models.md`).

## `detokenize`

```
out, math, in_math = [], [], False
for t in tokens:
    if t == "$":                    # a delimiter: closes (or opens) the math run
        if in_math: out.append("$" + " ".join(math) + "$"); math = []
        in_math = not in_math
    elif in_math: math.append(t)
    else:         out.append(t)
if in_math and math: out.append("$" + " ".join(math) + "$")
return " ".join("".join(out).split())
```

Outside math the tokens are **concatenated** (`"a", "b"` → `"ab"`), inside they are joined by
spaces; an unclosed run is closed at the end, but a lone `"$"` with nothing after it produces
nothing. The final `" ".join(text.split())` is Python's whitespace collapse, which the port already
has (`UnicodeText.CollapseSpaces` — the same predicate set, U+00A0 included).

## `flags`

The classifier over the recognizer's crops: `width_batches` + `pad_batch` (step `080`) exactly as
the recognizer batches, then `p > MATH_MIN`. `Probabilities` is the same loop with the raw `p`
exposed — the parity fixture compares it bit for bit, because a bool would hide a crop, a padding
or a resize that went wrong in the last ulp.

## `read`

```
crops  = crop_lines(gray, boxes[:, :4], height, None)          # 64 rows, no CenterMask
for idx in width_batches(widths, batch, max_ratio=4.0, max_area=16*64*640):
    mem, mask = enc(x, widths)
    ck, cv    = init(memory=mem)                                # the mask is dropped
    sk = sv = zeros((L, B, H, 0, D)); tok = BOS; done = False
    for t in range(max_tokens - 1):
        logits, sk, sv = step(token=tok, pos=t, self_k=sk, self_v=sv, cross_k=ck, cross_v=cv, mask=mask)
        tok = logits.argmax(-1)
        top = 1.0 / exp(logits - logits.max(-1, keepdims=True)).sum(-1)
        for b in range(B):
            if done[b]: continue
            probs[b].append(float(top[b]))
            if   tok[b] == EOS:                                        done[b] = True
            elif len(probs[b]) >= 8 and mean(probs[b]) < 0.8:          done[b] = True
            elif tok[b] >= N_SPECIAL:                                  seqs[b].append(int(tok[b]))
        if done.all(): break
    for k, j in enumerate(idx):
        out[j] = (detokenize([itos[i] for i in seqs[k]]),
                  float(mean(probs[k])) if probs[k] else 0.0)
```

Port notes:

- **Few, wide batches.** `max_ratio 4.0` and `max_area = 16*64*640` (not the recognizer's
  defaults): one batch is one decoding loop of up to `max_tokens` steps, and a step costs about the
  same for 8 lines as for 40.
- **The feed is filtered per graph** (`Feed`), which is what makes the init call see `memory`
  alone.
- **`sv = sk` at the start** is an alias in the reference (both are the same zero-length cache);
  the port makes two `MathTensor`s pointing at the same empty array — neither is ever mutated.
- **`tok` is updated for every row on every step**, before the `done` test: the reference's
  `tok = logits.argmax(-1)` sits outside its `for b in range(B)` loop. The port keeps that order
  (`tok[row] = best` first, then `if (done[row]) continue`), so a finished row still feeds the
  step's argmax.
- **`top` is a float32 expression.** `e` is float32, `e.sum(-1)` is numpy's float32 sum
  (`Numerics.Sum32`), and NEP 50 keeps the Python `1.0` weak, so the division is float32 —
  `1f / Numerics.Sum32(e)`.
- **The mean is a float64 mean** (`Numerics.Mean64`), because `probs[b]` is a list of Python floats
  and `np.mean` turns it into a float64 array: numpy's `pairwise_sum_DOUBLE` tree. The naive
  `Numerics.Mean(double)` is Python's `sum()/n` and is *not* what this uses — the new
  `sum64`/`sum64_rows` sections of `fixtures/numerics.json` pin `Sum64`/`Mean64` against numpy
  (bit-exact, 15 sizes straddling 8/128/the multiples of 8, plus `mean(1)` over 9 rows).
- **The early stop** (`len(probs) >= 8 and mean < MATH_MIN_CONF - EARLY_STOP_MARGIN`) marks the row
  done without adding the token, so the reading keeps its low mean and is rejected by
  `MATH_MIN_CONF` anyway. `probs[b]` stays empty only for a row whose first step is EOS, which is
  the `if probs[b] else 0.0`.
- **The vocabulary comes from the logits' last dimension** (`logits.shape[-1]`), not from the
  metadata: the fixture's stand-in vocabulary is 14 tokens wide.

## Integration

**The OCR path** (`Recognizer.Run`, the reference's `recognize` tail) runs after the CTC groups:
`Flags(crops, batch, height)` over the crops the recognizer has just made (the recognizer's height,
32), then `Read(gray, boxes[math])` and, for every reading with `"$" in text and conf >=
MATH_MIN_CONF`, the line becomes `(text, 1.0, [(text, xc)])` with
`xc = float(boxes[j, 0] + boxes[j, 2]) / 2`. One token holds the whole line, so it is never split;
the token has no PDF content index (this is the OCR path).

**The text-layer path** (`PdfConverter.ReadMath`, the reference's `_read_math`) considers only the
lines whose first token is a character carrying a content index (`isinstance(r[2][0][0], str)` and
`len(r[2][0]) > 2`), crops them at the recognizer's height **without** CenterMask, and replaces a
line's text with `(text, 1.0, [(text, xc, first)])` where `first = min(content index)` — so the line
keeps its place in the PDF content order. The step is timed as `math_ms` (the reference's
`t["math_ms"]`), and the CLI prints it under `--timings`.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `fixtures/math.json` (70 KB) holds 15 `detokenize` cases, a `real` section and a `standin`
  section; the pages and crops travel as zlib+base64 raw arrays, so the tests need neither an image
  codec nor `det.onnx`.
- `MathReaderTests` (21) — every `detokenize` case, the unclosed-run rule, the real models and the
  stand-ins; `NumericsTests` gained `Float64_reductions_match_numpy` (16 numerics tests in all).
- **The real case** (`temp/math-models`, skipped when the folder is absent): the math page
  (`temp/inputs/math.png`, 7 detected lines) through the four real graphs —
  - the metadata: `height 64`, `max_tokens 320`, `dec_layers 3`, `dec_heads 8`, `head_dim 24`;
  - the classifier's probabilities **bit-exact** (`[0.0606, 1.0, 1.0, 1.0, 0.9986, 1.0, 0.0164]`)
    and its flags `[F,T,T,T,T,T,F]`;
  - MathRec's five readings, texts exact, e.g. `$\sum _ { \mathrm { i = 1 } } ^ { \mathrm { ~ n ~ n
    ~ } }$ i $=$ n(n $+$ 1)/2` (0.9335) and `$\mathrm { l i m _ { x + 0 } }$ sin x / x $= 1$`
    (0.9270) — the two the CLI replaces; the worst relative confidence difference over the five is
    **3.5e-9**.
- **The stand-ins** run without any model file and pin the loop's logic: a scripted decoder with
  four behaviours in one batch — tokens with `$` (`$x _$`, confidence exactly 1.0), the
  low-probability filler (empty text, `1/14 = 0.0714…`, the early stop after 8 steps), tokens
  without `$` (`{i`), and a script longer than `max_tokens - 1` (the loop's bound). The stand-ins
  *assert the loop's invariants* and throw when the port breaks one: the self-attention cache is
  `[L,B,H,pos,D]` at every step (empty at the first, the previous step's output afterwards), `pos`
  is the step number, `token` is the previous step's argmax, and the init call is fed `memory`
  alone. The classifier stand-in returns `0.9` for the first line of a batch and `0.1` for the
  rest, so a different `width_batches` split shows in the probabilities; the fixture records the
  four batches it produced.
- `cli_parity.py` → **83 cases, all byte-identical** (the 77 of step `195` plus six): `math-page`
  (`math.png --math`), `math-page-json`, `math-text` (`math.pdf --math`, the text-layer path),
  `math-plain` (a page with no math lines — the classifier's all-negative path), `math-pool` (four
  pages through the worker pool, so the workers load the math models too) and `out-math` (the
  Markdown written to `--out`). The existing `math` case (`--math` over `resources/models`, which
  has no math models) still covers the `FileNotFoundError`.
- End to end, both call sites are byte-identical to the reference: `math.png --math` and
  `math.pdf --math` (which exercises `_read_math`: the text layer's `E = mc²` line comes back as
  `$$\mathrm { E = m c ^ { 2 } }$$` in its content-order place).

## Deviations

- **The reference needs `PYTHONUTF8=1` for `--math` on this machine.** `math.py` reads
  `math_meta.json` with `read_text()`, i.e. in the locale encoding — `cp936` here — and the file is
  UTF-8 with 11 non-ASCII bytes (`tokens` holds `–`, `’`, `⁄`), so the reference dies with
  `UnicodeDecodeError: byte 0x93` at offset 7779. The port always reads UTF-8. The harness sets
  `PYTHONUTF8` **per math case** (`Case.env`), not globally, so the other 77 cases keep seeing the
  reference's own locale behaviour.
- **The confidence is a float32-derived mean.** `top` is `1.0 / e.sum(-1)`, and numpy dispatches a
  contiguous float32 sum to a CPU-specific SIMD kernel whose tree differs from
  `npy_pairwise_sum_FLOAT` in the last ulp (the known deviation recorded in `PLAN.md`). One of the
  five real readings differs by **3.5e-9** relative as a result; the mean itself is bit-exact
  (`Numerics.Mean64`, proven by the new numerics fixture). The CLI output is unaffected — a
  replaced line's confidence is always `1.0`, and a rejected reading is never printed.
- **`Flags`/`Probabilities` take the crop height.** The reference reads it off `lines[0].shape[0]`;
  a `float[]` has no shape, so the caller says it. It is the *recognizer's* height (32), not
  MathRec's, and it is the last parameter so the reference's `batch` position is unchanged.
- Nothing else: the crops, the batches, the feeds, the argmax, the EOS/early-stop decisions, the
  detokenized texts and the classifier's probabilities are compared exactly.
