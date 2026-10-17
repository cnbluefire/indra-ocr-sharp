# 370 — The path flavour is the host's, not the port's

## Goal

`indra-ocr`'s paths are `type=Path` in the reference, so the reference — not the port — decides
how a path is spelled: `str(Path(s))`. That spelling is `pathlib`'s, and `pathlib` picks its flavour
from `os.name`. The port implemented the **Windows** flavour unconditionally, so on Linux every
path the port normalized came back with backslashes.

The first real Linux run found it: `ci.yml`'s `pack` job (`ubuntu-latest`, `build/pack.sh`) failed in
the package smoke, in the one check that hands `ModelFolder.Resolve` the folder it just resolved:

```
  ok   Download fetches nothing for a folder that is already complete
  FAIL the walk runs to the end: ModelError: the model folder
       \home\runner\.cache\huggingface\hub\models--datalab-to--indra-ocr\snapshots\v3.1.1
       lacks det.onnx, rec.onnx, meta.json
```

`ModelFolder.Resolve` resolves the Hub cache with `Path.Combine` (a POSIX path: `/home/runner/…`)
and returns it as it is; the *second* call hands that string back, and `PathHelper.ExpandUser` →
`Normalize` replaced every `/` with `\`. On POSIX a backslash is an ordinary character in a name, so
`\home\runner\.cache\...\v3.1.1/det.onnx` was one long file name that does not exist. Nothing was
downloaded wrongly; the folder was there and complete — the port simply could not name it any more.

It was not only the smoke. `--model-dir` and `INDRA_OCR_MODEL_DIR` go through `expanduser()`, so a
nested one — `--model-dir /srv/models`, say — became `\srv\models` and the folder was reported
missing; an input in a subdirectory (`indra-ocr scans/a.png` became `scans\a.png`), a nested `--out`
(the file went into a directory literally named `results\out`) and the JSON's `file` field were no
better. The Windows host was never wrong: there `str(Path(s))` *is* the backslash spelling, which is
why 83/83 of the alignment harness stayed green.

## What changed

`PathHelper` now has the flavour as an input, and the host's is the one `pathlib` would pick:

| | Windows (`PureWindowsPath`) | POSIX (`PurePosixPath`) |
|---|---|---|
| separator | `\` | `/` |
| altsep | `/` → `\` | none: `\` is a name character |
| anchor | drive (`C:`) or UNC share, or a root | a leading `/`, and `//` is a root of its own |
| empty / `.` components | dropped | dropped |
| `..`, a name's dots | kept | kept |
| `expanduser("~")` | `%USERPROFILE%` | `$HOME`, then the passwd entry |

- The host is chosen once — `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` is the
  `os.name` of the port (`Core` targets `netstandard2.0` too, so `OperatingSystem.IsWindows` is not
  available there). The Windows body of the parse is **unchanged**, character for character.
- The POSIX parse is new: root `/`, a doubled leading `//` kept as its own root and three or more
  slashes folded into one, exactly as `posixpath.splitroot` does (`//a` is `//a`, `///a` is `/a`).
- `Path(...) / name` joins with the flavour's separator (the right side still wins when it carries
  an anchor), and `Path.name` divides on the flavour's separator — so on POSIX the name of
  `a\b.txt` is `a\b.txt`, and its stem is `a\b`. (Step 375 replaced both with `pathlib`'s own
  structural rule — `docs/375-path-parity.md` — which spells ordinary paths the same way and the
  corners the way `pathlib` does.)
- `expanduser` reads the variable `os.path.expanduser` reads on that platform: `USERPROFILE` on
  Windows, `HOME` (and then `GetFolderPath(UserProfile)`, which is the passwd entry on POSIX) on
  POSIX. The result is normalized, and a home that is not set leaves the `~` alone.

`PathHelper` sits in Core (`ModelFolder` needs `expanduser()`) and both hosts share it, so one fix
covers the CLI, the viewer, the Hub cache and the packages.

## `.stem` is python 3.14's

`--out` names its files `path.stem + ext`, and 3.14 changed `Path.stem`: the stem has to keep at
least one non-dot character.

| name | ≤3.13 | 3.14 here | port before | port now |
|---|---|---|---|---|
| `scan.` | `scan.` | `scan` | `scan.` | `scan` |
| `scan..` | `scan..` | `scan.` | `scan..` | `scan.` |
| `scan..pdf` | `scan.` | `scan.` | `scan.` | `scan.` |
| `.gitignore` | `.gitignore` | `.gitignore` | `.gitignore` | `.gitignore` |

So an input named `scan.` writes `scan.md`, not `scan..md`. The alignment interpreter here is
3.14.7 (`tools/pyenv`), which is the version the port is aligned against (docs/050, docs/145 the
same way); on 3.13 and earlier the reference itself would write `scan..md`. A trailing dot is a
POSIX-only name — Windows drops it — which is exactly the kind of input this step is about.

## Verification

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings, 0 errors (both `net10.0` and `netstandard2.0`).
- `dotnet test src/Indra.Ocr.slnx` — **321 passed** (was 218: `PathHelperTests` adds 103, and two
  existing expectations changed). Every case in `PathHelperTests` is read off CPython
  3.14.7 — `str(PurePosixPath(s))`, `str(PureWindowsPath(s))`, `.stem`, `PurePath.__truediv__`,
  `Path(s).expanduser()` with a fake home — so the tables are the reference's answers, not the
  port's.
- The flavour is an internal parameter (`internal static Normalize(path, PathFlavour)`), so **both**
  flavours are checked on **either** host: the POSIX rules are verified on Windows here, and the
  Windows rules cannot drift when the suite only runs on Linux. The host's own choice is asserted
  with the framework's spelling (`Path.Combine("temp", "x")`) and with the home variable, so a wrong
  variable or a wrong separator fails the suite on the host that has it.
- Windows is byte-for-byte what it was: `cli_parity.py` **83/83** (the `out-*` cases write files),
  `parity_sweep.py` 24/24, `viewer_cli_parity.py` 20/20, `viewer_parity.py` 12 cases + `stderr`.
- The Linux side is verified where a Linux host exists: `ci.yml`'s `pack` job (this step's failure)
  and the release workflow's `publish` job, whose platform smoke runs the published binary.

## Known differences (documented, not reproduced)

- **`~user`**: `posixpath`/`ntpath` look a named user up (`pwd`, `HOMEDRIVE`+`HOMEPATH`); the port
  only expands a bare `~`, so `~user/…` stays a name. The reference then raises
  `RuntimeError("Could not determine home directory.")`; the port reports the folder it was given.
- ~~**A UNC path's corners**~~: this step recorded `str(PureWindowsPath(r"\\server\share"))` as
  `\\server\share\` (and `\\server` as `\\server`) while the port gave `\\server\share`/`\server`.
  Step 375 made the parse structural and now reproduces those spellings — and the degenerate ones
  around them — exactly; the deviation is gone (`docs/375-path-parity.md`).
