# 385 — The model fetch: offline mode, the two timeouts, and the daily stamp

**Status:** done.

## Goal

Three things about the fetch behind `models.model_dir` (`docs/170-models.md`, `docs/320-async-io.md`):

1. **Honour `HF_HUB_OFFLINE`** (and the legacy `TRANSFORMERS_OFFLINE`), the way `huggingface_hub`
   does: no request leaves the process at all.
2. **Align the timeouts with the reference** — but as the reference really has them: a *per-request*
   and *per-read* budget, never a cap on a transfer. The port had one 30-minute total
   (`HttpClient.Timeout`), which cuts off a model that takes longer than that to arrive — the one
   thing the user asked not to have — and, before this step, no way to fail a stalled Hub early.
3. **`config.json`**: is it needed for inference, or only downloaded?

Plus the version bump to `0.3.2-alpha.2` (`src/Directory.Build.props`, `docs/340-nuget-packages.md`).

## What the reference actually does

`indra_ocr/models.py` is driven by `huggingface_hub` 2.2.0. Measured against the reference itself
(a copy of the real cache under a temporary `HF_HUB_CACHE`, `HF_ENDPOINT` pointing at a closed
port, each case in its own process because `huggingface_hub` freezes the environment at import):

| case | result | stamp |
|---|---|---|
| Hub unreachable, cache complete | the cached snapshot folder comes back (4.7 s of its own retries) | **touched** |
| `HF_HUB_OFFLINE=1`, cache complete | the cached snapshot folder comes back (0.5 s, no request) | **touched** |
| `HF_HUB_OFFLINE=1`, no cache | `ModelError`, see below | not touched |
| `HF_HUB_OFFLINE=0`, no cache | `ModelError`: `LocalEntryNotFoundError: Got: ConnectError: …` | not touched |
| `TRANSFORMERS_OFFLINE=1`, no cache | same as `HF_HUB_OFFLINE=1` | not touched |
| `HF_HUB_OFFLINE=true` / `on` | same as `1` | not touched |

The finding that mattered: **`_touch(stamp)` runs whenever `snapshot_download` returns**, and
`snapshot_download` *returns the cached folder* when it cannot reach the Hub — its own internal
fallback (`_snapshot_download.py` catches `ConnectError`/`TimeoutException`/`OfflineModeIsEnabled`
and falls through to the local folder, whose block is commented "internet connection is down … let's
look if we can find the appropriate folder in the cache"). So a run with no network keeps the cached
models **and goes quiet for a day**; the port used to keep the models and re-check on *every* start,
paying the connect delay each time. Only a failure that travels *out* of `snapshot_download` (a file
fetch that fails after the listing worked) skips the stamp — that is the `except` branch in
`models.py`, which is the port's `catch (ModelError)`.

> **This corrects step 250.** `docs/250-reference-tests.md` read `models.py` and concluded the
> opposite — that an unreachable Hub returns the cached snapshot *without* stamping, so the reference
> "keeps retrying" — and the port was changed to match that reading. Against the `huggingface_hub`
> that is installed here (2.2.0, what the reference runs with), the fallback *returns*, so `_touch`
> runs; the probe above is the measurement, and the port follows it from this step on. (The listing
> was read then too; the fallback block is the part of it that settles the question.)

The offline failure text is the reference's, verbatim (its `LocalEntryNotFoundError`, truncated to
200 characters by `models.py`, which is why it ends mid-quote):

```
could not download the models from Hugging Face (datalab-to/indra-ocr, v3.1.1):
LocalEntryNotFoundError: Cannot find an appropriate cached snapshot folder for the specified
revision on the local disk and outgoing traffic has been disabled. To enable repo look-ups and
downloads online, set 'HF_HUB_OFFLINE. Check the network connection, or point at a local model
folder with --model-dir or INDRA_OCR_MODEL_DIR.
```

(one line in reality; wrapped here for the page)

### The timeouts, as huggingface_hub has them

- `HF_HUB_ETAG_TIMEOUT` (`DEFAULT_ETAG_TIMEOUT`, 10 s) is the timeout of its **metadata** requests —
  `get_hf_file_metadata`, the `HEAD` that resolves a file's URL.
- `HF_HUB_DOWNLOAD_TIMEOUT` (`DEFAULT_DOWNLOAD_TIMEOUT`, 10 s) is the timeout of the **file GET**.
- Both are handed to httpx as a plain number, which httpx reads as *connect = read = write = pool =
  10 s*; its read timeout is an **idle** window per read, not a transfer limit.
- Its own tree listing (`repo_info`, `list_repo_tree` → `paginate`) passes **no** timeout at all.

So a download that takes a minute is normal and untouched, while a connection that never answers is
given up on in about ten seconds. Both variables are read the same way:
`_as_int(os.environ.get(name)) or DEFAULT`, i.e. unset, empty, `0` or unparsable is the default;
`HF_HUB_OFFLINE` is `_is_true(HF_HUB_OFFLINE or TRANSFORMERS_OFFLINE)` — true for `1`, `on`, `yes`,
`true` in any case, and **not** trimmed.

## The port

