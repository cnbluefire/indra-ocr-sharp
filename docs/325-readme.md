# 325 — The README

## Goal

The repository had a one-screen README aimed at the port itself (layout, build, where the
reference tooling lives). Now that the port is complete, the README is the front page for
someone who wants to *use* it: what the original project is, what this port is, how to build it,
how to run the two hosts, how to use the library, and what the licensing is. The name it carries
is **Indra.Ocr.Sharp**.

## What it says

- **Title and upstream links** — `Indra.Ocr.Sharp`, one line of positioning ("the same command
  line, the same local viewer, the same output — no Python, no PyTorch, no OpenCV"), and the two
  upstream URLs (code and model weights).
- **About Indra OCR** — what the original is, with upstream's own numbers, and a line saying the
  numbers are upstream's. Links to the repository, the model page, the documentation and
  upstream's *Limitations*.
- **About this port** — parity (byte-for-byte deterministic output, the four harnesses' counts,
  the deliberate differences living in `docs/`), ONNX Runtime through NuGet, no OpenCV, the two
  decoder projects, AOT-readiness and the models' origin. A note that the executables keep the
  reference's names because they are part of the parity surface, and the `Layout` table from the
  old README, extended with the dependency direction.
- **Build** — `dotnet build` / `dotnet test`, `dotnet run` for the two hosts, and the NativeAOT
  publish for any RID.
- **Command line** — the CLI examples and option table, the viewer examples and option table
  (both taken from the port's own parsers, so they cannot drift), and the exit-code rule. The
  viewer's `--tesseract` / `--tesseract-lang` are listed with the truth: they exist for
  command-line parity and the comparison is not implemented, so `--tesseract` fails at startup
  and `/info` always reports `tesseract: false`.
- **Using the library** — `PdfConverter` and `ImageConverter` as the two halves of the
  reference's `document.convert`, dispatched with `OrtOcr.IsPdf`, with a worked example that
  loads the models, reads a PDF page by page and an image frame by frame, and notes on the
  convenience overloads, the `OcrPage` fields, the `GrayImage` pixel entry point and the
  asynchronous download.
- **License** — code Apache-2.0 (`LICENSE`, derivative of `datalab-to/indra`, attribution in
  `NOTICE`), model weights modified OpenRAIL-M
  (`THIRD-PARTY-LICENSES/MODIFIED-OPENRAIL-M.txt`, downloaded at runtime, not distributed here),
  and the licensing unchanged from the original project and its models.
- **Development** — the reference Python environment, the four alignment harnesses and their
  counts, `docs/` and `PLAN.md`.

## Files

- `README.md` — rewritten.
- `NOTICE` — the product name became `Indra.Ocr.Sharp` (it said `indra-ocr-sharp`); nothing else
  in it changed.
- `docs/README.md`, `PLAN.md` — this step.

The names that are parity targets did not move: the executables stay `indra-ocr` and
`indra-ocr-viewer`, and `ProductInfo.Name`/`Version` (`indra-ocr 0.3.2`) are untouched.

## Verification

The README's commands were run, not assumed:

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings; `dotnet test src/Indra.Ocr.slnx` — 218 passed.
- `dotnet run --project src/Indra.Ocr.Cli -- --version` and
  `dotnet run --project src/Indra.Ocr.Viewer -- --version` — both print `indra-ocr 0.3.2`.
- `indra-ocr-viewer --tesseract` — exit 1 with `indra-ocr-viewer: tesseract is not supported by
  this port`, which is what the README says happens.
- The model size quoted (about 18 MB) is the sum of the eight downloaded files
  (`resources/models`: 17.45 MB).
- The library example's API surface was checked against the sources it names: `ModelFolder.Resolve`,
  `OrtOcr.IsPdf`, `PdfConverter.Open`/`PdfPage`/`ConvertPages`/`ConvertPage`,
  `ImageConverter.Convert`/`ConvertFrames`, `OcrPage`'s members, `OcrLine.Box/Text/Confidence`,
  `LayoutBlock.Label/Bbox/Text`.
