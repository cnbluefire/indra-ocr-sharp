# 145 — Unicode bidi classes

**Status:** done.

## Goal

Port `unicodedata.bidirectional`, the one `unicodedata` function `document.py` needs beyond
character categories: `right_to_left` decides the base direction of a token list by counting
the strong classes — `R`/`AL` as right-to-left, `L` as left-to-right. .NET exposes no
bidirectional classes, so the data is generated from the same CPython the reference runs on.

## Files

| File | Contents |
|---|---|
| `tools/scripts/test/gen_bidi_table.py` | generates the table and the fixture, verifying the ranges against Python for all 1 114 112 code points |
| `src/Indra.Ocr.Core/Text/BidiClasses.cs` | generated: 2 300 contiguous ranges, `(last code point, class)` |
| `src/Indra.Ocr.Core/Text/UnicodeText.cs` | `BidiClass(int)` / `BidiClass(string)` |
| `src/Indra.Ocr.Tests/fixtures/bidi.json` | the same ranges, as the reference answer |
| `src/Indra.Ocr.Tests/BidiClassTests.cs` | the parity tests (3) |

## Shape

```
unicodedata.bidirectional(chr(cp))   ->  UnicodeText.BidiClass(cp)
# "" for unassigned code points; 24 classes over 2 300 ranges
document.right_to_left:  rtl += b in ("R", "AL"); ltr += b == "L"
```

## Port notes

- **The table is CPython's, not .NET's.** Python 3.14 here reports `unidata_version 16.0.0`;
  the generated ranges are exactly what `unicodedata.bidirectional` answers for that UCD, so
  the port cannot drift with .NET's own Unicode data. `BidiClassData` is `internal`, and the
  generator stamps the version and "do not edit by hand" in its header.
- **Ranges, not a per-code-point array.** Contiguous runs of equal class collapse 1 114 112
  answers into 2 300 entries (~59 KB of C#), and a binary search over their end points answers
  in eleven steps. The generator asserts the collapse is lossless — it re-derives every code
  point's class from the ranges and compares it with Python — before writing anything.
- **`""` is a class.** Unassigned code points (and only those) answer the empty string; a
  surrogate, for instance, answers `L` (U+D800 is inside a range whose UCD class is `L`). This
  is why the port returns a `string` rather than an enum.
- **The string overload mirrors Python's argument rule.** `unicodedata.bidirectional` takes a
  one-*code-point* string (`len(s) == 1` in Python), so an astral character is one argument
  and `"AB"` is a `TypeError` — here an `ArgumentException`. `right_to_left`'s own
  `len(t[0]) == 1` guard (step 160) is therefore the same test in both languages:
  `UnicodeText.CodePointCount(s) == 1`.

## Verification

- `tools\pyenv\Scripts\python.exe tools\scripts\test\gen_bidi_table.py` asserts the ranges
  reproduce `unicodedata.bidirectional` for all 1 114 112 code points, then writes the C# file
  and the fixture.
- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **119 passed** (was 116; +3 `BidiClassTests`), 15 s.
- `Bidi_class_matches_unicodedata_everywhere` expands the fixture ranges back into a
  per-code-point table and compares all 1 114 112 of them against `UnicodeText.BidiClass`
  (152 ms), so a transcription error anywhere in the generated file fails the test.
- `Strong_classes_are_the_ones_right_to_left_counts` pins the classes the caller actually
  looks at, and `String_overload_takes_one_code_point` the argument rule.

## Deviations

- None: the reference's answers are reproduced exactly, for every code point, by construction.
  The only difference is the *type* — a class name string instead of CPython's
  interned-constant return, with the same contents.
