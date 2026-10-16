# 310 — The model download's progress

## Goal

A first run has no models and fetches about 19 MB from Hugging Face; on a slow link that is a
minute of silence. The port says what it is doing, on **stderr**, one line at a time — the file
being fetched, its size, how much has arrived and the percentage — with no progress-bar animation,
so a redirected stream holds readable lines rather than a trail of carriage returns.

The downloader reports; the hosts print. Core has no idea what a line looks like, and a host that
wants the numbers for something else (a log, a page, a test) gets them without parsing text.

## The report

`Indra.Ocr.Onnx.ModelDownloadProgress` (Core) is what a host is told:

```csharp
public readonly record struct ModelDownloadProgress(string File, string Path, string Url,
                                                    long Downloaded, long Total);
```

`File` is the name in the repository (`det.onnx`), `Path` the local file it is written to, `Url`
what it is fetched from, `Downloaded` how many bytes have arrived, `Total` the size the Hub lists
(or 0 when it does not).

`ModelDownload.Download(..., Action<ModelDownloadProgress>? downloadProgress = null)` reports, and
`ModelFolder.Resolve(..., downloadProgress)` — the function both hosts call — passes it through.
The cadence is the downloader's:

- once before a file's body is read (`Downloaded == 0`: the file, where it comes from, where it
  goes);
- then at every tenth of the file, with how much has arrived;
- then once more at the end, when the body does not end on a report (a short file, or a body that is
  not the size the Hub listed).

A file that is already there at the right size is not downloaded and is not reported, so a cached
run says nothing at all. `null` (the default) reports nothing: the downloader prints nothing itself,
and every byte still lands in the folder.

## The lines

`ConsoleOutput` (the CLI project, which the viewer references — the reference's own `viewer.py`
imports from `cli.py`) turns a report into the host's text:

```
downloading det.onnx (389.9 KB)
  from https://huggingface.co/datalab-to/indra-ocr/resolve/v3.1.1/det.onnx
  to temp\hub-cache\models--datalab-to--indra-ocr\snapshots\v3.1.1\det.onnx
det.onnx: 50.8 KB / 389.9 KB (13%)
det.onnx: 100.0 KB / 389.9 KB (25%)
...
det.onnx: 389.9 KB / 389.9 KB (100%)
```

- The unit is chosen from the file's own size, so every line about one file reads in the same unit
  (`569 B`, `191.0 KB`, `9.3 MB`) — one decimal, as a progress bar scales.
- The percentage is `downloaded * 100 / total` truncated, which is what a `tqdm` bar shows; it is
  the honest share, so a coarse read size makes the steps read 13, 25, 39 rather than 10, 20, 30.
- The newlines are Python's (`\n`), as everywhere in `ConsoleOutput`; `WriteError` translates them
  to the platform's.

`Cli.Execute` and the viewer's `Engine.Ocr` pass `ConsoleOutput.WriteDownloadProgress` as the
callback. The viewer's banner stays on stdout, so the two streams stay apart: stdout is the
viewer's own output, stderr is progress and errors.

## How this relates to the reference

`models.model_dir` calls `snapshot_download`, whose `tqdm` bar is drawn by `huggingface_hub` itself:
its description is the file name, `unit_scale` the byte count, `disable=None` (so the bar appears
only on a terminal), and `HF_HUB_DISABLE_PROGRESS_BARS` turns it off. The port's lines carry the same
information without the animation, read the same variable — `ConsoleOutput.DownloadProgressCallback`
hands the host's callback, or nothing — and print whether or not stderr is a terminal, which is a
deliberate difference: a CI log is where a slow download is worth seeing. The harnesses never notice
either way, because they always pass `--model-dir resources/models` and so download nothing.

## Verification

- `dotnet test src/Indra.Ocr.slnx` — **216 passed** (207 before). New: the copy loop's cadence
  (`ModelDownloadTests`: every tenth, the end, a short file once, an unknown size at the end), the
  lines (`ConsoleOutputTests`: the first report, a later one, 100%, a small file, no size) and the
  reference's switch (`HF_HUB_DISABLE_PROGRESS_BARS`).
- A real first run, with a fresh `HF_HUB_CACHE` and no `--model-dir`:
  `indra-ocr temp/inputs/latin.png --max-pages 1 --format text` — the eight wanted files
  (`config.json`, `det.onnx`, `meta.json`, `order.onnx`, `orient.onnx`, `rec.onnx`, `rec_cjk.onnx`,
  `script.onnx`) each reported, exit 0, and the page's text on stdout. A second run over the same
  cache: **0 bytes on stderr**.
- The viewer, same conditions: the same lines on stderr, the banner on stdout, `/info` answering
  200; over a complete cache, **0 bytes on stderr**.
- `cli_parity.py` 83/83, `parity_sweep.py` 24/24, `viewer_cli_parity.py` 20/20,
  `viewer_parity.py` 12 cases + `stderr`.

## Files

- `src/Indra.Ocr.Core/Onnx/ModelDownloadProgress.cs` — the report.
- `src/Indra.Ocr.Core/Onnx/ModelDownload.cs` — the callback, `Fetch` and the copy loop that reports.
- `src/Indra.Ocr.Core/Onnx/ModelFolder.cs` — `Resolve`'s pass-through.
- `src/Indra.Ocr.Cli/ArgumentParsing/ConsoleOutput.cs` — the lines.
- `src/Indra.Ocr.Cli/Cli.cs`, `src/Indra.Ocr.Viewer/Engine.cs` — the two hosts' wiring.
- `src/Indra.Ocr.Tests/ModelDownloadTests.cs`, `src/Indra.Ocr.Tests/ConsoleOutputTests.cs` — the
  tests.