| file | what changed |
|---|---|
| `src/Indra.Ocr.Core/Onnx/ModelDownload.cs` | `OfflineMode`, `EtagSeconds`/`DownloadSeconds`/`Seconds`, `Budget`, `ReadAsync`, the offline branch, `CopyAsync`'s `idleSeconds`, `DownloadFailed(string, string)`, `Offline(...)`, `HttpClient.Timeout = InfiniteTimeSpan` |
| `src/Indra.Ocr.Core/Onnx/ModelFolder.cs` | the stamp is touched whenever the download returns; `Missing`'s note on `config.json` |
| `src/Indra.Ocr.Tests/ReferenceInputsTests.cs` | the daily-check case (stamp semantics), two offline cases, three timeout cases, `MiniHub` |
| `tools/scripts/test/cli_parity.py` | `offline-cache`, `offline-missing` |

- **Offline mode is checked before anything else**, before the folder is even created: with
  `HF_HUB_OFFLINE` set there is no listing, no body, no `HF_ENDPOINT` lookup. A folder (or cache
  snapshot) that holds the model set is returned as `false` — the same outcome as an unreachable
  Hub — and anything else raises the reference's message above. `ModelFolder` then touches the
  stamp on the `false` outcome, exactly as the reference's fallback does.
- **Two budgets, no third.** `HubFilesAsync` (the tree listing) runs under `HF_HUB_ETAG_TIMEOUT`;
  `FetchAsync` gives the file GET's headers `HF_HUB_DOWNLOAD_TIMEOUT` and then *disposes that
  budget before reading the body*, so the transfer itself is not capped, and `CopyAsync` reads each
  chunk under its own idle window of the same number. `HttpClient.Timeout` is
  `InfiniteTimeSpan`, because .NET's timeout is a total one — the exact thing that must not apply.
- **A budget that fires is "the Hub could not be reached".** The budgets ride on the same exception
  family a caller's `CancellationToken` does, so `Offline(...)` now accepts
  `OperationCanceledException` (not just `TaskCanceledException`) and keeps telling the two apart by
  the *caller's* token, which is what it did before: a cancelled token still travels out
  untouched.
- **`config.json` is not needed for inference and is not checked.** The reference requests it with
  the models because the Hub counts a repo's downloads by it (`models.MATH_FILES` aside, its
  `allow_patterns` are `*.onnx`, `meta.json`, `config.json`); nothing in the pipeline ever reads it.
  The port downloads it (so the Hub sees what the reference's Hub sees) and its completeness check
  stays `det.onnx`, `rec.onnx`, `meta.json` (+ the math set with `math`) — the reference's own
  `FILES[:2] + FILES[3:]`. `order.onnx` remains the one the reference's check skips.

## Verification

- **The reference, on this host** (`docs/320-async-io.md`'s probe, re-run for this step): the table
  above, including the stamp mtime changing after both the unreachable and the offline run.
- **The suite**: 385 tests, 0 failures — five new ones. `Cached_models_are_checked_once_a_day` now
  pins the corrected stamp rule (touched by a fallback, untouched by a failure that travels out).
  `Offline_mode_uses_the_cache_and_never_calls_the_hub` measures the offline promise: a *live*
  counting socket as `HF_ENDPOINT` and **zero** connections, plus the exact message, character for
  character (the test builds it from the reference's own text so the 200-character cut is checked
  too). `Offline_mode_is_the_references_environment_check` walks the two variables' truth table.
  `A_stalled_hub_is_given_up_on_after_the_etag_timeout` points `HF_ENDPOINT` at a socket that
  accepts and never answers, sets `HF_HUB_ETAG_TIMEOUT=1`, and requires the cached set back in well
  under the 30 minutes the old total timeout would have waited.
  `A_slow_download_is_not_capped_by_the_download_timeout` is the user's requirement itself: a
  400 KB body in ten pieces 400 ms apart — 3.6 s of transfer under `HF_HUB_DOWNLOAD_TIMEOUT=2` —
  must arrive complete and byte-identical. `A_stalled_body_ends_the_download_and_keeps_the_cache`
  is the other half (a body that stops arriving ends the download, the `.part` stays, the cache is
  kept and the stamp is not touched).
- **The CLI harness**: `offline-cache` and `offline-missing` run both sides with
  `HF_HUB_OFFLINE=1` and compare exit code, stdout **and stderr** — byte for byte, on a real
  conversion and on the offline failure. 85/85 with them.
- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors, both target frameworks.

## Deviations

- **The listing has a budget where the reference leaves it open.** `huggingface_hub` passes no
  timeout for `repo_info`/`paginate`, so a Hub that accepts a connection and then says nothing
  hangs the reference forever; the port gives that request `HF_HUB_ETAG_TIMEOUT`. It is the same
  number the reference uses for its own metadata request, and it is the only place the port is
  *stricter*.
- **A nonsense timeout value is the default here.** `int("abc")` raises in Python (at import, in
  `constants`) and httpx raises on a negative number; the port takes the default instead, so a typo
  in `HF_HUB_ETAG_TIMEOUT` cannot take the run down.
- **The exception name in a *connection* failure is still .NET's** (`docs/170-models.md`): the
  reference says `LocalEntryNotFoundError: Got: ConnectError: …`, the port says
  `HttpRequestException: …`. The prefix and the tail are the same. The offline message *is* the
  reference's own, name included, because no exception is behind it — the port never asked.
- **Retries are not reproduced.** The reference's httpx client retries a refused connection
  (measured: 4.7 s against a closed port); the port fails as soon as the budget runs out.
