# 165 — The three-way split (`Indra.Ocr` / `Indra.Ocr.Pdf` / `Indra.Ocr.Core`)

**Status:** done.

## Goal

Land the project layout the whole plan is written against: **five** projects, no `src/Shared/`
linked sources, and a `Core` that holds every algorithm over an OCR *result* while the two
decoders (`Indra.Ocr` for encoded images, `Indra.Ocr.Pdf` for PDFs) hand it the same
`GrayImage` and never reference each other.

This step is the review of steps `012`/`140`/`150`: the seam those steps introduced
(`ITextDocument`/`ITextPage`, a decoder-linked `ImageFiles`, a `Converter` in Core) was the wrong
shape — it existed only to keep PDFium out of Core, at the price of an interface, a document
abstraction and a shared-source folder. Deleting the seam and giving each decoder its own project
does the same job with less machinery.

## Files

| Change | File | Contents |
|---|---|---|
| new project | `src/Indra.Ocr/Indra.Ocr.csproj` | ImageSharp only; references `Core` |
| moved | `src/Shared/ImageFiles.cs` → `src/Indra.Ocr/Images/ImageFiles.cs` | decoder; `List<GrayImageData> ReadImages(path)`, `ReadImage(bytes)`, `EncodeJpeg` |
| new | `src/Indra.Ocr/Images/GrayImageData.cs` | owning frame: `Width`/`Height`/`Stride`/`Data` + implicit → `GrayImage` |
| new | `src/Indra.Ocr/Images/ImageConverter.cs` | the image half of `document.convert` (`Convert` / `ConvertFrames`) |
| deleted | `src/Shared/ImageFiles.cs`, `src/Shared/` | no linked sources |
| deleted | `GrayImageConsumer` | the callback form of `ReadImages` |
| moved | `src/Indra.Ocr.Core/Document/TextLayer.cs` → `src/Indra.Ocr.Core/Text/` | `TextLayer`, `TextChar`, `TextLine`, `TextLayerStats`, `TextLayerReader` |
| new | `src/Indra.Ocr.Core/Text/TextLayerRuns.cs` | `readable`, `right_to_left`, `assign_runs` (from `document.py`) |
| new | `src/Indra.Ocr.Core/Pipeline/SafePage.cs` | `document._safe` |
| moved | `Converter.cs` → `src/Indra.Ocr.Pdf/PdfConverter.cs` | the PDF half (`Convert` / `ConvertPages`) |
| new | `src/Indra.Ocr.Pdf/PdfiumError.cs` | the PDFium exception, named as the reference prints it |
| deleted | `Core/Document/{TextPage.cs, Converter.cs}` | `ITextDocument`, `ITextPage`, `TextPageError`, `Converter` |
| deleted | `src/Shared/` | — |

## Dependency graph

```
                 Indra.Ocr        (ImageSharp)      Indra.Ocr.Pdf   (PDFiumCore)
                     │                                   │
                     └──────────────► Core ◄─────────────┘
                                  (OnnxRuntime)
                        ▲                     ▲
                        │                     │
                 Indra.Ocr.Cli         Indra.Ocr.Viewer
```

`Indra.Ocr` and `Indra.Ocr.Pdf` reference `Core` and not each other; `Core` references neither.
`Cli`/`Viewer` reference all three and own no codec.

## Port notes

- **The interfaces existed for one reason and that reason is gone.** `ITextDocument`/`ITextPage`
  were Core's PDF-free view of a PDF page, so `textlayer.py` and `document.py` could be ported
  into Core. But a page is pixels first and a text layer second, and the *policy* (`read_text_layer`
  and the verdict) needs only runs, a render and a scale — plain data. So the interface, the
  `TextPageError` type and the page abstraction are deleted: `PdfDocument.LoadPage` returns
  `PdfPage`, and `PdfPage`/`PdfDocument` are `IDisposable` directly.
