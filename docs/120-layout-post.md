# 120 — layout_post

**Status:** done.

## Goal

Port `indra_ocr/layout_post.py`: from the reading order, the same-block logits and the OrderNet
layout heads, build the page's blocks and its Markdown — section headers, paragraphs (with the
hyphen and CJK joins), list groups, HTML tables (both the geometric assembly and the one from the
table heads), display equations and the furniture rules. It also carries the helpers this module
owns: `balance_math` (a second pass over the recognizer's `$` spans), `join_lines` and Python's
`round`.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Layout/LayoutPost.cs` | `LayoutBlock`, `TableCell`, `BalanceMath`, `JoinLines`, `SplitBlocks`, `Rows`, `MergeWrapped`, `TableHtml`, `HeadsPlausible`, `TableHtmlHeads`, `PageBlocks`, `PageMarkdown` |
| `src/Indra.Ocr.Core/Numerics/Numerics.cs` | `RoundHalfEven` (Python's `round`) |
| `src/Indra.Ocr.Core/Text/UnicodeText.cs` | `OtherLowercase`, `IsLowerCodePoint`, `IsAlphaCodePoint`, `Strip` (Python's `str.strip`) |
| `src/Indra.Ocr.Core/Order/OrderPost.cs` | `LineFeatures` now strips with Python's whitespace set |
| `tools/scripts/test/gen_layout_fixtures.py` | `fixtures/layout_post.json` + the extra predicate ranges |
| `src/Indra.Ocr.Tests/LayoutPostTests.cs` | the parity tests (14) |

## Shape

```
blocks = split_blocks(order, same)               # a new block where same[prev, i] <= 0
for blk in blocks:
    cls = cls_logits[blk].sum(0).argmax()        # float32 column sum, first max
    Page-Header / Page-Footer, not keep_furniture -> dropped
    every line a $...$ formula                   -> "$$" lines, joined with "\n"
    Section-Header                               -> "## " + join_lines
    Table (heads plausible, tab given)           -> table_html_heads
    Table (more than one line)                   -> table_html over the cells
    List-Group                                   -> one line per _ITEM item
    otherwise                                    -> join_lines
"\n\n".join(non-empty parts)
```

`page_blocks` is the same walk without the rendering: the class name, the union box of the block's
lines rounded with Python's `round(x, 1)` and the lines newline-joined.

## Port notes

- **Python `round(x, digits)` is not `Math.Round`.** `Math.Round(0.15, 1)` is `0.2` because it
  scales by `10**digits` first and `0.15 * 10` lands exactly on `1.5`; Python rounds the *decimal*
  value of the double (`0.15` is really `0.1499…`), so `round(0.15, 1) == 0.1`.
  `Numerics.RoundHalfEven` reproduces Python: it decomposes the double into `mant * 2**exp`, forms
  the exact rational `|value| * 10**digits`, rounds that to an integer with ties to even and
  divides back (exact IEEE division while both operands are exact, else the exact decimal string
  parsed by .NET — which is correctly rounded too). Validated bit-exactly against Python's `round`
  over 3 665 fixture cases (random doubles, float32-derived doubles, `-0.0`, denormals, `1e308`,
  `±inf`, `nan`, digits 0…4) and a separate 425 110-value probe.
- **Python's `str.strip()` is not `Trim()`** for `0x1C`–`0x1F`; `UnicodeText.Strip` uses the
  Python whitespace set. Every code point is checked here (the `space` ranges of
  `unicode_predicates.json`), and `OrderPost.LineFeatures` now uses it.
- **Python's `islower()`/`isalpha()`** are `Ll`/`L*` *plus* the `Other_Lowercase` property
  (`UnicodeText.OtherLowercase`, 28 ranges); they drive the hyphen join.
- **numpy float32 scalar arithmetic stays float32** (NEP 50: a Python float is the *weak* operand,
  and comparisons are promoted the same way). So `bands` (the median of a list of `np.float32`
  scalars), the band midpoints, the `min(x1, bands[c, 1]) - max(x0, bands[c, 0])` span test, the
  `abs(cy[i] - mean) < 0.5 * h` row test and the `cb[c, 0] - h <= centers[...] <= cb[c, 2] + h`
  colspan test all stay in `float`; only the values the reference wraps in `float(...)` are
  `double`. Confirmed by probe (`temp/probe_layout_types.py`).
- **Python's `sorted(...)` is stable.** The three sorts the reference makes with it (rows by `x0`,
  cells by `(y0, x0)`, clusters by their top edge) are reproduced with the input *position* as the
  tiebreak, since `Array.Sort` is unstable.
- **`np.argsort(cy)` in `_rows` is the one numpy sort here**, and it inherits the documented
  deviation (`docs/110-order-post.md`): the port uses a stable argsort. The generator replays every
  table/page case with a stable argsort: **0 of 38 outputs change**, so no fixture depends on the
  tie order.
- **The regexes are hand-composed** to keep Python's semantics: `_PLAIN_WORD` is matched as ASCII
  letters plus Python's `\w` word rule (`isalnum()` or `_`, which is not .NET's `\w` — it excludes
  combining marks), `\s` is `UnicodeText.IsSpaceCodePoint`, `\d` is Python's decimal digits
  (`\p{Nd}`), `_ITEM`'s marker alternatives are tried in order, `_WHOLE_MATH` is a direct check.
- **`html.escape(text, quote=False)`** is `&`, `<`, `>` in that order (attributes are not escaped).
- **`_cluster` is reused** from `OrderPost` for the table heads; `tab[2]` (the cell head) selects
  the cells, the heads decide plausibility (a cell whose lines do not overlap horizontally in more
  than 10 % of the cells falls back to the geometric table).
- **`cells`** is `IReadOnlyDictionary<int, IReadOnlyList<TableCell>>` — the line → cells map that
  step `130`'s `assemble` builds — and `tab` is the optional `[3, n, n]` head slice.
- **The display-equation path** is `all(_WHOLE_MATH.match(t.strip()))`, i.e. an inner slice of code
  points `[1:-1]` — equal to the code-unit slice because the first and last code points are `$`.

## Verification

- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **95 passed** (was 81; +14 `LayoutPostTests`), 15 s.
- `fixtures/layout_post.json` (477 KB) holds 3 665 `round`, 21 `join_lines`, 25 `balance_math`,
  8 `split_blocks`, 10 `rows`, 5 `merge_wrapped`, 8 `table_html`, 11 `heads_plausible`,
  11 `table_html_heads` and 30 `page_markdown` cases. All expectations are the reference's own
  output, all arrays are raw little-endian bytes and the `round` corpus carries raw bit patterns,
  so the tests compare bit patterns.
- The page cases are synthetic pages that reach every block class (`classes`, with the
  `keep_furniture` and `cells` and table-head variants), the list markers (`list_markers`), the
  display-equation path (`whole_math`, `mixed_math`), an empty page and far-apart lines in one
  block — plus the real pages of `recognize.json` (`latin`, `cjk`, `two_columns`, `mixed`, `widths`
  n=18, `canvas` n=24, `handmade` n=4) driven through the real `OrtOCR.reading_order` and the real
  `order.onnx`, with and without `keep_furniture` and through the model's table heads.
- `unicode_predicates.json` gained the `lower` (2 569 code points) and `alpha` (141 028) ranges, and
  the existing all-code-points predicate test now also checks `IsLowerCodePoint`,
  `IsAlphaCodePoint` and `Strip`.

## Deviations

- **`np.argsort(cy)` tie order in `_rows`** — as in `docs/110-order-post.md`; measured at 0 of 38
  table/page outputs, so no fixture depends on it.
- **`RoundHalfEven` beyond the exact fast path**: for `|value * 10**digits| >= 2**53` (never a page
  coordinate) the result goes through the exact decimal string, and for negative `digits` the
  final multiply is a double multiply (the reference has no negative-`digits` call).
- **Degenerate tables** (`table_html` with no lines) raise in the reference (`max()` of an empty
  sequence); the port cannot be reached with an empty table from `page_markdown`, which is where
  the reference's own call sites are.
