# 330 — The product line lives in the build, not in a C# constant

## Goal

`ProductInfo` (`src/Indra.Ocr.Core/ProductInfo.cs`) held two `const string`s — `Name = "indra-ocr"`,
`Version = "0.3.2"` — read by three places: the CLI's `--version`, the viewer's `--version` and the
viewer's `/info`. The version was therefore a file of its own, next to a comment that had outlived
its purpose ("placeholder … before real code lands in step 020+"), and it lived in `Core`, which
otherwise knows nothing about a host.

Now the build carries it: `<Product>` and `<Version>` in `src/Directory.Build.props`, and each host
reads the values back from **its own assembly** where it needs them.

## Why the *product* name, not the assembly name

The reference prints `indra-ocr {__version__}` in both hosts (`cli.py:72`, `viewer.py:609`), and the
viewer's `/info` reports the same `__version__` (`viewer.py:262`, defined in `__init__.py:22`). The
viewer's assembly, however, is `indra-ocr-viewer` — its csproj sets `AssemblyName`. Reading
`Assembly.GetName().Name` would make the viewer print `indra-ocr-viewer 0.3.2` and break the
byte-for-byte match that `cli_parity.py`'s `version` case, `viewer_cli_parity.py` and
`ArgparseTests.Version_prints_the_product_line` all check.

So the name read back is `AssemblyProductAttribute.Product`, set to `indra-ocr` for every project
in the solution: the product is one thing, the executables are two.

## The `+<sha>` that must not appear

`AssemblyInformationalVersionAttribute` is the SDK's `$(Version)` plus `+$(SourceRevisionId)`
whenever SourceLink or CI fills that in. The version string is part of the parity surface, so
`IncludeSourceRevisionInInformationalVersion` is `false` and the attribute is exactly `0.3.2`.

## Where the values are read

`src/Indra.Ocr.Cli/ArgumentParsing/ProductAttributes.cs` — in the CLI project because the viewer is
built on top of it, which is the reference's own shape (`viewer.py` imports from `cli.py`):

| member | assembly attribute | value |
|---|---|---|
| `Name(Assembly)` | `AssemblyProductAttribute` | `indra-ocr` |
| `Version(Assembly)` | `AssemblyInformationalVersionAttribute` | `0.3.2` |
| `VersionLine(Assembly)` | both | `indra-ocr 0.3.2` |

Each host passes its own assembly — `typeof(CliParser).Assembly`, `typeof(ViewerParser).Assembly` —
so the value is the one that host was built with. The viewer's `--version` and its `/info` are the
same `__version__` because they are the same assembly.

Both attributes are read with `GetCustomAttribute<T>()`; the trim/AOT analyzers accept it as written
(the build stays at 0 warnings with `PublishAot` on), so nothing is suppressed.

## Verification

- `dotnet build src/Indra.Ocr.slnx` — 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` — **218 passed** (the same count: no test was added, the
  existing `ProductSmokeTests` changed what it reads).
- `indra-ocr.exe --version` and `indra-ocr-viewer.exe --version`, captured through `subprocess`
  (not the shell) — `b'indra-ocr 0.3.2\r\n'` and an empty stderr, both hosts.
- `GET /info` against a live viewer (`--model-dir resources/models`, then stopped) —
  `{"tesseract":false,"version":"0.3.2","defaults":{…}}`.
- `cli_parity.py` 83/83 (the `version` case included), `parity_sweep.py` 24/24,
  `viewer_cli_parity.py` 20/20, `viewer_parity.py` 12 cases + `stderr`.
- A trimmed, self-contained publish of **both** hosts (`-p:PublishAot=false -p:PublishTrimmed=true
  -p:SelfContained=true -r win-x64`, step `290`'s fallback, so the values are read back with the
  trimmer on): both print `indra-ocr 0.3.2`, and that viewer's `/info` answers
  `"version":"0.3.2"`. Reading the product line from the assembly at runtime is exactly what
  trimming could have broken (the attribute could be dropped with the metadata), so it is checked
  rather than reasoned about.

## Notes

- The first attempt wrote the reason into an XML comment that spelled the version flag with two
  leading hyphens. XML forbids `--` inside a comment, so `Directory.Build.props` failed to load
  (`MSB4024`) — and because it is the file that sets `TargetFramework`, every project then reported
  `NETSDK1207`/`NETSDK1210` ("not supported for the target framework") instead. A broken
  `Directory.Build.props` does not announce itself as broken. The comment now says "the version
  flag".
- Deleting `ProductInfo.cs` also takes `Core`'s last product constant with it: the product line is
  a host concern, and `Core` keeps none.
- `ProductSmokeTests` keeps its name (`docs/245-host-layout.md`, `docs/250-reference-tests.md`
  refer to it) and now asserts the assembly's product line for both hosts, plus the bare version
  the viewer's `/info` answers with.

## Files

- `src/Directory.Build.props` — `Product`, `Version`, `IncludeSourceRevisionInInformationalVersion`.
- `src/Indra.Ocr.Cli/ArgumentParsing/ProductAttributes.cs` — new.
- `src/Indra.Ocr.Core/ProductInfo.cs` — deleted.
- `src/Indra.Ocr.Cli/CliParser.cs`, `src/Indra.Ocr.Viewer/ViewerParser.cs`,
  `src/Indra.Ocr.Viewer/Viewer.cs` — the three call sites.
- `src/Indra.Ocr.Tests/ProductSmokeTests.cs` — reads the assemblies.
- `docs/325-readme.md` — the sentence that named `ProductInfo`.
