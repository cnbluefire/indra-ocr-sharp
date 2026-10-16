# docs/ — step specifications

Implementation steps for the indra-ocr → C# port. Numbers are coarse and leave room for
inserts (`010`, then `015` between `010` and `020`, etc.).

Each step file records: goal, scope, inputs, outputs, parity notes, verification, and
status. Keep these in sync with `PLAN.md`.

| Step | Title |
|---|---|
| [000](000-project-governance.md) | Project governance & layout |
| [010](010-solution-scaffold.md) | Solution, projects, build props, NuGet refs |
| [012](012-project-layout.md) | Project layout: `src/`, `Indra.Ocr.Pdf`, `GrayImage`, no codec in Core |
| [015](015-test-scaffolding.md) | Test scaffolding |
| [020](020-numeric-ops.md) | NumericOps (numpy parity) |
| [030](030-image-codecs.md) | Gray image types + image codecs (`Indra.Ocr`) |
| [040](040-opencv-primitives.md) | OpenCV primitives (resize, warpAffine, CCL) |
| [050](050-vocab.md) | Vocab + Unicode helpers |
| [060](060-ort-sessions.md) | ORT sessions + model set |
| [070](070-det-post.md) | det_post (LineNet pre/post-processing) |
| [080](080-rec-preproc.md) | rec_preproc (line crop + batching) |
| [085](085-line-mask.md) | line_mask (CenterMask) |
| [090](090-orientation-deskew.md) | orientation + deskew |
| [095](095-adaptive-detect.md) | adaptive detect |
| [100](100-recognize.md) | recognize |
| [110](110-order-post.md) | order_post |
| [115](115-reading-order.md) | reading_order |
| [120](120-layout-post.md) | layout_post |
| [130](130-assemble.md) | assemble |
| 135 | image inputs (folded into [030](030-image-codecs.md)) |
| [140](140-pdfium.md) | PDFium backend |
| [145](145-bidi-classes.md) | Unicode bidi class table |
| [150](150-textlayer.md) | textlayer |
| [155](155-ortocr.md) | OrtOCR (whole-page driver) |
| [160](160-document.md) | document Converter (PDF + image) |
| [165](165-project-split.md) | Three-way split: `Indra.Ocr` / `Indra.Ocr.Pdf` / `Core` |
| [170](170-models.md) | models (Hugging Face download) |
| [180](180-cli.md) | CLI (argparse, model folder, output, exit codes) |
| [190](190-parallel.md) | convert_many (worker-process pool) |
| [195](195-harness.md) | CLI alignment harness |
| [200](200-math.md) | MathReader |
| [205](205-core-shared.md) | Shared helpers move into Core |
| [210](210-viewer-engine.md) | Viewer Engine |
| [215](215-argparse-core.md) | Argparse engine + shared options move into Core |
| [220](220-viewer-api.md) | Viewer HTTP layer (minimal API + source-gen JSON) |
| [230](230-viewer-page.md) | The viewer's page (`PAGE`/`LOGO`) |
| [240](240-viewer-http.md) | Viewer parity surface (command line + concurrency) |
| [245](245-host-layout.md) | Host layout: argparse + Python helpers back in the CLI |
| [247](247-console-output.md) | One console class (`PythonInterop` folded into `ConsoleOutput`) |
| [250](250-reference-tests.md) | The reference's own unit tests, ported (26 of 27) |
| [260](260-parity-sweep.md) | End-to-end parity sweep (the reference's own assets) |
| 270 | [ONNX backend benchmark (ONNX Runtime vs Lokad.Onnx)](270-onnx-benchmark.md) |
| [280](280-deskew-threads.md) | The deskew search's threads (and why ReadyToRun is not it) |
| [290](290-aot.md) | NativeAOT: the properties, and the two warnings that had to go |
| [295](295-invariant-globalization.md) | Invariant globalization, and the NFC it forced |
| [300](300-viewer-ctrl-c.md) | The viewer's Ctrl+C (and what the slim host already had) |
| [310](310-model-download-progress.md) | The model download's progress (a callback in Core, lines in the hosts) |
| [320](320-async-io.md) | The I/O boundary is asynchronous (the pipeline is not) |
