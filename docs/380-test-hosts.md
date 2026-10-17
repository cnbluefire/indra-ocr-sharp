# 380 — The test suite on a host that is not the capture host

## Goal

The first run of `ci.yml`'s `test` job (`ubuntu-latest`) failed **three tests** while every other job
passed. None of the three was a port bug: two fixtures had captured the machine that generated them,
and the third recorded pdfium's own font substitution. This step makes those answers the same on
every host, and lets `ci.yml`'s stand-in publish cover the arm64 RIDs as well. The next run failed
**one** test left over from the same class — the rotation matrix's last bit, which belongs to the
host's `sin`/`cos` — and that is dealt with at the end of this document.

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

## The rotation matrix's last bit is the platform's `sin`/`cos`

With the three above fixed, the next `ci` run failed **one** test — `CvPrimitivesTests.Rotation_matrix_matches_cv2`,
on the 45° case: `m[1] = 0.7071067811865475`, expected `0.7071067811865476`. That is a **one-ulp**
difference in `cos π/4`, and it is nobody's bug: `CvWarpAffine.RotationMatrix2D` calls `Math.Cos`/`Math.Sin`,
the reference's own C++ calls `std::cos`/`std::sin`, and both are the *host* library — the two always move
together, but glibc and the Windows CRT round this argument one ulp apart. (The fixture's angle → radians
expression is the reference's, bit for bit, so the argument is identical; only the function's last bit
differs. The skew search builds its 32 probe angles through the same matrix, but `OrientationTests`
compares its sharpness scores at 1e-5 and its chosen angle is a grid value, so it never depended on a
single last bit.)

Every other comparison in the suite held: the OpenCV fixed-point paths, the connected-components labels,
the ONNX numbers and the sample PDFs' text layers are all platform-stable. So the one comparison that
touches a host library now allows the ulps its last bit is worth — `1e-12` relative, ~10⁴ ulps, far below
the `docs/015` tolerance rule and far above any real formula error (which would be at least 1e-6) — and
nothing else was loosened. The allowance was checked against **all 651 cases** with `cos` and `sin` each
moved a ulp in both directions: the worst deviation is `2.6e-13` against the `1e-12` it is given, so
there is ~4× headroom for a second ulp and no room for a mistake. `docs/040-opencv-primitives.md` records
why the port cannot be bit-exact here on a fixture captured on another host.

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
* `CvPrimitivesTests.Rotation_matrix_matches_cv2` now allows the ulps the host's `cos`/`sin` are worth
  (1e-12 relative) and every other comparison in the suite is still bit for bit — the run that found
  the one-ulp failure had all of the others green, including the OpenCV fixed-point paths, the
  connected-components labels, the ONNX numbers and the sample PDFs' text layers.
* The Linux half (the runner's own substitution, its libm, the arm64 RIDs) is verified where a Linux
  host exists — a dispatched `ci` run — as with `335`/`370`/`375` (`docs/370-path-flavour.md`).
