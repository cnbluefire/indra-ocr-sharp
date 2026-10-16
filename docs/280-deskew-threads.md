# 280 — The deskew search's threads (and why ReadyToRun is not the answer)

## Goal

Close the remaining gap between the port and the reference on a single page, and answer the
question "would ReadyToRun help?" with measurements rather than a guess.

The starting point (step `260`) was: the port is byte-identical but slower — `latin.png`
599 ms against the reference's 419 ms, `multi_column.png` 1876 against 1416,
`table_pmc.png` 1665 against 1243 (Release, the CLI's `--timings` page number). A probe run
(`temp/prof`, Release) showed where it sat: `EstimateSkew` 174 ms of the page, against the
reference's 42 ms — the only stage that was still off by more than noise (detection,
recognition, reading order and assembly were already at 1:1, recognition slightly ahead).

The reason is not the algorithm but the threads: the reference's `parallel.py:46` calls
`cv2.setNumThreads(load_kwargs["threads"])`, so OpenCV spreads each of `estimate_skew`'s 32
`warpAffine`s over the same 8 threads the process was told to use (`--cores`, `0` = all of
them), while a pool worker sets 1. The port's `WarpAffine32f` is a scalar managed loop and was
running the 32 angles one after another on one core.

## The change

The angles are independent, so the port scores them in parallel instead of vectorising the
warp:

- `Orientation.EstimateSkew(GrayImage gray, int side = DeskewSide, int threads = 1)` — the new
  `threads` is the reference's `cv2.setNumThreads`.
- `Orientation.BestAngle(ink, width, height, angles, threads)` — fills a `float[]` of scores
  (`Parallel.For` with `MaxDegreeOfParallelism = threads` when `threads > 1` and there is more
  than one angle, the plain loop otherwise) and only then takes the first maximum, in index
  order.
- `Orientation.Upright(..., int threads = 1)` — both overloads pass it on to `EstimateSkew`.
- `OrtOcr` keeps the `threads` it was constructed with (it was only used for the
  `SessionOptions` before) and hands it to `Orient.Upright`.

The thread count each host gives it is the one it already resolved, so it is the reference's
own:

| host | threads | reference equivalent |
|---|---|---|
| CLI, one process | `--cores` (`0` → `Environment.ProcessorCount`) | `cv2.setNumThreads(--cores)` in `_serial` |
| CLI, pool worker | `ThreadsPerWorker` = 1 | `cv2.setNumThreads(1)` in `_init` |
| viewer | `--threads` (default 4) | *(OpenCV keeps its own default there: `viewer.py` sets `OMP_THREAD_LIMIT`, not `cv2`)* |

The viewer row is the one place the port differs from the reference in *resource* use: it uses
`--threads` where the reference's viewer leaves OpenCV at its default. Output is unaffected
(the deskew result is bit-exact either way); the count is a timing-only choice.

## Why the output cannot change

- One angle's `Sharpness` reads the thumbnail and writes only the buffer it allocated itself;
  nothing is shared between angles, so every score is the number the serial order produces, bit
  for bit, whatever order the threads compute them in.
- The winner is still the first maximum in index order — Python's
  `max(angles, key=sharpness)` — because the values are collected first and scanned
  sequentially afterwards.
- The reference's `cv2.warpAffine` splits *one* warp across threads; the port splits the *angle
  list*. Both end up with `threads` workers on the same 32 independent warps, and the
  per-angle values do not depend on the split.

Verified byte-identical: 203 tests, `cli_parity` 83/83, `parity_sweep` 24/24 (including
`pool-assets-cores1`, the one-worker pool where the deskew must stay serial),
`viewer_cli_parity` 20/20, `viewer_parity` 12/12.

## Results

Isolated (Release, `temp/prof`, a 900×700 page, deskew side 800):

| | 1 thread | 2 | 4 | 8 |
|---|---|---|---|---|
| `EstimateSkew` | 167.7 ms | 101.8 ms | 62.0 ms | **50.9 ms** |
| reference `estimate_skew` | 73.5 ms | | | 41.9 ms |

