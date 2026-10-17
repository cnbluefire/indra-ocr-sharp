# 380 — The test suite on a host that is not the capture host

## Goal

The first run of `ci.yml`'s `test` job (`ubuntu-latest`) failed **three tests** while every other job
passed. None of the three was a port bug: two fixtures had captured the machine that generated them,
and the third recorded pdfium's own font substitution. This step makes those answers the same on
every host, and lets `ci.yml`'s stand-in publish cover the arm64 RIDs as well.

## What failed, and why

| test | cause | fix |
|---|---|---|
| `ArgparseTests.Help_matches_the_reference` | `--threads`' help spells the host's usable cores (`usable_cores()`), and the fixture was captured on an 8-core machine while the runner has 4 (`… here 8)` vs `… here 4)`) | the test puts the host's count back into the captured text before comparing, and asserts that it substituted exactly one token. The rule behind the number is `CliParser.UsableCores`; the whole help text is compared against the reference running on the *same* host by `tools/scripts/test/cli_parity.py` |
| `DetectTests.Detect_at_matches_the_reference` | the coverage floor `boxes >= 100` assumed `resources/models/det.onnx`; `resources/` is externally downloaded and not in CI, so only the stand-in cases ran (85 boxes) | the floor depends on whether the model is there (`det is null ? 50 : 100`). `DetSession.TryOpen` answers null **only** when the file is absent — a load failure throws — so a lower floor cannot hide a broken model |
| `TextLayerTests.Runs_and_stats_match_the_reference` | a run bbox differed by 0.036 pt — the `H` of `Hello world` at 12 pt: `20.96` as captured on Windows, `20.924` on the runner | the samples embed their font now (the rest of this document) |

## The text layer's boxes are the *host*'s font

The samples were built with fpdf2's built-in `Helvetica`, one of the PDF "standard 14" fonts. Those
are never embedded, so pdfium substitutes a **host** font, and `FPDFText_GetCharBox` — the call both
the reference and the port take their boxes from — answers with the *substitute's* glyph boxes.
`TextLayerReader` only takes the min/max of those numbers; there is no arithmetic of the port's own
in them, which is why the difference is a property of the machine and not of the port.

Metric compatibility is why only the ink edges moved: both hosts substitute an Arial-metric font (the
space's advance came out as exactly 0.278 em, Helvetica's), but the glyph *outlines* are different
designs, and the left ink edge of an `H` differs by 0.036 pt at 12 pt. The fixture was therefore a
statement about Windows, and on Linux the port was answering what the reference on Linux answers.

### The fix: the samples embed their font

Every sample now embeds `tools/scripts/test/fonts/LiberationSans-Regular.ttf` (SIL OFL 1.1, 401 KB,
committed beside the generator — the file and its provenance are described in
`tools/scripts/test/fonts/README.md`). It is metric-compatible with Helvetica, so the layout the
samples were built with is unchanged, and pdfium now reads the metrics and the outlines *out of the
PDF* on every host. The three fixtures that carry sample PDFs (`pdf.json`, `document.json`,
`viewer.json`) were regenerated from the reference against the new samples; the port matches them
with no change of its own.

Two patched samples had to be reworked for that, because a TrueType font goes into the PDF as a
`/Type0` font with `/Identity-H` whose two-byte codes are the **subset's own** (fpdf2 numbers them by
first use):

* `invisible` used to splice a hand-written content stream — a hard-coded `/F1` and the literal bytes
  `(hidden layer)`. Those bytes no longer say "hidden layer" (a probe extracted garbage). The sample
  now draws both lines with fpdf2, and the patch *moves* the last text object into a stream of its
  own and sets render mode 3 in front of it: still two content streams, still one visible and one
  invisible text object, with the codes the document actually uses.
* `broken`'s `/ToUnicode` was a hand-written CMap over **1-byte ASCII** codes (`<48>`, `<65>`, …)
  with `<00> <FF>` as its codespace. Against an `/Identity-H` font nothing matched: the layer came
  out as a short run of raw codes instead of the documented private-use/control characters. The patch
  now reads the subset's own map (`_tounicode`, bfchar and bfrange) and writes a map that points
  every code the page uses at a private-use character (or `0x01` for the space), with a proper
  two-byte codespace — the whole run reads as "not text", which is what the sample is for.

`pdf_samples.build_all()` refuses to build a sample whose font is not embedded
(`_font_is_embedded`, over `/FontFile`, `/FontFile2`, `/FontFile3` on the descriptor, descendant
fonts included), so the next sample that reaches for a standard font fails at generation time with
the reason instead of quietly making the fixture host-bound again.

## Why not `FPDF_SetSystemFontInfo`

pdfium can be told to use the embedder's own system-font interface:
`FPDF_SetSystemFontInfo(FPDF_SYSFONTINFO*)`, called during initialisation (version 1 or 2, with
`MapFont`/`GetFont`/`GetFontData`/`GetFaceName`/`GetFontCharset`/`DeleteFont`/`EnumFonts`/`Release`;
`FPDF_GetDefaultSystemFontInfo` returns the platform's own). Both pdfium builds here export it — the
headers come with `bblanchon.PDFium.*` and `pypdfium2.raw` has the symbol — so the port *could* pin
the substitution to a committed font and get the same numbers on every host.

It is the wrong tool for this suite. The hook is **process-global**, and the other side of every
comparison is the reference's own process (`uv run indra-ocr`, pypdfium2), which we must leave
unmodified. A port that pinned what the reference does not would *cause* the divergence this step is
removing — on any host whose substitution differs from the pin — and not only in the text layer's
boxes: the same interface feeds glyph loading, so the rendered pages, i.e. the OCR input, would
differ too. Pinning it would also freeze one host's behaviour into the port instead of making the
*input* deterministic. Embedding the font keeps both sides identical on every host, and leaves real
PDFs — whose fonts are usually embedded and sometimes are not — behaving exactly as the reference
does on each host.

## `ci.yml` publishes the arm64 stand-ins too

The stand-in publish matrix was `linux-x64`/`win-x64` while `release.yml` publishes four RIDs. It now
covers `linux-arm64` (`ubuntu-24.04-arm`) and `win-arm64` (`windows-11-arm`) as well: a stand-in
publish needs no platform toolchain, so those runners exercise the RID's own runtime pack and the
trim/AOT analyzers on the architecture the release will publish for.

## Verification

* `dotnet build src/Indra.Ocr.slnx`: 0 warnings, 0 errors (both targets).
* `dotnet test src/Indra.Ocr.slnx`: **380 passed**, against the regenerated fixtures.
* The reference runs, all on the new samples: `cli_parity.py` **83/83**, `parity_sweep.py` **24/24**,
  `viewer_cli_parity.py` **20/20**, `viewer_parity.py` **12 cases + `stderr`**.
* `pdf_samples.py` run directly: all eight samples embed their font, and every text-layer probe reads
  the text it is supposed to read (`Hello world`, `Second line`, `Under image`, `hidden layer` in its
  own stream at render mode 3, the broken sample as private-use/control characters, the locked one
  with its password).
* The two host-dependent tests were checked from both sides: the help fixture's substitution is
  asserted to have happened exactly once, and the detect floors were re-derived from what the
  no-model run actually produces.
* The Linux half (the runner's own substitution, the arm64 RIDs) is verified where a Linux host
  exists — a dispatched `ci` run — as with `335`/`370`/`375` (`docs/370-path-flavour.md`).