- **`extract_runs` splits in two.** Reading the runs off a page *is* the PDFium calls
  (`get_unicode`/`get_angle`/`get_box`, the device affine, the page size, and the "Failed to get
  charbox." error), so it stays in `Indra.Ocr.Pdf` as `PdfPage.ExtractRuns()`. The verdict on
  those runs (`bad_char_count`, `no_ink_frac`, the four checks and their messages) is
  recognition-result code and lives in Core (`TextLayerReader`).
- **The verdict reads the runs lazily.** The reference checks the invisible-text fraction
  *before* it touches a character box, so a page whose text is drawn invisibly never reaches
  `extract_runs` and a box PDFium refuses cannot turn that verdict into an error.
  `TextLayerReader.ReadTextLayer` therefore takes a `Func<(IReadOnlyList<TextLine>, TextLayerStats)>`
  and calls it after the invisible-text check; `PdfConverter` passes `() => page.ExtractRuns()`.
- **`PageChar` and `PdfiumError` stay in the PDF project.** `PageChar` is what `ExtractRuns`
  consumes, and `PdfiumError`'s *name* is observable (step `160`'s `_safe` prints
  `f"{type(e).__name__}: {e}"`), so both belong where the PDFium calls are.
- **The namespace is `Indra.Ocr.Text`, not `Indra.Ocr.TextLayer`.** A namespace named
  `TextLayer` shadows the type `TextLayer` in every `Indra.Ocr.*` namespace — the compiler then
  resolves `TextLayer` to the namespace and every use fails. The text-layer types join the
  existing `Indra.Ocr.Text` namespace instead.
- **The image namespace is `Indra.Ocr.Images`, not `Indra.Ocr.Image`.** The same trap with
  SixLabors' `Image` (which `ImageFiles` uses heavily); `Images` is unambiguous and plural, like
  the project name.
- **`GrayImage` cannot be stored, so frames need an owning twin.** `GrayImage` is a `ref struct`
  view: no `List<GrayImage>`, no lambda capture, no heap. `GrayImageData` is the owning record
  (`Width`, `Height`, `Stride`, `ReadOnlyMemory<byte> Data`) with an implicit conversion to
  `GrayImage`, so a decoder can return `List<GrayImageData>` — one entry per frame, mirroring
  `cv2.imreadmulti` — and the frames go to the pipeline one at a time. The pixels are allocated
  by the decoder anyway (a frame is colour, the pipeline wants 8-bit gray), and the conversion is
  free: it only wraps memory the record already keeps alive.
- **`GrayImageConsumer` and the callback `ReadImages` are deleted.** With an owning frame there is
  no reason to push frames out through a delegate: `ReadImages(path)` returns the list, the
  caller slices it for `max_pages` exactly like the reference does, and single- and multi-frame
  files both work. Batch scenarios (a multi-page TIFF) are the caller's business.
- **`ImageConverter` calls `SafePage.Read` like `PdfConverter` does.** The lambda captures the
  `GrayImageData` (heap-safe) and the implicit conversion happens *inside* it, so the
  `ref struct` never has to be captured — which is exactly why the owning type is needed.
- **`SafePage.From` is public** so a caller that already holds the exception (the image half) can
  use it without a lambda.
- **`ImageFiles.Load` has a span overload.** `ReadImage(ReadOnlySpan<byte>)` is the viewer upload
  path; `Image.Load<Rgba32>(ReadOnlySpan<byte>, out IImageFormat)` pins the caller's buffer and
  reads it through an `UnmanagedMemoryStream`, so the bytes are not copied (no `ToArray()`, no
  `MemoryStream`). The stream path (files) picks the format from the same `Load` call instead of a
  separate `DetectFormat` pass.
- **`OrtOcr.IsPdf` is the dispatch.** `is_pdf` is a module function of `pipeline.py`, so it sits
  with the rest of that module's port; it searches the first 1 KiB for `%PDF-`. The hosts check
  `File.Exists` first, then `IsPdf`, then hand the path to the matching converter.

## Verification

- `dotnet build src\Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` → **128 passed** (was 116 + the step-160 tests; the
  document tests now cover both converters and the split text layer), ~75 s.
- `ImageDecodeTests` still matches the OpenCV reference through the relocated decoder (the
  grayscale formulas are unchanged), `TextLayerTests`/`PdfTests` still pass against their
  fixtures, and `DocumentTests.Convert_over_sample_pdfs_matches_the_reference` matches the
  reference on every sample page after the split.

## Deviations

- **The project count went from four to five**, so `AGENTS.md`, `PLAN.md` and `docs/012` describe
  the old layout; they were updated in this step.
- **`src/Shared/` is gone.** The test project references `Indra.Ocr` directly, so the linked
  sources have no purpose left.
- **One flaky test-host crash was seen once** under the full suite (native PDFium, during
  `PdfTests.Renders_match_pypdfium2`); it did not reproduce in later runs (`PdfTests` alone 8/8,
  the full suite 128/128 twice). Recorded here so a future failure is not mistaken for a parity
  bug.
