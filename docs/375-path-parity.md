# 375 — `PathHelper` is `pathlib`'s rules, not an approximation of them

## Goal

Step 370 gave `PathHelper` the right *flavour* (`docs/370-path-flavour.md`). It did not give it the
right *rules*: the port spelled paths by string surgery that agrees with `pathlib` on every ordinary
path and quietly drifts on the corners. A fuzz sweep against CPython's own answers found **1270
distinct divergences** over 15 000 path-shaped words — none of them reachable from the port's four
call sites with a realistic path, all of them wrong.

The reference does not spell paths by string surgery. `pathlib` parses first and prints afterwards,
and its parse is structural:

| reference | rule |
|---|---|
| `_parse_path(s)` | `(drive, root, parts)` — `splitroot` for `drive`/`root`, then `parts = tail.split(sep)` |
| `_format_parsed_parts` | `drive + root + sep.join(parts)`, or `"."` when all three are empty |
| `Path.name` / `.stem` | read the last **part** (so a path with no part has no name and no stem) |
| `PurePath.__truediv__` | `ntpath.join`/`posixpath.join` over the **raw** spellings, then parse the result |
| `Path.expanduser` | only when there is no `drive`/`root` and the first **component** starts with `~` |

The port's string surgery could not express any of that, and each of its rules had a corner:

1. **`name`/`stem` came from the whole string.** `Path(".")`, `Path("/")` and `Path("C:\\")` have no
   last part, so CPython answers `""`; the port answered `"."`/`""` by trimming text.
2. **`join` was "append a separator when the right side is relative".** `Path("") / "b"` is `b` (the
   left is `"."`, it owns no separator to lend), and `Path("C:") / "b"` is `C:b` — the drive already
   ends in its anchor, so `ntpath.join` inserts nothing.
3. **UNC and degenerate shapes from `splitroot`'s slicing.** `\\` alone is a drive with no root,
   `\\srv` is a drive whose share never got a separator, a third leading separator is part of the
   drive, and a *share that is the whole path* takes the root `splitroot` could not see —
   `str(PureWindowsPath(r"\\server\share"))` is `\\server\share\` (the trailing separator 370 listed
   as "not reproduced").
4. **The device form is a substring test.** CPython writes `drv_parts[2] not in '?.'`, which is
   `'?.'.__contains__(drv_parts[2])` — so a *one-character* component (`\\srv`, `\\?`, `\\.`) is
   tested elementwise, but an **empty** component (`\\\server` → `['', '', '', 'server']`) and the
   two-character string `?.` are `in` it and count as device forms too. This one needed the CPython
   source read, not guessed at.

## How it was found, and how it is checked now

The oracle is CPython 3.14.7 (`tools/pyenv`), the interpreter the port is aligned against. The
corpus generator is `tools/scripts/test/gen_path_fuzz.py`: path-shaped words from a nasty alphabet
(drives, `UNC`, `\\?\`, `//?/`, `//./`, doubled separators, `.`/`..`, `~`, spaces, `%`, `*`,
non-ASCII, up to six pieces each) answered by `pathlib` itself, for **both** flavours — `Pure*`
answers the POSIX rules on a Windows host too, so one run pins both.

`expanduser` is a `Path` (not `PurePath`) method and a `PosixPath` cannot even be built on Windows,
so the generator runs 3.14's body against a `Pure*` instance, with `HOME`/`USERPROFILE` pinned; for
the Windows flavour it checks that reconstruction against the real
`pathlib.Path(a).expanduser()` — value and `RuntimeError` alike — case by case.

The corpus ships: **`src/Indra.Ocr.Tests/fixtures/path_fuzz.json`** (1907 cases, generated with the
default `per_kind`/seed; the fixture records both, plus the alphabet and the pinned homes, so it is
self-describing and regenerable). `PathHelperTests.The_fuzz_corpus_matches_cpython` runs every row
through the internal flavour overloads; the named theories above it are the rules, the corpus is the
net under them. A deep sweep is a re-run with a larger `per-kind`: four seeds × ~36 000 cases and
two seeds × ~76 500 cases all came back **0 mismatches**, every one of the eight `kind × flavour`
buckets at 0.

## What changed

`PathHelper` now transcribes 3.14 (`Lib/pathlib/__init__.py`, `Lib/ntpath.py`, `Lib/posixpath.py`):

| member | is now |
|---|---|
| `SplitRoot(s, flavour)` | `ntpath.splitroot` / `posixpath.splitroot` |
| `Parse(s, flavour)` | `_parse_path`: `splitroot`, then `tail.split(sep)` |
| `Format(...)` | `_format_parsed_parts`: `drive + root + sep.join(parts)` or `"."` |
| `Normalize` | `Format(Parse(s))` |
| `Join(left, right, flavour)` | `ntpath.join`/`posixpath.join` over the raw strings, then `Parse` (the `Finish` line is ntpath's bare-UNC-drive rule) |
| `Name`/`Stem` | the last parsed part (and `stem` keeps 3.14's "one non-dot character") |
| `ExpandUser` | 3.14's `Path.expanduser` body over `Parse`d parts |

It is pure string arithmetic, so the same body serves `net10.0` and `netstandard2.0` with no `#if`,
and the flavour overloads stay `internal` so both flavours are checkable on either host.

The port's observable behaviour is unchanged for every path the earlier steps tested — that is
exactly why 370's checks passed. What moves is message text, the JSON's `file` field and an `--out`
file name, and only for spellings a person does not type.

## Verification

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings, 0 errors (both target frameworks).
- `dotnet test src/Indra.Ocr.slnx` — **380 passed** (was 321): the corpus fact plus 58 named cases
  (UNC/degenerate `normalize`, the `join` of an empty or drive-only left, name-/root-only `stem`).
- Windows is byte-for-byte what it was: `cli_parity.py` **83/83**, `parity_sweep.py` 24/24,
  `viewer_cli_parity.py` 20/20, `viewer_parity.py` 12 cases + `stderr`.
- Linux is verified where a Linux host exists, as in 370: `ci.yml`'s `pack` job.

## Known differences (documented, not reproduced)

- **`~user`**: `posixpath`/`ntpath` look a named user up (`pwd`, `HOMEDRIVE`+`HOMEPATH`); the port
  only expands a bare `~`, so `~user/…` stays a name. The reference then raises
  `RuntimeError("Could not determine home directory.")`; the port reports the folder it was given.
  That is why the corpus keeps bare-`~` rows only: `pathlib` *refuses* the others, and reproducing a
  refusal that depends on the machine's user database is not parity worth having.
