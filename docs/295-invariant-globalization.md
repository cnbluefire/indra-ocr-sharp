# 295 — Invariant globalization, and the NFC it forced

## Goal

Set `<InvariantGlobalization>true</InvariantGlobalization>` for the whole solution
(`src/Directory.Build.props`), so the native binaries need no ICU: on Linux a self-contained or
NativeAOT binary otherwise depends on the machine's ICU, which is the one thing an AOT publish is
supposed to remove. It is also the honest setting for a port of a Python program — the reference
formats and parses with no locale at all, and every culture-sensitive thing the port does
(`FloatFormat`, `ArgumentNumber`, `PythonText`, the `str.strip`/`round` rules) is hand-written to
match it.

The setting is a behaviour knob, not a packaging detail, and it did change behaviour: one test
failed. This step is the story of that failure and of the managed NFC it required.

## The failure

`VocabTests.Canonical_matches_the_reference` — `Expected: "é"`, `Actual: "é"`, and the two look
identical because they differ in the *bytes*: the fixture's input is `e` + U+0301 (combining
acute), the reference composes it into U+00E9 (`unicodedata.normalize("NFC", text)`,
`indra_ocr/vocab.py:43`) and the port did not.

Under invariant globalization `string.Normalize` is a no-op — there is no ICU to normalize with —
and `string.IsNormalized` answers true for everything, so `UnicodeText.NormalizeNfc` (the fast
path, then `Normalize(FormC)`) handed the input straight back.

The port cannot use the framework's normalization any more, and it should not have used it in the
first place:

- ICU's Unicode version is the *machine's*, so `string.Normalize` would drift from the
  reference's interpreter on a machine with a newer (or older) ICU, while the port's rule is
  bit-exact parity;
- .NET's `Normalize` throws on unpaired surrogates, so the port caught the exception and returned
  the whole string unnormalized. Python normalizes *around* them, so a label with one lone
  surrogate was a silent deviation;
- the port's philosophy is already "replicate the reference's algorithms in managed code"
  (OpenCV's fixed-point resize, the 4-connectivity CCL, the bidi table).

## The tables

`tools/scripts/test/gen_nfc_table.py` generates `src/Indra.Ocr.Core/Text/NfcTables.cs` from
CPython's own `unicodedata` (16.0.0, the version the reference's interpreter carries — the same
source and the same shape as `gen_bidi_table.py`), and records
`src/Indra.Ocr.Tests/fixtures/nfc.json`:

| table | rows |
|---|---|
| canonical decompositions (one or two code points, Hangul excluded) | 2081 |
| primary composites | 961 |
| non-zero canonical combining classes, as runs | 393 |

Hangul is excluded on purpose: UAX #15 decomposes and composes `AC00..D7A3` arithmetically, which
is both smaller and exact. The generator checks every table against `unicodedata` before writing
it, and the fixture checks it again from the C# side.

The fixture holds 25114 cases: every canonical decomposition and every primary composite (both
the composite and the pair it comes from), all 11172 Hangul syllables, 4000 random strings over
an alphabet of starters, combining marks of several classes, jamo, Hangul and supplementary-plane
characters with canonical decompositions, 4000 random strings of arbitrary code points, and the
edges (empty, a lone mark, a blocked composition, 2000 repeats, astral pairs). 153 further cases
carry a lone surrogate and are stored as **code points** instead of as strings: .NET's JSON
reader refuses to read a string with one back ("Cannot read invalid UTF-16 JSON text as string.
Invalid surrogate value").

## The algorithm

`src/Indra.Ocr.Core/Text/Nfc.cs` is UAX #15 in three passes over code points:

1. **canonical decomposition**, recursively, with Hangul arithmetically. Surrogate pairs are
   decoded, and a lone surrogate is just a code point with no entry, so it passes through exactly
   as Python lets it;
2. **canonical ordering** — a stable insertion sort by combining class, so equal classes keep
   their order;
3. **canonical composition** — the last starter absorbs every following character it composes
   with while nothing of the same or a higher class stands between them, and an irregular
   sequence that *starts* with a mark blocks composition until a real starter appears (the
   reference implementation's `lastClass = 256` trick). Hangul composes arithmetically here too:
   `L + V` makes an LV syllable, `LV + T` an LVT one.

`UnicodeText.NormalizeNfc` is gone, and its surrogate `try`/`catch` with it: `Canonical` now
calls `Nfc.Normalize` directly.

## Verification

- `dotnet build src/Indra.Ocr.slnx` — **0 warnings, 0 errors** (the generated tables compile
  with the AOT/trim analyzers on).
- `dotnet test src/Indra.Ocr.slnx` — **206 passed** (203 before: three `NfcTests` — the fixture,
  idempotence over the same cases, and the label path that the property broke).
- The four harnesses against the live reference: `cli_parity.py` 83/83, `parity_sweep.py` 24/24,
  `viewer_cli_parity.py` 20/20, `viewer_parity.py` 12 cases + `stderr`. All of them run the
  Debug build, whose `runtimeconfig.json` now carries `"System.Globalization.Invariant": true`,
  so the setting is exercised by everything above rather than assumed.
- Page timings are unchanged within this machine's noise (min 510 ms against 481 ms recorded
  before the change, spread 510–619 ms over the same page): `Canonical` runs once per recognized
  label, a few dozen times per page.

## Notes

- What else invariant mode changes: `CultureInfo.CurrentCulture` is always the invariant one, so
  `ToLower`/`ToUpper`/`string.Compare`/`ToString`/`Parse` are culture-free. The port never relied
  on the machine's culture — everything culture-sensitive is hand-written — and the test suite
  plus the harnesses are the evidence. Normalization was the one real casualty.
- The alternative was to leave the property out and keep ICU. That would have kept a dependency
  the AOT publish exists to remove, and would have left the port's NFC tied to the machine's ICU
  version; implementing the reference's own tables removes both, and is what `145` (bidi) and
  `050` (vocab) already do for their Unicode questions.
- `docs/290-aot.md`'s note said globalization was left on and that turning it on was "a change to
  verify, not a packaging detail". That is exactly what happened, and the note now points here.

## Files

- `src/Directory.Build.props` — `<InvariantGlobalization>true</InvariantGlobalization>` and its
  reason.
- `tools/scripts/test/gen_nfc_table.py` — the generator (tables + fixture), from CPython's
  `unicodedata`.
- `src/Indra.Ocr.Core/Text/NfcTables.cs` — the generated tables.
- `src/Indra.Ocr.Core/Text/Nfc.cs` — UAX #15 over them.
- `src/Indra.Ocr.Core/Text/UnicodeText.cs` — `Canonical` calls `Nfc.Normalize`.
- `src/Indra.Ocr.Tests/fixtures/nfc.json`, `src/Indra.Ocr.Tests/NfcTests.cs` — the fixture and
  its three tests.
