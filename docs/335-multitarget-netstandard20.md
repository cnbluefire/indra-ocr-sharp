# 335 — The three libraries multi-target (`net10.0;netstandard2.0`)

## Goal

`Indra.Ocr.Core`, `Indra.Ocr` and `Indra.Ocr.Pdf` build two assets from now on: the `net10.0` one
the two hosts use, and a **`netstandard2.0`** one, so the port can be packaged for NuGet and
consumed from a .NET Framework application. The two executables and the test project stay
`net10.0`.

The net10.0 asset must remain exactly what it was — same code paths, same byte-for-byte parity —
and the ns2.0 asset has to be the same port, *functionally*: it is verified by running it on
**.NET Framework 4.6.2** and comparing every digest against the net10.0 run of the same program
(below).

## The multi-target spelling

`TargetFrameworks` (plural) with `TargetFramework` **cleared** in the three csproj files:

```xml
<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>
<TargetFramework></TargetFramework>
```

The shared `src/Directory.Build.props` sets `TargetFramework` (to `net10.0`) for every project, and
the SDK only treats a project as a cross-targeting build when that property is *empty*
(`Microsoft.NET.Sdk`'s `Sdk.targets`). Clearing it next to `TargetFrameworks` is therefore what turns
the outer build into a cross-targeting one; without it a single target framework wins and
`TargetFrameworks` is ignored.

`IsAotCompatible` is set for `net10.0` only:

```xml
<IsAotCompatible Condition="'$(TargetFramework)' == 'net10.0'">true</IsAotCompatible>
```

netstandard2.0 is not an AOT-capable target and the SDK reports `NETSDK1210` for it on every build
otherwise. The trim/AOT analyzers therefore still run where they mean something (the net10.0 asset,
which is what `PublishAot` compiles, `docs/290-aot.md`).

## The compat layer

`src/Compat/NetStandard20.cs` is compiled into all three libraries (one linked `<Compile>` item
each) and is entirely `internal`, so no shim can leak into a consumer's app or into a NuGet package
surface. It carries two kinds of thing:

- **Polyfills for a missing type** — `System.MathF` (netstandard2.1) and the members a source
  generator cannot add to an *existing* BCL type.
- **`#if`-dispatched helpers** — `Guard` (`ArgumentNullException.ThrowIfNull` …), `Bits`
  (`BitConverter.SingleToInt32Bits`), `MathCompat.Clamp`, `ListCompat.AsSpan`,
  `UnicodeCompat.Category`, and a group of extension methods (`string.Contains(char)`,
  `Dictionary.TryAdd`, `List<T>.AddRange(ReadOnlySpan<T>)`, `Stream.ReadAsync(Memory<byte>,…)`, …)
  so that a call site reads identically on every target: where the framework has the member, its
  own instance member is found first and the extension is not compiled at all.

**Every `#if` names the real minimum version of the member it guards**, never a blanket
"not netstandard2.0":

| condition | member | why |
|---|---|---|
| `NET6_0_OR_GREATER` | `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrEmpty` | .NET 6 |
| `NET7_0_OR_GREATER` | `ObjectDisposedException.ThrowIf`, `Stream.ReadAtLeast` | .NET 7 |
| `NET8_0_OR_GREATER` | `List<T>.AddRange(ReadOnlySpan<T>)` | .NET 8 |
| `NET9_0_OR_GREATER` | `System.Threading.Lock` | .NET 9 |
| `NET5_0_OR_GREATER` | `CollectionsMarshal.AsSpan`, `HttpClient.GetStringAsync(string, ct)`, `HttpContent.ReadAsStreamAsync(ct)` | .NET 5 |
| `NETCOREAPP3_0_OR_GREATER` | `MathF.FusedMultiplyAdd` hardware FMA path, `File.Move(string, string, bool)` | .NET Core 3.0 |
| `NETSTANDARD2_1_OR_GREATER \|\| NETCOREAPP2_1_OR_GREATER` | `CharUnicodeInfo.GetUnicodeCategory(int)`, `BitConverter.*Bits`, `Math.Clamp`, `string.Contains(char)`, the array/`List` `Range` slicing | netstandard2.1 / .NET Core 2.1 |

The `||` matters: .NET 5 and later define the `NETCOREAPP*_OR_GREATER` symbols and **never**
`NETSTANDARD2_1_OR_GREATER`, so a condition that only names netstandard2.1 would silently take the
shim on net10.0 and change the reference build. Measured on net10.0, for the record:
`NETCOREAPP2_1_OR_GREATER` and `NETCOREAPP3_0_OR_GREATER` are true, `NETSTANDARD2_1_OR_GREATER` is
false.

A handful of call sites keep their own `#if` because the fallback is not a one-liner (all of them
in Core/Pdf, none in the two hosts):

| site | condition | fallback |
|---|---|---|
| `Imaging/CvMath.cs` | `NETCOREAPP3_0_OR_GREATER` | the fixed-point warp runs unfused (`FusedMultiplyAdd` is exact only on the net10.0 target — see below) |
| `Onnx/ModelDownload.cs` (3) | `NET5_0_OR_GREATER` / `NETSTANDARD2_1_OR_GREATER \|\| NETCOREAPP3_0_OR_GREATER` | `GetAsync`, `ReadAsStreamAsync()`, delete-then-move |
| `Onnx/OnnxScriptModel.cs` | `NETSTANDARD2_1_OR_GREATER \|\| NETCOREAPP2_1_OR_GREATER` | an explicit `for` over the array instead of `array[..n]` (`RuntimeHelpers.GetSubArray` does not exist below it) |
| `Pipeline/OrtOcr.cs` | `NET7_0_OR_GREATER` | the file is read fully and the PDF magic is tested by hand instead of `Stream.ReadAtLeast` |
| `Pdf/Pdfium.cs` | `NET9_0_OR_GREATER` | `object` instead of `System.Threading.Lock` |

PolySharp 1.16.0 (a source generator, ns2.0 target only) covers the *language-level* polyfills —
`init` accessors, `record`, `Index`/`Range`, collection expressions, `[CallerArgumentExpression]`,
`[MemberNotNull]`, … — so the compat file only has to carry what a generator cannot add to an
existing BCL type. `System.Memory`, `System.Runtime.CompilerServices.Unsafe` and (for Core)
`System.Text.Json` are the BCL *types* that arrived after netstandard2.0. All of it is scoped to the
ns2.0 target, so the net10.0 build uses the framework's own types and none of these packages reach
it.

## PDF: PDFiumCore had to go

The wrapper the project used, **PDFiumCore**, has no `netstandard2.0` asset — its lowest is
netstandard2.1 — and netstandard2.1 is a target .NET Framework **cannot reference at all** (NuGet
refuses with `NU1202`; ns2.1 needs .NET Core 3.0+). So there was no version of "reference the
wrapper" that works for a ns2.0 asset, and the two half-solutions (bind the shim for ns2.0 only, or
drop PDF from the ns2.0 asset) were rejected: one binding for every target cannot drift, and the PDF
half is not optional for a 1:1 port.

