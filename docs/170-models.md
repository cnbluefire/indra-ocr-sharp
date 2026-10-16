# 170 — models (the download)

**Status:** done.

## Goal

Port the download half of `indra_ocr/models.py`: fetch the model set from the Hugging Face repo
into a folder, with the same file selection (`*.onnx`, `meta.json`, `config.json`, the math models
only when `math`), the same "keep what is already there" behaviour and the same failure message.
Plus `ModelError`, the exception the CLI prints and turns into exit code 1.

## Files

| File | Contents |
|---|---|
| `src/Indra.Ocr.Core/Onnx/ModelDownload.cs` | `HfRepo`, `HfRevision`, `Files`, `MathFiles`, `Wanted`, `Matches`, `HasModels`, `Download` |
| `src/Indra.Ocr.Core/Onnx/ModelError.cs` | `ModelError` |
| `src/Indra.Ocr.Tests/ModelDownloadTests.cs` | the pattern/selection tests + the opt-in download test |

## Shape

```
# the reference's model_dir
path = arg or INDRA_OCR_MODEL_DIR
if path:                      # a local folder
  missing = [f for f in FILES[:2] + FILES[3:] if not (path / f).exists()]
  if missing: raise ModelError(f"the model folder {path} lacks {', '.join(missing)}")
  return path
cached = snapshot_download(repo, revision, local_files_only=True, **patterns)   # or None
if cached and age(stamp) < CHECK_EVERY_S: return cached
try: p = snapshot_download(repo, revision, **patterns); touch(stamp); return p
except Exception as e:
  if cached: return cached            # offline: keep the cached models
  raise ModelError(f"could not download the models from Hugging Face ({repo}, {rev}): "
                   f"{type(e).__name__}: {reason[:200]}. Check the network connection, or "
                   "point at a local model folder with --model-dir or INDRA_OCR_MODEL_DIR.")

# the port
ModelDownload.Download(folder, math, revision)
  files = HubFiles(revision, math)     # the Hub's tree API, filtered by Wanted
  for each: skip when it is there at the Hub's size, else fetch to <file>.part then move
  on a Hub failure: return when HasModels(folder, math), else ModelError(the wording above)
```

## Port notes

- **The download is decoupled from the model folder.** The reference's `model_dir` does two
  things: it *chooses* a folder (argument → `INDRA_OCR_MODEL_DIR` → the Hub cache) and it
  *downloads* into it. Here `OrtOcr`'s constructor already takes the folder — that is the
  "model dir" feature — and `ModelDownload.Download` is one self-contained operation: a folder
  in, the model files in it. It never learns where the folder came from or who reads it, and the
  lookup order, the cache layout and the daily check stay with the caller (the CLI, step `180`).
- **`ModelError` is the download's own failure type**, so a network failure surfaces with the
  reference's wording from the download itself; the "the model folder X lacks ..." message is the
  caller's, where the folder is chosen.
- **The file selection is the reference's patterns.** The Hub's tree API
  (`/api/models/{repo}/tree/{rev}?recursive=true`) lists the files with their sizes;
  `Wanted(path, math)` keeps `*.onnx`, `meta.json`, `config.json` and — only with `math` — the
  math files. The reference's `allow_patterns`/`ignore_patterns` pair is reproduced as one
  predicate: the math files are in the allow list (`*.onnx` matches four of them) and in the
  ignore list when `math` is off, which is the same thing as "keep them only with `math`".
- **`fnmatch`, not a glob.** Python's `fnmatch` lets `*` cross a `/`, so `*.onnx` would match
  `assets/x.onnx`; `Matches` implements those semantics (`*` and `?`, with backtracking) rather
  than `FileSystemName`'s path-aware matching. The reference's patterns only use `*`.
- **A file that is already there at the Hub's size is left alone.** `huggingface_hub` compares
  the blob's stored etag (a sha256 for LFS files); the port compares the size the tree API
  reports, which makes a second call cheap and completes a partially-filled folder without
  re-hashing 24 MB.
- **Offline keeps the models you have.** When the Hub cannot be reached, `Download` returns
  quietly if the folder already holds the model set (`HasModels`: `det.onnx`, `rec.onnx`,
  `order.onnx`, `meta.json`), exactly like the reference's "offline or the Hub is down: keep the
  cached models"; otherwise it raises `ModelError` with the reference's message (the first line
  of the cause, truncated to 200 characters).
- **`HF_TOKEN` is honoured.** The repo is public, so no token is needed; when the environment
  has one it is sent as `Authorization: Bearer`, as `huggingface_hub` does.
- **`Files`/`MathFiles` are the reference's tuples.** `Files` is `models.FILES` (used for the
  "is it complete" checks); the reference's *missing-file* check is `FILES[:2] + FILES[3:]`,
  which never looks at `order.onnx` — a quirk the CLI's check reproduces (step `180`).

## Verification

- `ModelDownloadTests` (5) passes: the pattern/selection predicates (`Wanted`, `Matches`),
  the constant sets, `HasModels` with and without `math`.
- `Download_fills_a_folder_with_the_model_set` is opt-in (`INDRA_OCR_TEST_DOWNLOAD=1`) so the
  suite stays offline; run against the real Hub it downloads the set, checks that the math
  models were **not** fetched, and that a second call downloads nothing. The folder it produced
  (`%TEMP%\indra-ocr-model-download`, 18.2 MB) is **byte-identical** to `resources/models` —
  all 8 files, sha256-compared — which is the strongest available check that the port fetches
  the right revision.
- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.

## Deviations

- **No progress bar.** `snapshot_download` draws a `tqdm` bar on stderr; the port downloads
  silently. Only a first run (or a run without a local folder) shows it, and the bar's rendering
  is not reproducible anyway.
- **The cache layout is a flat folder.** `snapshot_download` returns a
  `models--repo/snapshots/<sha>` tree of blobs and symlinks; the port writes the files straight
  into the folder the caller named. The *path* it returns is not part of the reference's CLI
  output, and the folder contents are what matters.
- **The exception type in the message is .NET's.** The reference prints
  `type(e).__name__` (`LocalEntryNotFoundError`, `ConnectionError`, …); the port prints the .NET
  exception's name (`HttpRequestException`, `TaskCanceledException`, …). The rest of the message
  is the reference's wording.
- **No `huggingface_hub` cache reuse.** A folder the reference's own `huggingface_hub` populated
  is not read by the port (and vice versa); each keeps to its own folder.
