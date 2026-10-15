# docs/ — step specifications

Implementation steps for the indra-ocr → C# port. Numbers are coarse and leave room for
inserts (`010`, then `015` between `010` and `020`, etc.).

Each step file records: goal, scope, inputs, outputs, parity notes, verification, and
status. Keep these in sync with `PLAN.md`.

| Step | Title |
|---|---|
| [000](000-project-governance.md) | Project governance & layout |
| [010](010-solution-scaffold.md) | Solution, projects, build props, NuGet refs |
| [015](015-test-scaffolding.md) | Test scaffolding |
| 020 | NumericOps (numpy parity) |
| 030 | Gray image types + codecs |
| 040 | OpenCV primitives |
| 050 | Vocab + Unicode helpers |
| 060 | ORT session infra |
| 070 | det_post |
| 080 | rec_preproc + line_mask |
| 090 | orientation + deskew |
| 095 | adaptive detect |
| 100 | recognize |
| 110 | order_post |
| 120 | layout_post |
| 130 | assemble |
| 135 | image inputs |
| 140 | PDFium backend |
| 150 | textlayer |
| 160 | document Converter |
| 170 | models |
| 180 | CLI |
| 190 | convert_many + output |
| 195 | CLI alignment harness |
| 200 | MathReader |
| 210 | Viewer Engine |
| 220 | Minimal API |
| 230 | PAGE/LOGO |
| 240 | Viewer HTTP tests |
| 250 | Port python unit tests |
| 260 | End-to-end sweep |
