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

4. **`Core` is fed pixels, not files.** There is exactly **one** image type,
   `Indra.Ocr.Imaging.GrayImage`, a borrowed view:

   ```csharp
   public readonly ref struct GrayImage
   {
       public GrayImage(int width, int height, int stride, ReadOnlySpan<byte> data);
       public GrayImage(int width, int height, ReadOnlySpan<byte> data); // stride = width
       public int Width { get; }
       public int Height { get; }
       public int Stride { get; }                 // bytes per row, >= Width
       public ReadOnlySpan<byte> Data { get; }    // 8-bit single channel
       public ReadOnlySpan<byte> Row(int y);
       public byte this[int y, int x] { get; }
       public GrayImage Slice(int x, int y, int width, int height);
   }
   ```

   The `ref struct` shape guarantees a borrowed view can never be stored on the heap, and
   `Stride` lets callers pass padded buffers (e.g. a PDFium bitmap) with no copy.

   The type is a **view only**: it owns nothing and never allocates. Outputs are
   caller-provided buffers (`Span<byte>` + size), so nothing is copied and no allocation
   policy is imposed on `Core`. (An earlier draft had a second, owning `GrayImage` class next
   to the `BitmapData` view; the two were functionally the same thing, so the view absorbed
   the name and the owning type was deleted.)

   `Data` is `ReadOnlySpan<byte>` (borrowed input) because `Core` only ever *reads* its
   inputs.

   Because an image can never be stored on the heap there is no `List<GrayImage>`; the host
   decoders hand images out **one at a time** through a callback
   (`ImageFiles.ReadImages(path, consume)`). Nothing is cached, and multi-page files are
   processed page by page.

   Spans stay safe across the pipeline: the ONNX boundary copies pixels into ORT's own
   `OrtValue` buffer, and every `await` sits *after* that copy, so a `Span`/`ReadOnlySpan`
   never has to survive an async boundary.

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
- `dotnet test src/Indra.Ocr.slnx` → 20 passed.
- `GrayImageTests` covers padded rows, `stride < width`, short buffers, tight views,
  sub-views and binding a writable `Span<byte>`.
- `ImageDecodeTests` still matches the OpenCV reference through the relocated, streaming
  decoder.

## Notes

- `SixLabors.ImageSharp` advisories (NU1902/NU1903) are suppressed in
  `Directory.Build.props`: 2.1.13 is the last Apache-2.0 release and the fixes only landed in
  3.x, which is under the Six Labors Split License. Accepted knowingly.
