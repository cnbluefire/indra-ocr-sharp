# 012 — Project layout

**Status:** done. (Recorded after `020`; the number is insert-friendly and reads next to
`010`, which created the original solution.)

## Goal

Put every .NET artefact in one place and give `Indra.Ocr.Core` a narrow, dependency-free
image contract, so the pipeline cannot be coupled to a particular image codec or PDF backend.

## What changed

1. **Everything .NET lives under `src/`.** The solution file, `Directory.Build.props` and
   `Directory.Packages.props` moved from the repo root into `src/`, together with the
   `Indra.Ocr.Tests` project. The repo root now holds only `AGENTS.md`, `PLAN.md`,
   `README.md`, `.gitignore`, `docs/`, `tools/`, `temp/`, `resources/` and `src/`.

2. **`Indra.Ocr.Pdf` split out of `Core`.** The PDFium NuGet package moved to a new
   class library. It is the only project that references PDFium. Reference direction is
   `Pdf → Core` (it needs `GrayImage`), never the reverse.

3. **`Core` lost its image codec.** ImageSharp moved out of `Core` entirely. The hosts
   (`Cli`, `Viewer`) now decode image files, and the shared decoder lives in
   `src/Shared/ImageFiles.cs`, which is *linked* into `Cli`, `Viewer` and the test project
   (`<Compile Include="..\Shared\ImageFiles.cs" Link="Hosting\ImageFiles.cs" />`) so there is
   a single implementation without adding a fifth project.

4. **`Core` is fed pixels, not files.** New input contract
   `Indra.Ocr.Imaging.BitmapData`:

   ```csharp
   public readonly ref struct BitmapData
   {
       public int Width { get; }
       public int Height { get; }
       public int Stride { get; }                 // bytes per row, >= Width
       public ReadOnlySpan<byte> Data { get; }    // 8-bit single channel
       public ReadOnlySpan<byte> Row(int y);
       public byte this[int y, int x] { get; }
   }
   ```

   The `ref struct` shape guarantees a borrowed view can never be stored on the heap, and
   `Stride` lets callers pass padded buffers (e.g. a PDFium bitmap) with no copy.
   `GrayImage` stays the *owning*, tightly-packed working type; `GrayImage.From(BitmapData)`
   copies the borrowed pixels in and `GrayImage.AsBitmap()` views them out.

   `Data` is `ReadOnlySpan<byte>` (the sketch in the request had `Span<byte>`) because `Core`
   only ever *reads* its inputs.

## Dependency graph

```
Cli ─┐                ┌─ ImageSharp
     ├─→ Core ←─ Pdf ─┘  (PDFium)
Viewer ┘      (ONNX)
```

`Core` depends on `Microsoft.ML.OnnxRuntime` only — no ImageSharp, no PDFium.

## Why grayscale only

The Python reference is grayscale end to end:

- images: `cv2.imreadmulti` / `cv2.imread(..., IMREAD_GRAYSCALE)` (`pipeline.read_images`);
- PDF pages: `page.render(scale=dpi/72, grayscale=True)` (`pipeline.load_pages`,
  `document._render`) — pypdfium2 asks PDFium for an 8-bit grey bitmap, so PDFium performs the
  colour conversion and nothing needs decoding afterwards.

So a single-channel 8-bit buffer is the complete input language of the pipeline.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → 17 passed.
- `BitmapDataTests` covers padded rows, `stride < width` and short-buffer rejection.
- `ImageDecodeTests` still matches the OpenCV reference through the relocated decoder.

## Notes

- `SixLabors.ImageSharp` advisories (NU1902/NU1903) are suppressed in
  `Directory.Build.props`: 2.1.13 is the last Apache-2.0 release and the fixes only landed in
  3.x, which is under the Six Labors Split License. Accepted knowingly.
