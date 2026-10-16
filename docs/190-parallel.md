# 190 — convert_many (the worker-process pool)

**Status:** done.

## Goal

Port the rest of `indra_ocr/parallel.py`: `convert_many`'s decision (serial or pooled), the
pool itself — one worker process per core, one page per task, results in input order — and the
fallback to this process when the workers stop. ONNX Runtime's own threads barely speed up one
page (rendering, the text layer and the layout run on one core), and pages are independent, so
one single-threaded process per core uses the machine far better; the workers are processes, not
threads, because pdfium is not thread-safe.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Cli/Worker/WorkerProtocol.cs` | `WorkUnit`, `LoadOptions`, `WorkResult`, the wire format |
| `src/Indra.Ocr.Cli/Worker/WorkerHost.cs` | the worker: `_init` + `_run` (its own converter, `_doc` kept open) |
| `src/Indra.Ocr.Cli/Worker/WorkerPool.cs` | the parent: the processes, the dispatch, the results by index, `BrokenPoolException` |
| `src/Indra.Ocr.Cli/Cli.cs` | `ConvertMany`, `Serial`, `Pooled`, `MaxWorkersForMemory`, the constants |
| `src/Indra.Ocr.Cli/Program.cs` | the worker dispatch (`WorkerHost.IsWorker`) |
| `src/Indra.Ocr.Tests/WorkerProtocolTests.cs` | the wire format, unit by unit in one stream |
| `tools/scripts/test/cli_parity.py` | the `pool-*` cases, against the reference |

## Shape

```
convert_many(paths, cores, max_pages, password, **load_kwargs):
  load_kwargs["path"] = model_dir(load_kwargs.get("path"), math=...)   # once, not per worker
  cores  = max(1, cores or usable_cores())
  plan   = _plan(paths, max_pages, password)          # per input: its units, or an InputError
  n_pages = sum(len(u) for u in plan if isinstance(u, list))
  workers = min(cores // THREADS_PER_WORKER, n_pages, _max_workers_for_memory())
  if workers < 2 or n_pages < MIN_POOL_PAGES:
      _serial(paths, plan, max_pages, password, cores, load_kwargs)
  else:
      _pooled(paths, plan, workers, max_pages, password, cores, load_kwargs)

_serial:  conv = load(threads=cores, **load_kwargs)      # one process, all cores per page
          per input: the InputError, or list(conv.convert(path, max_pages, password))

_pooled:  threads = THREADS_PER_WORKER                   # 1
          OMP/OPENBLAS/MKL_NUM_THREADS = threads while the workers start
          ex = ProcessPoolExecutor(workers, spawn, initializer=_init, initargs=(kwargs, threads))
          results = ex.map(_run, flat)                   # every unit, in submission order
          per input: consume its units in order; the first InputError wins, else the pages
          on BrokenProcessPool: warn, then _serial for the inputs not yet yielded
          finally: ex.shutdown(wait=False, cancel_futures=True)
```

## Port notes

- **A worker is this same program.** `ProcessPoolExecutor`'s spawn context pickles
  `_init`/`_run` and re-imports the caller's main module — which is why the reference's warning
  mentions `if __name__ == "__main__":`. The port starts `indra-ocr` again with
  `INDRA_OCR_WORKER=1` in its environment (`Program.Main` checks it before anything else), so
  the worker needs no importable entry point and no pickle: it reads its `load_kwargs` from
  stdin and answers units on stdout. The environment variable is invisible to the command line
  (`--help` is untouched).
- **The wire format is private to the two ends.** `WorkerProtocol` writes length-prefixed UTF-8
  strings and little-endian doubles with `BinaryWriter`/`BinaryReader`: text of any script and
  the exact bits of the boxes, confidences, skew and timings round-trip without escaping. A
  result is `[(index, Page)]` — the page's lines, timings, markdown, blocks, rotation, skew,
  source and note — or the `InputError`'s message, rebuilt as an `InputError` on the parent's
  side. Where a field is optional (a PDF's password, a page's markdown) the writer *always*
  writes the value and the reader *always* reads it; the flag beside it only says whether the
  value is meaningful. Both bugs this format had were the other way round — the writer writing a
  value the reader skipped when the flag was false — which left the stream a byte out, so a
  worker's second PDF page read garbage (and the pool hung on the page after that). One unit per
  worker never noticed; `Units_round_trip_in_a_stream`, `Results_round_trip_in_a_stream` and the
  "the reader consumed the whole stream" assertion are the regression tests.
- **Dispatch is `ex.map`.** One task per worker (`Pump`) takes the next unit from a shared
  cursor (`Interlocked.Increment`), writes it, reads its own result and publishes it by unit
  index under a lock, pulsing the waiter. The caller's `foreach` waits on `Result(index)` for
  the unit it needs, so the results are consumed in submission order while the workers compute
  out of order. Because each task drains its own worker's stdout, no pipe can fill up behind a
  computing worker, so the parent never deadlocks on a full pipe.
- **The parent hands the plan to the pool.** `Pooled` flattens the plan's units in input order
  (`flat`), which is the order `Result(index)` is consumed in; an input that is an `InputError`
  in the plan yields it without asking a worker, and an input whose worker reports an error
  yields the *first* error, discarding that input's pages — exactly the reference's
  `error = error or r`.
- **`_doc` is kept open across units.** A worker keeps the last PDF open and re-opens only when
  the path changes (the port disposes the old document; the reference lets it go), which is the
  whole point of the pool: one document open, one page per request.
- **The fallback.** `BrokenPoolException` (a dead or unreachable worker) is caught around the
  per-input consume, and the inputs not yet yielded are read by `Serial` with `cores` threads.
  A pool that cannot start at all fails the same way.
- **`threads`.** The workers get `THREADS_PER_WORKER` (1); the serial path gets `cores`, which
  is `max(1, cores or usable_cores())` — note `--cores -1` is truthy in Python, so it becomes
  `max(1, -1)` = 1, not `usable_cores()`. `OMP_NUM_THREADS`, `OPENBLAS_NUM_THREADS` and
  `MKL_NUM_THREADS` are set to the worker's share while the workers start and restored right
  after (the libraries read them at import). `cv2.setNumThreads` has no counterpart: the port's
  imaging is managed code with no thread pool.
- **The threshold.** `MIN_POOL_PAGES = 4`: fewer pages than that in total, or fewer than two
  workers, and everything is read in one process with all the cores on each page. So a single
  image, a one-page PDF or a two-page PDF never starts a pool.

## Verification

- `tools/scripts/test/cli_parity.py` gained the pool cases, all byte-identical to the reference
  (exit code, stdout, stderr and the `--out` files; timings masked):
  - `pool-images` (six image inputs), `pool-pdf` (a six-page PDF the harness builds with fpdf2),
    `pool-pdf-json`, `pool-pdf-text`, `pool-pdf-mode`, `pool-pdf-max`;
  - `pool-mixed` (a six-page PDF, an image, a missing file, a locked PDF, a two-page PDF and a
    truncated PNG — the plan's errors, a worker's error and the pool's results in one run);
  - `pool-cores2` (two workers over six pages — a worker's *second* unit, the case the protocol
    bug broke), `pool-cores1` (one worker: serial), `pool-small` (three pages: serial),
    `pool-timings`, `out-pool`.
  The harness now runs **77 cases**; every one matches.
- The three PDFium-driving test classes (`PdfTests`, `TextLayerTests`, `DocumentTests`) share
  one xUnit collection, so they no longer render at the same time. PDFium is not thread-safe —
  the reason the pool uses processes — and two of them running in parallel made a render and a
  conversion comparison fail once in a while. Everything else still runs in parallel.
- `WorkerProtocolTests` (4) round-trips the config, a stream of units (password / none / none /
  image / image / empty password / negative cap), pages with lines, blocks, timings, markdown
  (and a page without any), rotation, skew and non-ASCII text, and a stream of results including
  an `InputError`; each stream asserts that the reader consumed exactly what the writer wrote.
- `dotnet test src\Indra.Ocr.slnx` → **147/147**; `dotnet build src\Indra.Ocr.slnx` → 0 warnings,
  0 errors.

## Deviations

- **The broken-pool warning is one line.** The reference goes through `warnings.warn(…,
  RuntimeWarning, stacklevel=2)`, so stderr holds the *source location* of `parallel.py` and a
  `warnings.warn(` line under it (and Python's once-per-location filter may swallow repeats).
  The port prints `RuntimeWarning: <the same message>` and nothing else. In practice a
  self-exec worker cannot hit the spawn-import failure the message describes; only a worker that
  dies does, and the wording is kept for parity.
- **`_max_workers_for_memory` on Windows is `1 << 30`.** The reference calls `os.sysconf`, which
  Windows Python does not have, and falls back to `1 << 30` — i.e. memory is never the limit
  there. The port keeps that. On Unix it uses the GC's total available memory (the container's
  limit, where `os.sysconf` reports the machine's physical pages) divided by `WORKER_MEMORY`;
  the count only ever changes how the work is spread, never the output.
- **`usable_cores()` is `Environment.ProcessorCount`** (see `docs/180-cli.md`), so on this host
  the pool is `min(8, n_pages)` workers like the reference's.
