# 130 — assemble

**Status:** done.

## Goal

Port the assembly half of `indra_ocr/pipeline.py`: `OrtOCR.assemble` (lines → split at wide gaps and
column gutters → reading order → Markdown) with its helpers `gutter_cuts`, `split_line`, `_decode`
and `_is_space`, plus the two text-layer order checks `assemble` calls through
`content_order` — `order_backtracks` and `pdf_order_coherent` from `textlayer.py` (whose home is
step `150`; they live with `assemble` here because this is their only runtime caller and step `150`
builds on them rather than re-implementing them).

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Pipeline/Assemble.cs` | `OcrLine`, `OcrPage`, `LineSegment`, `Assembler` (`Run`, `GutterCuts`, `SplitLine`, `IsSpace`, `Decode`, `OrderBacktracks`, `PdfOrderCoherent`) |
| `src/Indra.Ocr.Core/Rec/Recognize.cs` | `RecToken.ContentIndex` (the text layer's optional third token element) |
| `tools/scripts/test/gen_assemble_fixtures.py` | `fixtures/assemble.json` |
| `src/Indra.Ocr.Tests/AssembleTests.cs` | the parity tests (9) |

## Shape

```
cuts = gutter_cuts(boxes, rec) if len(boxes) else []     # x of gaps that sit on a column gutter
for b, (text, conf, toks), at in zip(boxes, rec, cuts):
    for bx, tx, tk in split_line(b, toks, SPLIT_GAP, at):
        nb.append(bx); nrec.append((tx, conf, tk))
boxes, rec, texts = ..., order, layout, logits = reading_order(boxes, texts, wh)
if layout is not None and layout[2] is not None:          # the model has the table heads
    is_table = layout[0].argmax(-1) == BLOCK_CLASSES.index("Table")
    if is_table.any():
        split every table line with split_line(b, toks, CELL_GAP)   # cells
        if the count changed: reading_order(...) again
note = "order=model"
if content_order and len(boxes) > 1 and every line has a content index:
    pdf = sorted(range(n), key=content index)
    if pdf_order_coherent(boxes[pdf], logits[ix_(pdf, pdf)]): order, note = pdf, "order=pdf"
t["order_ms"], t["total_ms"] = ...
lines = [Line([round(float(v), 1) ...], rec[i][0], round(rec[i][1], 4)) for i in order]
if layout is not None:
    blocks = page_blocks(...); md = page_markdown(..., cells=..., tab=layout[2])
elif len(boxes) < 2:
    md = "\n".join(texts)