`Indra.Ocr.Pdf` therefore binds PDFium itself, for **all** targets:

- `src/Indra.Ocr.Pdf/Native/PdfiumBindings.cs` — the 27 functions the port calls, transcribed from
  `fpdfview.h` / `fpdf_text.h` / `fpdf_edit.h` (they ship inside `bblanchon.PDFium.Win32`), keeping
  the C names (`FPDF_LoadPage`, `FPDFText_GetCharAngle`, `FPDFFormObj_GetObject`, …) so that every
  one of them can be grepped in the header. Handles are small internal classes over `IntPtr`; the
  pointer-returning entry points are the only ones that need a wrapper, because a P/Invoke cannot
  return a class. Everything is `internal`: the project's public surface
  (`PdfDocument`/`PdfPage`/`Pdfium`/`PdfConverter`) is unchanged.
- The native library comes from `bblanchon.PDFium.{Linux,Win32,macOS}` 156.0.8076 as **direct**
  dependencies — the same packages, at the same PDFium build, the wrapper pulled in — so both
  targets run the same physical PDFium, and the net10.0 renders are still byte-identical to the
  reference's (218 tests, the PDF ones included).
- `.NET Framework` native loading: the bblanchon targets copy the *matching* platform's
  `pdfium.dll` to the output root (and every other platform into `<arch>\` subdirectories), which
  the ordinary DLL search finds. `PdfiumNative.EnsureLoaded()` additionally preloads
  `<base>\x64\pdfium.dll` by full path when that copy exists, for a host that only got the
  sub-directory layout (the loader then hands the already-mapped module to the later
  name-based `DllImport`).

The entry points are `__cdecl` (`FPDF_CALLCONV` is `__stdcall` only when PDFium itself is built with
`FPDFSDK_EXPORTS`, which the shipped DLL is not), the data types are the headers'
(`FPDF_DWORD` = `unsigned long`, so `uint64` in the signatures PDFiumCore exposed as `UInt64`), and
`FPDF_TEXT_RENDERMODE` is the `int` enum with `INVISIBLE = 3`.

## What the ns2.0 asset cannot be

The ns2.0 build is a **functional** .NET Framework target, not a bit-exact one. Three things are
known to differ, and all three are properties of .NET Framework, not of the port:

1. **No fused multiply-add and no CRT `expf`.** `MathF.FusedMultiplyAdd` on .NET Framework is the
   unfused `x * y + z`, and `MathF.Exp` is `(float)Math.Exp(x)`. Both are load-bearing for the
   numpy transcriptions (`Numerics/NpyExp.cs` reproduces numpy's own `exp` kernel), so an
   intermediate can be up to 1 ulp away, which in principle can flip a detector threshold. The
   net10.0 build keeps the real instruction and stays the reference.
2. **.NET Framework's Unicode tables are older than .NET 10's.** `CharUnicodeInfo` is per-runtime
   data — measured over all 1 114 112 code points, .NET Framework 4.8 classifies 853 859 of them
   `Cn` where .NET 10 (and CPython's `unicodedata` 16.0.0, `docs/050-vocab.md`) says 819 533, and
   105 697 `Lo` against 136 477 — the code points assigned in Unicode 9.0 and later. The shim's
   *algorithm* is exact (below), so this only affects text that contains characters newer than the
   framework's tables; if it ever matters, `UnicodeCompat` can be generated from CPython's
   `unicodedata` the way `Text/NfcTables.cs` and `Text/BidiClasses.cs` are (`docs/145`,`docs/295`).
3. **`CollectionsMarshal.AsSpan` does not exist below .NET 5**, so `ListCompat.AsSpan` reads the
   list's *own* storage — its first field, `_items`, which is what the BCL's `AsSpan` reads — rather
   than copying it. That field's own declaration says why it can be relied on:

   ```csharp
   internal T[] _items; // Do not rename (binary serialization) [cDAC] [ComWrappers] : Contract depends on this exact name
   internal int _size;  // Do not rename (binary serialization) [cDAC] [ComWrappers] : Contract depends on this exact name
   ```

   The name is part of every serialized `List<T>` (`[Serializable]`, no `[OptionalField]`), and the
   debugger's cDAC contracts and the ComWrappers data contract resolve the field *by name* (the
   `[cDAC]`/`[ComWrappers]` markers were added on `main` after 10.0; `release/10.0` carries the
   serialization half alone). So both the name and, because it is declared first and holds the only
   reference field, its position are as fixed as anything in corelib. C# cannot name the field
   (`List<T>`'s indexer returns by value, so `ref list[0]` does not compile), so the list is
   reinterpreted as a class whose first field is the array, the way
   `Microsoft.Toolkit.HighPerformance` does it; the contract on the span is `CollectionsMarshal`'s —
   read-only, and the list must not be added to while the span is live. `temp/ns20-test` asserts the
   alias on both runtimes (an element written through the list is visible through the span).

## Verification

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings, 0 errors, both targets of all three libraries.
- `dotnet test src/Indra.Ocr.slnx` — **218 passed** (net10.0). The PDF tests exercise the new
  binding for real (they open, read the text layer of and render the fixture PDFs).
- The four harnesses: `cli_parity.py` 83/83, `parity_sweep.py` 24/24, `viewer_cli_parity.py` 20/20,
  `viewer_parity.py` 12 cases + `stderr` — byte-identical, so the wrapper's removal changed nothing
  observable on the reference target.
- `temp/ns20-test` (throwaway, dual-target `net462;net10.0`): the same program run twice — image
  decode + full OCR pipeline, and PDF text layer + render + converted page — over **10 cases**
  (Latin/tilted/two-column/CJK/blank/rotated90 images, text/image/rotated/invisible/cropped/broken
  PDFs, a math page, and two deliberate error inputs). Every line agrees between .NET Framework
  4.6.2 and .NET 10, down to the bit patterns of confidence values, the per-character digest of
  PDFium's output, the device affine and the render's SHA-256; the two error cases produce the same
  `InputError` text. So the ns2.0 asset does not merely load on .NET Framework — it computes the
  same answers on these inputs. The program is named `Indra.Ocr.Tests` so that Core's
  `InternalsVisibleTo` lets it call `ListCompat.AsSpan` directly and assert that the span *aliases*
  the list (an element written through the list is read back through the span): `1` on both
  runtimes, i.e. the layout read above is real on .NET Framework, not a copy that happens to agree.
- `temp/ns20-test --download` on .NET Framework, with a cold `HUGGINGFACE_HUB_CACHE` and no
  `--model-dir`: the first run of a real consumer (the Hub tree request, the downloads with their
  progress lines, the atomic move — all through the ns2.0 shims) fetched 8 files that are
  byte-identical to `resources/models`, and the pages then produced the same digests.
- `temp/ns20-probe/unicode-equiv`: the shim's astral branch  (`GetUnicodeCategory(char.ConvertFromUtf32(cp), 0)`) against `GetUnicodeCategory(int)` for all
  1 114 112 code points on one runtime — **0 mismatches**, i.e. the shim is a faithful translation
  (the table difference above is the runtime's, not the shim's).
- The compat file was also compiled into a probe project of its own, proving it does not depend on
  anything the three libraries happen to provide.

## Consuming the ns2.0 asset from .NET Framework (notes for the package)

- `System.ValueTuple` is needed on .NET Framework 4.6.2/4.6.1: the public surface uses tuples
  (`PdfDocument.PageSize`, `PdfPage.ExtractRuns`, …), netstandard2.0 has `ValueTuple` but .NET
  Framework only gained it in 4.7.
- ONNX Runtime's native `onnxruntime.dll` must end up next to the executable: .NET Framework does
  not probe `runtimes\`. The `Microsoft.ML.OnnxRuntime` package's own `build/netstandard2.0`
  targets do the copy, but they are `build/` (not `buildTransitive/`), so a consumer has to
  reference the package directly — or our package has to ship the equivalent targets when it is
  packaged.
- `PlatformTarget` must be a real architecture (`x64` here); the native libraries are RID-specific.
  Binding redirects for `System.Memory`/`Unsafe` are generated (`AutoGenerateBindingRedirects`).

## Files

- `src/Indra.Ocr.Core/Indra.Ocr.Core.csproj`, `src/Indra.Ocr/Indra.Ocr.csproj`,
  `src/Indra.Ocr.Pdf/Indra.Ocr.Pdf.csproj` — multi-target, per-target package references.
- `src/Compat/NetStandard20.cs` — new; linked into all three.
- `src/Indra.Ocr.Pdf/Native/PdfiumBindings.cs` — new; PDFiumCore's replacement.
- `src/Indra.Ocr.Pdf/PdfDocument.cs`, `PdfPage.cs`, `Pdfium.cs` — the binding's names instead of the
  wrapper's.
- `src/Directory.Packages.props` — `PDFiumCore` out, the three `bblanchon.PDFium.*` in as direct
  references; PolySharp/System.Memory/Unsafe/System.Text.Json for the ns2.0 target.
- `src/Indra.Ocr.Core/Imaging/CvMath.cs`, `Onnx/ModelDownload.cs`, `Onnx/OnnxScriptModel.cs`,
  `Pipeline/OrtOcr.cs` — the call-site `#if`s.
- `src/Indra.Ocr.Tests/PdfTests.cs` — the comment that named PDFiumCore.
