# 320 — The I/O boundary is asynchronous (the pipeline is not)

## Goal

The question that started this step: inference runs on ONNX Runtime, so should the port's public
API be asynchronous? The answer is **no for the pipeline and yes for the two places that talk to
the network**. `ModelDownload` and `ModelFolder` gain an asynchronous implementation; every
algorithm that works on an image or a result stays synchronous, and the hosts keep calling the
synchronous wrappers.

## Why the pipeline stays synchronous

- **ONNX Runtime's .NET binding has no `RunAsync`.** `InferenceSession.Run` blocks the calling
  thread; there is nothing to await. Wrapping it in `Task.Run` would be *fake* async — it would
  hand the work to a pool thread and then have the caller await it — and would not make inference
  faster. It would also break the two places that already decide how the work is spread: the
  worker-process pool (`--cores`, one process per core) and the viewer's engine lock.
- **`GrayImage` is a non-owning `ref struct`.** It is a view over the caller's pixels (width,
  height, stride, data); a `ref struct` cannot cross an `await`, so an async pipeline would have to
  copy every frame. The whole point of the type is that it does not.
- The pipeline's own work is CPU-bound and deterministic. Async is for waiting, not for computing;
  a thread blocked in `Run` is exactly what the pool wants.

## What became asynchronous

`ModelDownload.DownloadAsync` and `ModelFolder.ResolveAsync` — the Hub listing
(`GET /api/models/{repo}/tree/{rev}?recursive=true`), every file body, and the writes to the
`.part` file. The directory checks, the daily stamp and the JSON parse are a handful of local
calls and stay synchronous; there is nothing to gain there.

The rules the two methods follow:

- **One implementation.** The asynchronous method *is* the implementation; the synchronous one is
  `GetAwaiter().GetResult()` over it. The two cannot drift apart. There is no `XxxAsync` mirror of
  the rest of the API, no `IAsyncEnumerable`, and no `Task.Run`.
- **`ConfigureAwait(false)` on every await**, so the wrapper cannot deadlock on a thread with a
  synchronisation context (the viewer is ASP.NET Core). Nothing awaits anything but I/O.
- **The progress callback stays synchronous** (`Action<ModelDownloadProgress>`, step 310) and is
  called on the thread doing the download; a host that wants to write a line does it inline.
- The `.part` file is kept and the next call overwrites it (step 170's rule); there is no resume.

## Cancellation is not "offline"

The download's catch filter used to be `HttpRequestException or TaskCanceledException or
IOException`, and `HttpClient` reports **both** a request timeout and a cancelled token as
`TaskCanceledException`. Left as it was, a cancelled download would have been mistaken for "the Hub
could not be reached" and the offline fallback would have quietly returned the folder's own model
set — a caller could not tell cancellation from success.

So the test moved into one place:

```csharp
private static bool Offline(Exception e, CancellationToken cancellationToken)
    => !cancellationToken.IsCancellationRequested
       && e is HttpRequestException or TaskCanceledException or IOException;
```

A cancelled token is the caller's own decision: `OperationCanceledException` travels out of
`DownloadAsync`, and out of `ResolveAsync` too — its `catch (ModelError)` does not catch it, so a
cached model set is not returned in its place. Everything else is unchanged: offline with a
complete folder returns `false` and keeps the models; offline with an incomplete folder throws
`ModelError`.

The pattern is `OperationCanceledException` since step `385` (which `TaskCanceledException` derives
from): the download's own request budgets — a body read over its idle window, say — ride the same
exception family as a caller's token, and it is the token, not the type, that must decide. A
cancelled token is still `IsCancellationRequested`, so it still travels out untouched
(`docs/385-model-fetch.md`).

A file that was interrupted is left as `<name>.part`; the next call overwrites it. Resuming would
mean trusting that the partial bytes are from the same revision the Hub now lists, and the Hub's
snapshot can move.

## The hosts

The CLI (`Cli.Execute`) and the viewer (`Engine.Ocr`) still call the synchronous
`ModelFolder.Resolve` — the wrapper. The download is a startup step in a run whose lifetime is one
document, so blocking there costs nothing and keeps both hosts free of an async state machine.
`Viewer`'s request path is the one place where an async download would earn something (a client
disconnect could stop it, via `RequestAborted`); that is step 330 if it is ever wanted.

## Verification

- Build clean, **0 warnings** (`inheritdoc` on the async overloads resolves).
- `dotnet test src/Indra.Ocr.slnx` — **218 passed** (216 before). New:
  `CopyAsync_stops_when_the_token_is_cancelled` (a body that never ends, cancelled mid-read) and
  `Cancellation_is_not_taken_for_offline` (a complete folder plus a cancelled token must throw, not
  return the cached set). The three copy-loop tests were retargeted to `CopyAsync`.
- The opt-in network test (`INDRA_OCR_TEST_DOWNLOAD=1`): the synchronous wrapper fills a folder, a
  second call downloads nothing, and then the asynchronous implementation fills a folder of its own
  with identical file sizes, a report per file (including the Hub's extra `config.json`) and the
  Hub's size on every report.
- A real CLI first run with a fresh `HF_HUB_CACHE` and no `--model-dir` — which goes through the
  wrapper and therefore the asynchronous implementation: the same eight files reported on stderr
  (87 lines), exit 0, the page's text on stdout. A second run over the same cache: **0 bytes on
  stderr**. A third with the daily stamp deleted (so the Hub really is contacted): **0 bytes on
  stderr** and the stamp written again.
- `cli_parity.py` 83/83, `parity_sweep.py` 24/24, `viewer_cli_parity.py` 20/20,
  `viewer_parity.py` 12 cases + `stderr`.

## Files

- `src/Indra.Ocr.Core/Onnx/ModelDownload.cs` — `DownloadAsync` (the implementation), `Download`
  (the wrapper), `HubFilesAsync`, `FetchAsync`, `CopyAsync`, `Offline`.
- `src/Indra.Ocr.Core/Onnx/ModelFolder.cs` — `ResolveAsync` and the `Resolve` wrapper.
- `src/Indra.Ocr.Tests/ModelDownloadTests.cs` — the copy-loop tests, cancellation, and the opt-in
  download over both paths.
