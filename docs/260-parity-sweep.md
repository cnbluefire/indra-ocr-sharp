# 260 — the end-to-end parity sweep

**Status:** done.

## Goal

The curated harnesses pin the command line on inputs they generate themselves: `cli_parity.py`'s
83 cases use synthetic pages (`temp/inputs`, `temp/pdf_samples`, `temp/cli_inputs`), chosen one
option at a time. `OrtOcrTests` pins the *pipeline* on the reference's own example pages, but no
harness ever ran the two **commands** over the material the reference ships.

This step is that sweep: every PNG under `resources/indra-src/assets` — the reference's demo
pages, its benchmark charts and its logo — through both commands, in all three output formats,
then all of them at once through the worker pool (parallel and serial) and through `--out`; and
two of those pages through the **viewer**'s API. Nothing in the port changed: the sweep is a
check, and it passed.

## The material

`resources/indra-src/assets` is externally downloaded material (not in git), so the sweep
discovers its PNGs by glob and skips itself when the folder is absent — a new asset joins the
sweep by itself.

| Asset | Lines out | What it puts on the table |
|---|---|---|
| `examples/chinese_essay.png` | 35 | Hanzi, and the CJK path: no space between joined lines, `rec_cjk` |
| `examples/multi_column.png` | 31 | Two columns: the reading order, `## Section-Header`, `\|Text` labels |
| `examples/table_pmc.png` | 73 | A table page: the `<table>`/`<tr>`/`<th>` HTML path |
| `examples/rotated_letter.png` | 21 | A turned page: the orientation model turns it upright first |
| `benchmarks/throughput.png` | 23 | A chart: a title and a caption, little else |
| `benchmarks/speed_score.png` | 22 | A second chart, different type |
| `datalab-logo.png` | 0 | A graphic with no text at all: an empty document (the trailing newline and nothing else), not an error |

## The sweep

`tools/scripts/test/parity_sweep.py` — the CLI side, 24 cases:

- **21 = 7 assets x `md`/`text`/`json`.** The three output paths are genuinely different: `md` is
  the assembled layout (headings, `|`-labelled blocks, tables), `text` is the plain reading order,
  `json` is the page the viewer's API also carries.
- **`pool-assets`** — all seven in one command: seven units is past `MIN_POOL_PAGES` (4), so this
  is the worker pool, with a real page per worker.
- **`pool-assets-cores1`** — the same seven with `--cores 1`, the serial fallback.
- **`out-assets`** — the same seven with `--out`, so seven files are written through the pool and
  compared as well as the streams.

`--dpi` is deliberately *not* an axis: an image file is already pixels, so the reference ignores
the flag for one. That is not an assumption — `rotated_letter.png` with and without `--dpi 100`
comes out byte-identical from the reference (only PDFs are rasterised at the requested scale, and
those have `cli_parity.py`'s cases).

`viewer_parity.py` — the viewer side, one new case:

- **`assets`** uploads `multi_column.png` and `chinese_essay.png` to `/upload` and asks for page 0
  of each. The viewer's JSON is its own encoding, not the CLI's `--format json`, so a real page —
  blocks, tokens, reading order — has to come back *spelled* the way the reference spells it. The
  fixture's inputs are small and synthetic; these two are the pages the reading order and the CJK
  recogniser exist for. `Case.skip_if` (new, mirroring `parity.Case`) skips the case when
  `resources/` is not downloaded.

## Result

| Harness | Before | After |
|---|---|---|
| `cli_parity.py` | 83/83 | 83/83 (unchanged) |
| `parity_sweep.py` | — | **24/24** |
| `viewer_parity.py` | 11/11 | **11/11** + the `assets` case, 12 lines with `stderr` |
| `viewer_cli_parity.py` | 20/20 | 20/20 (unchanged) |

**No failure was found.** Exit code, stdout, stderr and the written files match byte for byte on
all 24 CLI cases, and the two viewer pages come back identical, ids and timings aside. So the port
holds over real material — CJK, tables, two columns, a turned page, charts, an empty page — and
not just over the harness's own fixtures.

## Verification

```
dotnet build src\Indra.Ocr.slnx
tools\pyenv\Scripts\python.exe tools\scripts\test\parity_sweep.py            # 24/24
tools\pyenv\Scripts\python.exe tools\scripts\test\parity_sweep.py --list     # the matrix
tools\pyenv\Scripts\python.exe tools\scripts\test\viewer_parity.py           # 11 cases + stderr
tools\pyenv\Scripts\python.exe tools\scripts\test\cli_parity.py              # 83/83
tools\pyenv\Scripts\python.exe tools\scripts\test\viewer_cli_parity.py       # 20/20
```

The sweep is the slowest harness (the seven-asset pool cases run fourteen real pages, twice); run
it with `--jobs` as the others. It shares the CLI side with `cli_parity.py` — `reference_command`,
`port_command`, `ENV` and the timing `mask` are imported from it, so the two cannot drift apart —
and writes under `temp/parity_sweep`.