3.3× on 8 threads (memory-bound, so not 8×), and `Upright` (768 orient side + deskew) goes
180.9 → 68.3 ms. The reference's 41.9 ms is now 1.2× away instead of 4×.

Per stage, in process, warm (Release viewer + `temp/timings_probe.py`, `orient_ms` / page
total):

| page | port before | port after | reference |
|---|---|---|---|
| `latin.png` | orient 236, total 338–410 | orient 93–123, total 157–321 | orient 64–81, total 170–210 |
| `multi_column.png` | orient 209–247, total 1052–1438 | orient 98–126, total 880–1189 | orient 85–139, total 1056–1343 |

End to end, the CLI's own page number (Release, best of 3–4 fresh processes):

| page | before | after | reference |
|---|---|---|---|
| `latin.png` | 599 ms | 481 ms | 419–435 ms |
| `multi_column.png` | 1876 ms | 1649 ms | 1416 ms |
| `table_pmc.png` | 1665 ms | 1455 ms | 1243 ms |

And the whole process (`temp/bench_cli.py`, best of 4): `latin.png` 1523–1548 ms against the
reference's 1637–1687 ms — the port is now ahead of the reference once its startup is counted,
because its startup floor is lower (`--version`: 121 ms against the reference's 407 ms).

The Debug build, which is what the harnesses and a developer run, gains much more (its deskew
was JIT-unoptimised): `latin.png` 2202 → 994 ms, `multi_column.png` 3635 → 2161 ms.

## ReadyToRun and tiering (measured, not adopted)

`dotnet publish -c Release -r win-x64 --self-contained false` into `temp/pub-*`, benchmarked
interleaved (`temp/bench_variants.py`, 3 rounds, best of each, Release):

| variant | `latin.png` wall / page | `multi_column.png` wall / page |
|---|---|---|
| plain (tiered compilation on) | **1523 / 517 ms** | **2744 / 1704 ms** |
| `TieredCompilation=false` | 1848 / 616 ms | 2910 / 1705 ms |
| `PublishReadyToRun=true` | 1647 / 630 ms | 2784 / 1822 ms |
| R2R + `TieredCompilation=false` | 1660 / 731 ms | 2817 / 1795 ms |
| R2R composite | 1593 / 679 ms | — |

ReadyToRun loses, in both its plain and composite form, and so does turning tiering off. The
reason is where the time is not: the runtime and the assemblies cost 121 ms of a 1523 ms run,
so there is almost no JIT/startup left for R2R to remove — and what is left (the page) is
compute-bound, where the tier-1 JIT's code beats crossgen2's R2R image. The ~0.9 s between the
runtime and the page is ONNX Runtime's own work (reading the six graphs, 17 MB in all, and
building their sessions with the reference's `ORT_ENABLE_ALL`), which no compiler setting
touches; the reference pays it too (its startup is ~1.24 s, of which `--version` alone is
407 ms).

So the shipped configuration stays the default Release publish. This section is here so nobody
tries R2R again: the answer is no, and it costs a page time to find out.

## Verification

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings, 0 errors (Debug and Release).
- `dotnet test src/Indra.Ocr.slnx` — **203 passed**.
- `tools/scripts/test/cli_parity.py` — 83/83, byte-identical.
- `tools/scripts/test/parity_sweep.py` — 24/24, byte-identical.
- `tools/scripts/test/viewer_cli_parity.py` — 20/20.
- `tools/scripts/test/viewer_parity.py` — 12 cases plus `stderr`.
- Measurements: `temp/prof` (Release probe), `temp/bench_cli.py`, `temp/bench_variants.py`,
  `temp/bench_startup.py`, `temp/timings_probe.py` (all under `temp/`, not committed).

## Files

- `src/Indra.Ocr.Core/Pipeline/Orientation.cs` — `EstimateSkew` / `BestAngle` / `Upright`.
- `src/Indra.Ocr.Core/Pipeline/OrtOcr.cs` — keeps `threads`, passes it to `Upright`.
- No host change: the CLI and the viewer already had the number.
