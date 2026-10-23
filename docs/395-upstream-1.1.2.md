# 395 — Upstream 1.1.2 baseline

## The two revisions

The read-only reference checkout in `resources/indra-src` was at upstream tag `v0.3.2`, commit
`3d773aeedc5d79d1d0a2a04007a9075cd71a59f4` (2026-10-15, “0.3.2: models from tag v3.1.1”).
On 2026-10-24, `https://github.com/datalab-to/indra.git` had advanced to `main` commit
`e20355319b5aa5705a876a8d2b40aca606d7df5f`; the code release to port is its latest tag,
`v1.1.2`, commit `0e96b93c6810f17cf8352acbb4487cc9e45b66d5`. The two commits after the tag are README-only
work plus its merge, so they do not change the Python package.

`resources/` is read-only. The current upstream was cloned to `temp/indra-upstream-current` for the
comparison; the reference checkout itself was not pulled or modified.

## What changed since 0.3.2

The range has six commits (`v1.1.0`, `v1.1.1`, `v1.1.2`, then the README work), 48 changed files,
2,760 insertions and 463 deletions. The implementation changes are:

- model revision `v3.1.1` -> `v1.1.0`, about 18 MB -> 76 MB; downloads add `lm.npz` and
  `lm_*.npz`;
- recognizer groups (`base`, `cjk`, `rtl`, `other`) selected by the script model, with optional
  `rec_<group>.onnx` and `vocab_<group>` metadata;
- CTC prefix beam search backed by n-gram language models for uncertain readings;
- handwriting and filled-form recognition, tall-window input, and light text on dark ground;
- deskew over +/-45 degrees, a five-class orientation model, and vertical CJK pages;
- right-to-left visual/logical conversion, including presentation forms, combining marks and
  mirrored brackets;
- a detector picture channel and picture blocks in JSON/markdown;
- text-layer quarter-turn handling, UTF-16 surrogate pairs, vertical layouts, visual-order RTL
  rejection, and an OCR/text-layer script-agreement gate for legacy fonts;
- CJK display-width features, CJK sentence punctuation, vertical-page XY-cut blocks, and several
  line-tidying fixes;
- `--max-pages` rejects a negative value; the viewer no longer exposes the removed PDF-order
  switch and reports `vertical`.

The complete upstream summary is `CHANGELOG.md` in that checkout. The new upstream tests cover dark
lines, arbitrary deskew, language-model decoding and routing, pictures, RTL, tall windows, line
tidying, turned text layers, vertical order and visual RTL.

## Porting order

The port keeps its commit-per-step rule. First update model discovery/download and independent
algorithms (RTL, LM, dark/tall crops, order features); then wire the 1.1.0 pipeline and output;
finally port the 1.1.1/1.1.2 text-layer trust checks and align the CLI/viewer/reference harnesses.
The target remains exact `v1.1.2` behaviour; upstream `main` is recorded only so a later refresh can
tell package changes from README-only commits.

The first sub-step switches `ModelDownload.HfRevision` to `v1.1.0`, keeps `lm.npz` / `lm_*.npz`,
and generalises model loading from one hard-coded CJK pair to every `vocab_<group>` /
`rec_<group>.onnx` pair named by `script_groups`. The v1.1 metadata currently says
`[base, cjk, rtl, world]` and `rec_window = 3`; all of those are retained by `OcrModels` for the
pipeline step.

The next independent-algorithm sub-step adds the reference's histogram percentiles and dark-line /
tall-window crop rule, plus its involutive RTL conversion. The latter cannot call .NET's NFKC under
invariant globalization, so the presentation-form compatibility decompositions are generated from
the same CPython Unicode database into `RtlPresentationForms.cs`, exactly like the existing NFC and
bidi tables. These routines are present but the old pipeline does not opt into them until the group
routing / `rec_window` step.

That pipeline sub-step wires four-way script routing, a 0.95 sure gate, double-reading uncertain
non-base lines and keeping the more confident answer, base-only tall-window input, shared light/dark
crop decisions, group-character tokens, RTL page decoding and Hangul jamo composition. A one-output
v0.3.2 script classifier remains a hard CJK switch so the old fixtures and old local model folder
keep working while the reference moves to the new models.