```

## Port notes

- **`split_line` returns `list[tuple[list, str, list]]`**, one item when nothing is cut. The
  reference's `[(list(box[:4]), _decode(toks), toks)]` keeps the *original* box (float32 values) and
  the *unstripped* decode; only the cut segments get `_decode(seg).strip()` and the tightened box
  `[max(box[0], min(xs) - 0.35*h), box[1], min(box[2], max(xs) + 0.35*h), box[3]]`.
- **`h = max(1.0, float(box[3] - box[1]))` clamps.** A zero-height line uses `h = 1.0`, so `gap * h`
  becomes tiny and the line is cut at nearly every pair (fixture case `tiny_box`).
- **`at` (the gutter cuts) only cuts between two neighbouring non-space characters** with the strict
  inequalities `prev < c < x`; `c == prev` or `c == x` does not cut (`at_equal`). `sorted(at or [])`
  is a value sort, so no tie-order question arises.
- **`_is_space` is called with the token key, not the tuple**: `tok == " " or tok ==
  vocab.index.get(" ")`. `RecToken` already separates the two token shapes, so `IsSpace` compares
  `Character == " "` for character tokens and `Id == space id` for id tokens.
- **`_decode` normalises only the character path**: `" ".join("".join(chars).split())` collapses
  every run of *Python* whitespace to one space, so it needs the Python whitespace set
  (`UnicodeText.IsSpaceCodePoint`), not .NET's `Split()`. The id path is `vocab.decode(ids)`, which
  drops id 0 (the CTC blank) and invalid ids — a line of blanks decodes to `""`.
- **numpy float32 comparisons in `gutter_cuts`.** `rights - gl` is a float32 array minus a (weak)
  Python float: NEP 50 keeps the subtraction *and* the `< 0.5*h` comparison in float32, so the port
  computes `MathF.Abs(rights[j] - (float)gl) < (float)(0.5*h)`. `h` itself is a Python float
  (`float(...)`), as are the cut midpoints `(a + c) / 2`.
- **`order_backtracks` compares float32 too**: `b[1] < a[1] - 0.5*h` and `b[0] < a[2] - h` are
  float32 box edges against weak Python floats. `h = float(np.median(...))` is the float32 median of
  the line heights widened to double (`Numerics.Median(ReadOnlySpan<float>)`), and
  `len(boxes) < 3` returns 0.
- **`pdf_order_coherent`**: the backtrack budget is `max(1, int(0.02 * n))` (truncating), and the
  agreement is the float64 mean of `pair_logits[i, i+1] > 0`, computed here as a count over `n - 1`
  (exact for 0/1 values). Fewer than `n * n` logits are treated as "no logits"; the reference cannot
  be called that way from `assemble`.
- **The table re-split keeps the original confidence** (`r[1]`, not the segment's) and the unchanged
  lines are copied verbatim; the second `reading_order` runs only when the cell split changed the
  line count.
- **`keys = [min(content indices of the line, default=None)]`**: the PDF content order is only kept
  when *every* line has a content index, i.e. when the recognizer's 2-tuples were replaced by the
  text layer's 3-tuples everywhere. `RecToken.ContentIndex` is the port of "the third element exists"
  (`len(tk) > 2` in the reference).
- **`sorted(range(n), key=keys[i])`** is stable (ties keep index order), so the port's comparer adds
  the index as tiebreak — the documented Python-sort rule, no numpy involved.
- **`t["total_ms"] = sum(v for k, v in t.items() if k != "total_ms")`** is re-computed *after*
  `order_ms` was written, over the dictionary's insertion order. The port fills the same keys; the
  values are wall clock (a `Stopwatch` for `order_ms`) and are not part of the parity contract.
- **`Page` fields** map 1:1: `lines` (reading order), `timings`, `markdown` (`None` unless the model
  has layout heads or the page has fewer than two lines), `source` (`"ocr"`; the text-layer and error
  sources belong to step `160`), `note`, `blocks`, `rotation`, `skew`, and the `text` property
  (`"\n".join(line.text)`).
- **`md = "\n".join(texts)`** for a page of fewer than two lines uses the *split* texts (after
  `SPLIT_GAP`), not the original lines.

## Verification

- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **104 passed** (was 95; +9 `AssembleTests`), 15 s.
- `fixtures/assemble.json` (460 KB) holds 5 `is_space`, 9 `decode`, 13 `split_line`, 6 `gutter_cuts`,
  7 `order_backtracks`, 9 `pdf_order_coherent` and 40 `assemble` cases. Every expectation is the
  reference's own output; boxes and model outputs are raw little-endian float32.
- The whole-page cases drive the real reference `OrtOCR.assemble` with a stub carrying only the
  model, the vocabulary and the flags. The real pages of `recognize.json` (`latin`, `cjk`,
  `two_columns`, `mixed`, `widths` n=18, `canvas` n=24, `handmade` n=4, `blank` n=0) run through the
  real `order.onnx`, each also with `table_heads` on and with `keep_furniture`; the synthetic cases
  reach what the real pages do not: a table line that the table heads split into cells and the second
  order pass it forces (`table_cells`), one line cut at the column gutter (`gutter_split`), a
  wide-gap split on a page with no model (`wide_split_nomodel`), empty and single-line pages (with
  and without a model), and the `content_order` path with text-layer tokens (aligned, scrambled,
  backtracking, partially indexed, with agreeing and disagreeing logits, and a single line).
- No model file is needed: each `assemble` case records the logits its model returned for every line
  count it was asked for, and the test's `IOrderModel` is a lookup table over that. A case whose
  model is `null` runs with no model at all, exactly as in the reference.

## Deviations

- **Timings** (`order_ms`, `total_ms`) are wall clock, as in the reference; the tests only assert the
  keys are present.
- **`_is_space`, `gutter_cuts`, `split_line`, `assemble` are instance members** of `Assembler`, whose
  constructor takes the pieces the reference reads from `self` (`order`, `table_heads`, `vocab`,
  `furniture`). The full `OrtOCR` driver is step `160`.
