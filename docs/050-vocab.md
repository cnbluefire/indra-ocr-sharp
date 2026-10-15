# 050 — Vocabulary and Unicode helpers

**Status:** done.

## Goal

Port `indra_ocr/vocab.py`: the token vocabulary the line recognizer decodes with, the label
canonicalizer, the two vocabulary builders, and the Indic syllable checks. These are the
last pure-managed pieces before the ONNX pipeline starts.

## Files

- `src/Indra.Ocr.Core/Text/UnicodeText.cs` — `Canonical`, `IndicValid`, `IndicPageOk`,
  `Category` (`unicodedata.category`), `CodePoints`, `CodePointCount`, `IsAlpha`, `IsSpace`.
- `src/Indra.Ocr.Core/Text/Vocab.cs` — `Vocab` (`FromJson`/`ToJson`, `Tokenize`, `Decode`,
  `Encode`, `Oov`, `Characters`, `Index`, `HasCommands`, `ClassCount`), plus
  `BuildLatinChars`/`BuildMultiTokens`.
- `tools/scripts/test/gen_vocab_fixtures.py` → `fixtures/vocab.json`.
- `src/Indra.Ocr.Tests/VocabTests.cs`.

## Where the vocabulary comes from

The pipeline does **not** build its vocabulary in code; `pipeline.py` loads it from
`meta.json` (`Vocab.from_json(meta["vocab"])`, and `meta["vocab_cjk"]` for the CJK
recognizer). The shipped `meta.json` holds those as JSON **strings**:

| key | tokens | commands | classes |
|---|---|---|---|
| `vocab` | 1677 | yes (LaTeX) | 1678 |
| `vocab_cjk` | 9967 | no | 9968 |

`meta["vocab"]` turned out to be **exactly** `build_multi_tokens()`, character for character
and in order, and `meta["vocab"]` is also exactly `Vocab.multi().to_json()` — so the builders
are still the definition of the token order, and reproducing them is what makes
`Vocab.Latin()`/`Vocab.Multi()` (and any future `--vocab` tooling) equal to the model.

Class 0 is the CTC blank; token `i` is class `i + 1`.

## The Unicode-version question

The builders are **derived from Unicode character categories**, so they depend on which
version of the Unicode Character Database the runtime ships:

- `_printable` drops `C*`/`Z*` (except space) and `Mn`;
- `_script_chars` drops `Cn` (unassigned) and `C*`/`Z*`.

Python 3.14 here reports `unidata_version 16.0.0`. .NET has no public "Unicode version"
property, so instead of trusting a version number the port **asserts the result**:
`Builders_match_the_reference` compares all 972 Latin and 1677 multi tokens against the
fixture, and `FromJson_matches_the_built_vocabulary` re-checks the 1677-token string from the
real `meta.json`. Both pass on .NET 10, i.e. its tables agree with `unicodedata` 16.0.0
everywhere the two vocabularies look. If a future .NET bumps the UCD in a way that reaches
these ranges, that test fails instead of silently renumbering the classes.

The category mapping is `unicodedata.category` → `System.Globalization.UnicodeCategory`:
`Cn → OtherNotAssigned`, `Co → PrivateUse`, `Cs → Surrogate`, `Cc → Control`, `Cf → Format`,
`Zl/Zp/Zs → LineSeparator/ParagraphSeparator/SpaceSeparator`, `Mn → NonSpacingMark`,
`Mc → SpacingCombiningMark`, `Lo → OtherLetter`.

## canonical()

Exactly `vocab.canonical`:

1. NFC (`string.Normalize(NormalizationForm.FormC)`);
2. a 10-entry look-alike table (four Romanian/Turkish comma-below ↔ cedilla letters, five
   bullet shapes → `•`);
3. `''|`` → "`;
4. runs of **4 or more** identical `[._\-=·*~]` → exactly three.

The regexes are re-implemented as scans rather than `System.Text.RegularExpressions`, because
.NET regexes work on UTF-16 code units while Python's work on code points: for `.` and `len()`
the two disagree on astral characters. The port iterates code points explicitly
(`CodePoints`), which also makes `Tokenize`/`Decode`'s `len(t) == 1` / `len(t) > 2` tests mean
the same thing as in Python.

`string.Normalize` throws on strings holding unpaired surrogates, which Python passes through;
the port catches that and returns the input unchanged (nothing in the pipeline produces them).

## Tokenize / Decode / Encode / Oov

- `_TOKEN_RE = \\[A-Za-z]+|\\[{}|,;! ]|.` (with `re.S`) — a LaTeX command, a single escaped
  character, or one character — is hand-written (`TokenRegexTokens`).
- `_MATH_SPAN = \$[^$]*[\\^_{][^$]*\$` — a `$...$` pair whose body contains `\`, `^`, `_` or
  `{`. That is what keeps prices (`"$5 or $10"`) as text. Also hand-written (`MathSpans`).
- Inside a match, single-space tokens are dropped (`math` mode), because spaces are not
  visible in rendered math; `Decode` then re-inserts **one** space between a LaTeX command
  and a following letter (`\alpha` + `x` → `\alpha x`, but `\alpha` + `\beta` →
  `\alpha\beta`). This is what keeps `"$ \leq $"`-style output valid LaTeX.
- A vocabulary without commands (`ClassCount` still `n + 1`) just returns the characters;
  the CJK recognizer uses that path.
- `Encode` returns `null` when *any* token is unknown (never a partial result), and `Oov`
  returns the unknown tokens as a set.

`ToJson` reproduces Python's `json.dumps(..., ensure_ascii=False)` byte for byte, including
the `\b \f \n \r \t` short escapes and lowercase `\uXXXX` for other C0 controls. That is
verified by round-tripping the whole 1677-token `meta["vocab"]` string.

## Indic checks

`indic_valid` walks the Devanagari/Bengali blocks: a combining mark (`Mn`/`Mc`) that is an
anusvara/candrabindu/visarga must follow a base, and a virama/nukta/vowel sign must follow a
consonant (`Lo`) or a nukta. A failure is the signature of a broken legacy font mapping
(`"िामथ"`, `"का ा"`, `"णािी"`). `indic_page_ok` marks a page suspect when more than 5% of its
Indic lines are invalid. Neither is used by the current CLI (`document.py` uses the
`readable()` check instead), but they are part of the module's API.

## Known gaps carried forward

`document.py` also needs `unicodedata.bidirectional` (to decide a run's base direction). .NET
exposes no bidi-class API at all, so that data has to be generated from Python and embedded;
it is its own step, `145`.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → 35 passed.
- `VocabTests` checks, against `fixtures/vocab.json` generated by
  `tools/scripts/test/gen_vocab_fixtures.py`:
  - the exact Latin (972) and multi (1677) token lists, and that every LaTeX command is present;
  - `meta.json`'s `vocab` string round-trips through `FromJson`/`ToJson`, equals
    `Multi()`, and the CJK vocabulary parses to 9967 command-free tokens (`ClassCount` 9968);
  - `Canonical` over the look-alike table, bullet foldings, leader runs, doubled quotes and
    NFC composition;
  - `Tokenize`/`Decode` (including blanks, out-of-range ids, and the LaTeX space rule),
    `Encode` (multi and a small command-free vocabulary) and `Oov`;
  - `indic_valid`/`indic_page_ok` over valid text, the three broken patterns, base-less marks
    and mixed pages.