The LM/orientation sub-step reads numpy's NPZ/NPY tables directly (no new dependency), exposes the
recognizers' optional `blank` / `topv` / `topi` outputs and applies the reference's gated CTC prefix
beam. On the upstream handwriting example this removes every remaining line-text difference against
Python. Deskew now searches +/-45 degrees in the reference's 2.0 -> 0.5 -> 0.1 degree stages; old
four-class and new five-class orientation models are both accepted.

The page/output sub-step carries the five-class model's vertical flag through Core, the worker
protocol, CLI JSON and viewer JSON; class 4 turns columns into lines but reports `rotation = 0`.
The detector's optional fourth channel is decoded with 8-connectivity into Picture blocks and
positioned markdown markers, while old three-channel graphs keep their exact path. Text-layer data
now carries quarter turn, sequence, frame, vertical and sideways runs; the trust check rejects a
visual-order RTL layer once 20 neighbouring RTL pairs are at least 25% reversed. The zero-turn PDF
path deliberately keeps its old affine rule, and the new quarter-turn rule activates only when the
converter asks for a turned frame.

The host-surface sub-step removes the retired `--model-order` / viewer PDF-order control, gives a
negative `--max-pages` the new argparse error, regenerates both help fixtures and the viewer HTML,
adds `vertical` to JSON, and moves the product version to `1.1.2` (NuGet `1.1.2-alpha.2`).

The 1.1.2 legacy-font gate samples at most six assigned text-layer lines, asks the script router what
their images contain, and rejects a layer when fewer than 25% agree (specifically: 90% Latin codes
while the router is confidently non-base). The page then falls through to OCR with
`text layer does not match the page` in its note.

The final PDF/CLI-alignment sub-step fixes quarter-turn frame 1/3 box mapping, rotates the render to
the same frame before assignment, makes model order unconditional after the option's removal, and
ports argparse's `_page_count` type name and negative-value error. The parity harnesses now accept
`INDRA_OCR_REFERENCE` / `INDRA_OCR_TEST_MODELS` overrides and cap OpenBLAS threads, so refreshes can
test a `temp/` checkout without touching `resources/`.

The final result-algorithm sub-step counts CJK/fullwidth characters as two order-feature columns,
recognises CJK sentence punctuation, adds whitespace-thresholded XY-cut blocks for vertical pages,
mirrors RTL page geometry before reading order/layout, forces vertical lines through the CJK
recognizer and maps their boxes back to the original page, and ports `tidy_lines`' fill-rule removal
and standalone list-marker merge. The order/assemble generators accept the same reference/model
overrides and their fixtures are regenerated from 1.1.2; the image-heavy pipeline fixture remains
the committed 0.3.2 input set because `resources/` is read-only and upstream changed those binaries.

## Verification

- Release solution build: both `net10.0` and `netstandard2.0`, 0 warnings/errors.
- 1.1.2 order/assemble/layout fixtures: 34/34.
- Selected deterministic CLI parity against the temp checkout and v1.1.0 models: 22/22, including
  help/version, `_page_count`, image md/text/JSON, rotated/tilted/columns/blank, PDF text/OCR/image,
  turned/cropped/invisible/broken layers, JSON and `--no-image-ocr`.
- Viewer command-line parity: 20/20; both hosts report `indra-ocr 1.1.2`.
- The upstream handwriting example's 21 line texts match after LM beam search.

The complete xUnit suite is now 393/393. Real-model tests require the local external model folder to
match the v1.1 family (RTL/world recognizers plus `lm.npz`); otherwise they skip instead of comparing
new fixtures with an old v0.3.2 model set. The viewer HTTP *reference* still cannot coexist with all
three v1.1 LM tables on this 2 GB Windows machine: when free memory fell below 203 MB, numpy/ORT and
even MSBuild failed allocations, so that server comparison remains for CI or a roomier host.
