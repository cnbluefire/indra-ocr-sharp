# 340 — The three NuGet packages, and the release that carries them

## Goal

The port is packaged, not just buildable: three NuGet packages that a consumer can reference from
`net10.0` **or** from .NET Framework (`netstandard2.0`, `docs/335-multitarget-netstandard20.md`),
and a release process that produces them together with the two hosts as native AOT binaries for the
four RIDs — locally through the scripts in `build/`, and on a runner through
`.github/workflows/release.yml`. A release attaches the two Windows zips: the Linux legs are built and
smoked like any other and stop at their artifacts, because of the glibc a Linux NativeAOT binary
links (`docs/290-aot.md`). `build/pack.sh`/`build/pack.ps1`, the package checks, the publish and
platform smoke (`355`) and `build/release.sh` are all steps of the same story, which is why they share
this file.

Everything below runs from a plain checkout with the .NET SDK: no MSBuild task, no `nuget.exe`, no
token, and no network except NuGet (for the packages' own dependencies) and, on a first run, Hugging
Face.

## The three packages

| Package id | Ships | Project |
|---|---|---|
| `Bluefire.Indra.Ocr.Sharp.Core` | `Indra.Ocr.Core.dll` | `src/Indra.Ocr.Core` |
| `Bluefire.Indra.Ocr.Sharp.Pdf` | `Indra.Ocr.Pdf.dll` | `src/Indra.Ocr.Pdf` |
| `Bluefire.Indra.Ocr.Sharp` | `Indra.Ocr.dll` | `src/Indra.Ocr` |

The three library projects are packed; `Indra.Ocr.Cli`, `Indra.Ocr.Viewer` and `Indra.Ocr.Tests` are
`IsPackable=false` (a host ships as a zip, `355`, not as a package).

The id carries a `Bluefire.` prefix and the family name `Indra.Ocr.Sharp{,.Core,.Pdf}`, so
`Bluefire.Indra.Ocr.Sharp` is the image decoder and the two `.Core`/`.Pdf` packages hang off it —
but the **assembly names and namespaces inside are unchanged** (`Indra.Ocr.Core.dll`,
`Indra.Ocr.dll`, namespaces `Indra.Ocr.*`, `Indra.Ocr.Images`). The prefix is only there so a
package does not squat an official-sounding id.

## The manifest is a nuspec, not the project

`build/Indra.Ocr.Sharp*.nuspec` — one per package — is the only description of what a package
holds, and `NuspecFile` makes NuGet use it exclusively. The project files carry only the values that
change per release:

```xml
<PackageId>Bluefire.Indra.Ocr.Sharp.Core</PackageId>
<IncludeBuildOutput>false</IncludeBuildOutput>
<NuspecFile>$(MSBuildThisFileDirectory)..\..\build\Indra.Ocr.Sharp.Core.nuspec</NuspecFile>
<NuspecBasePath>$(MSBuildThisFileDirectory)..\..\build</NuspecBasePath>
<NuspecProperties>packageVersion=$(PackageVersion);configuration=$(Configuration)</NuspecProperties>
```

- **`IncludeBuildOutput=false`** because the nuspec's `<files>` list is the whole truth. Without it
  the SDK adds the project's own build output as well — one entry per target framework — and the two
  collide on the same `lib/<tfm>/<assembly>.dll` destination (`NU5050`).
- **`NuspecBasePath`** pins the base the nuspec's relative `<file src>` paths resolve against, so
  `dotnet pack` from the repository root and `nuget.exe pack build\X.nuspec` agree on every path.
- **The tokens**: `$packageVersion$` and `$configuration$` come from `NuspecProperties`. The token
  is deliberately *not* called `$version$` — that name is reserved, and NuGet fills it from the
  project's own version, which ignores the flavor (`PackageVersion` is not `Version`, below).
- **Every `<file>` is explicit.** A wildcard would pull in whatever else is in the output folder:
  `Indra.Ocr.Pdf`'s output holds `Indra.Ocr.Core.dll` (its `ProjectReference`), which must not ride
  along in the Pdf package.
- **The `.pdb` files are listed on purpose.** A symbols package can only be built from files the
  nuspec names (`NU5005` otherwise); NuGet then routes them by extension, so the `.pdb` entries end
  up in the `.snupkg` and the assemblies stay in the `.nupkg`.

Each package ships its assemblies at both `lib/net10.0` and `lib/netstandard2.0`, plus `LICENSE` and
`NOTICE` at the root; `Core` also ships `THIRD-PARTY-LICENSES/MODIFIED-OPENRAIL-M.txt`, because the
models it downloads are not Apache-2.0. The licence itself is an SPDX expression
(`<license type="expression">Apache-2.0</license>`), not a file reference: a path would have to
resolve against `NuspecBasePath`. There is no `build/` or `tools/` directory in any package, no
build script, and no analyzer — the packages are plain libraries (that is one of the checks,
below).

## Dependency groups

Per target framework, with ranges the port actually builds against:

| Package | `net10.0` group | `netstandard2.0` group |
|---|---|---|
| `.Core` | `Microsoft.ML.OnnxRuntime [1.31.0,1.32.0)` | same, plus `System.Memory 4.5.5`, `System.Runtime.CompilerServices.Unsafe 6.0.0`, `System.Text.Json 8.0.5` |
| `.Pdf` | `.Core [$packageVersion$]`, `bblanchon.PDFium.{Linux,Win32,macOS} 156.0.8076` | same, plus `System.Memory 4.5.5`, `System.Runtime.CompilerServices.Unsafe 6.0.0` |
| `Indra.Ocr.Sharp` | `.Core [$packageVersion$]`, `SixLabors.ImageSharp [2.1.13,3.0.0)` | same, plus `System.Memory 4.5.5`, `System.Runtime.CompilerServices.Unsafe 6.0.0` |

- **ONNX Runtime is capped below 1.32**: its native library takes part in the numbers the pipeline
  produces (the fixed-point arithmetic, `expf`, the float32 kernels), so a consumer may take any
  1.31.x patch but not a minor upgrade. The build pins `1.31.0` (`Directory.Packages.props`).
- **PDFium is exact** (`156.0.8076`): the P/Invoke in `src/Indra.Ocr.Pdf/Native/PdfiumBindings.cs`
  is transcribed from that release's headers, and the ranges in the packages are the versions the
  port was verified against (`docs/335-multitarget-netstandard20.md`).
- **ImageSharp is capped below 3.0**: 2.1.13 is the last Apache-2.0 release, and the port decodes
  against 2.x's API.
- The three BCL packages exist for the `netstandard2.0` asset only; on `net10.0` they are in-box.
  `System.ValueTuple` is deliberately **not** in any group: .NET Framework 4.7+ would select it and
  collide with `mscorlib` (the .NET Framework consuming notes below say who needs it instead).

## The version

One place, `src/Directory.Build.props`:

```xml
<Version>1.1.2</Version>              <!-- the reference's parity version -->
<PackageFlavor>alpha.2</PackageFlavor>
<PackageVersion>$(Version)-$(PackageFlavor)</PackageVersion>
```

`PackageVersion` (`1.1.2-alpha.2`) is what the package files are named after, what the consumer
restores, and what the release tag carries (`v1.1.2-alpha.2`, a `--prerelease` because of the `-`).
`Version` stays the Python package's `__version__`, because both hosts
print it and the viewer reports it in `/info` — it is part of the parity surface
(`docs/330-product-version.md`). The major/minor track the reference, so a port of a newer upstream
release moves them and only `PackageFlavor` moves between our own pushes; it has to be bumped every
push, because a version once on nuget.org can never be replaced.

No script repeats the version: `build/pack.*` reads it out of the props and passes
`-p:SmokePackageVersion=` to the smoke consumer, `build/publish.*` names the zips after the RID
only, the workflow reads it in a `preflight` job, and `build/release.sh` reads it itself unless
`--version` says otherwise. The reads are anchored at the start of a line
(`^\s*<Version>([^<]*)</Version>`), so they can never pick up a mention of the tag in a comment.

## The build scripts

| Script | What it does |
|---|---|
| `build/pack.ps1`, `build/pack.sh` | `dotnet pack` the solution → `tools/scripts/test/check_nupkg.py` → `tools/nuget-smoke` on `net10.0` (and on `net462` where .NET Framework exists) |
| `build/publish.ps1`, `build/publish.sh` | `dotnet publish` both hosts natively → the per-RID zip → `build/smoke-win.ps1` / `build/smoke-linux.sh` |
| `build/smoke-win.ps1`, `build/smoke-linux.sh` | the published binaries themselves, with nothing but PowerShell / a shell and `curl` |
| `build/release.sh` | the GitHub release: assets, notes, `gh release create` (or `upload --clobber`) |

The pack and publish scripts take a configuration (`--configuration` / `-Configuration`), an output
folder and a dry run (`--dry-run` / `-DryRun`); the smoke scripts take the directory to smoke, a
model folder and a port. The `.sh` files are the Linux/macOS spelling of the same steps in the same
order, not a reduced version. The logs go to `temp/logs/` (`pack.binlog`,
`publish-<rid>-<Host>.binlog`, all with `-v:n`), which is what the workflows upload when a step
fails. `temp/` is gitignored; nothing a script writes is committed.

### `build/pack.*`

1. `dotnet pack src/Indra.Ocr.slnx -c Release -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg`
   — the **solution**, so the two hosts are compiled against the libraries that are being packed as
   well. Six files come out of it: three `.nupkg` and three `.snupkg`.
2. `tools/scripts/test/check_nupkg.py`, which checks those six files entry by entry (89 checks).
3. `tools/nuget-smoke`: a real consumer of the packages (85 checks per target runtime).

`--skip-smoke`, `--skip-net462`, `--model-dir`, `--output`, `--configuration`, `--dry-run`.

### `tools/scripts/test/check_nupkg.py`

The nupkg is a zip, so this needs nothing but the standard library: it opens each package and
checks the contract a consumer sees — the file names, `lib/<tfm>/<assembly>.dll` for all six
pairs, the licence files (including the OpenRAIL-M copy in `Core`), the `<dependencies>` groups per
target framework with their ranges, the `<id>`/`<version>`/`<license>` metadata, and that nothing
else is in there (no `build/`, no `tools/`, no stray assembly). Two details it has to get right:
the package's own metadata file (`package/services/metadata/core-properties/*.psmdcp`) is matched by
prefix and excluded from the exact comparison, since NuGet names it after a GUID; and target
framework names are normalized (`netstandard2.0`, `.NETStandard2.0`, `net10.0`) before comparing.

The contract is written out in the script (id → assembly, extra files, dependency groups), so
changing a nuspec means changing `check_nupkg.py`, `README.md`'s library section and this file
together.

### `tools/nuget-smoke`

A consumer project under `tools/`, `net10.0;net462`, that references the three **packages** from a
local feed (`tools/nuget-smoke/nuget.config` points at `../../temp/packages` and `<clear/>`s
everything else, so a published version of the same id can never satisfy it by accident) — never the
projects. It goes red exactly when the *packages* are wrong.

Two halves, so "the packages work" covers behaviour and surface:

- a **functional walk** over the committed samples (`build/smoke/latin.png`, `build/smoke/latin.pdf`,
  written by `tools/scripts/gen_smoke_samples.py`): both decoders, `ModelFolder.Resolve`, the
  pipeline and the PDF text layer;
- a **reflection walk** over every public type of the three assemblies that materialises every
  public member. Resolving those members loads every type their signatures name, which is the cheap
  way to catch an incomplete dependency group in a nuspec — that is what the `netstandard2.0`
  groups of `System.Memory`/`Unsafe`/`System.Text.Json` are for.

The .NET Framework half is what keeps the `netstandard2.0` assets honest. It needs two references a
consumer has to add itself (below), and the project says so in its own comments. It is run on
Windows only: no other machine has .NET Framework. `build/pack.sh` skips it with a line saying so on
Linux (`OS` is not `Windows_NT`); the release workflow packs on `windows-latest` precisely so the
`net462` half runs, and `ci.yml` packs on Linux, where it does not.

The smoke resolves its model folder through the public API, so without `--model-dir` it exercises
the Hugging Face cache and the download path — TLS, JSON and all — which is what a CI run does. That
is also why the release workflow does not cache the models: the download *is* part of what is being
verified.

### `build/publish.*`

`dotnet publish` of the two hosts for one RID, the zip, then the platform smoke.

- **One output folder per host**: `temp/publish/<rid>/cli` and `temp/publish/<rid>/viewer`. Publish
  owns its output folder (the single-file bundler empties it), and a trimmed publish would otherwise
  write `indra-ocr.dll` twice, once as the CLI's app and once as the library the viewer links —
  which is what produced `Entry point not found in assembly 'indra-ocr'` while this was being built.
- **The zip** `temp/publish/indra-ocr-sharp_<rid>.zip` merges both folders (CLI first), drops the
  symbol files (`*.pdb`, `*.dbg`) and adds `LICENSE` and `NOTICE` at the root. For native AOT that
  is two executables plus the native libraries they share (`onnxruntime`, `pdfium`) — the viewer is
  a separate executable, not a second copy of the pipeline.
- **The RID has to be the machine's**: Native AOT links with the platform's own toolchain and does
  not cross-compile. `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`; anything else is refused by
  name. The release workflow builds and smokes all four; only the two Windows zips are attached to a
  release, and `build/release.sh` refuses a Linux zip by name (`docs/290-aot.md`).

### `build/smoke-win.ps1`, `build/smoke-linux.sh`

Nothing but PowerShell / a shell and `curl`: the CLI on both committed samples (markdown, text,
JSON, `--version`, a missing input's error and exit code, `--out`) and then the viewer over plain
HTTP (`/info`, `/upload`, `/page`, the page JSON's shape) with the process stopped at the end. 23
checks per platform, one script per platform (no shared helper, so a runner only needs what its own
platform has).

The checks are deliberately coarse: both hosts are the port end to end (native AOT in a release,
the trimmed stand-in with `INDRA_OCR_FAKE_AOT`), and what this answers is "does the published binary
start, read a PDF and an image, write its output, and serve the viewer's API". The byte-for-byte
comparison against the reference lives in `tools/scripts/test/` and needs the Python environment,
which a release machine does not have.

The model folder is `resources/models` when it is there (a developer's clone); otherwise the
argument is left out and the hosts resolve the models themselves, which is what a release user does.
Versions are compared by prefix (`indra-ocr <version>`), and the value is read from
`src/Directory.Build.props` rather than repeated.

### `build/release.sh`

The last step, and the only one that talks to GitHub: `gh` takes the token from `GH_TOKEN` and the
repository from the checkout. It attaches the three `.nupkg`, the two Windows zips and the notes;
the three `.snupkg` are **not** release assets — they are the `push` job's, and a debugger finds
them on nuget.org by the package's own id and version.

1. `INDRA_OCR_FAKE_AOT` is refused outright, whatever the assets look like.
2. The version comes from `src/Directory.Build.props` unless `--version` says otherwise, and `-` in
   it makes the release a `--prerelease` (`0.3.2-alpha.2`).
3. The five assets are checked by name (`--packages`, default `temp/packages`; `--zips`, default
   `temp/publish`): three `.nupkg` and two `indra-ocr-sharp_<rid>.zip` (`win-x64`, `win-arm64`). A
   missing one is a failure, and so is an `indra-ocr-sharp_<rid>_notaot.zip`: the stand-in must
   never be released. An `indra-ocr-sharp_linux-*.zip` is a failure too, by name — a release asset
   cannot be taken back, and a Linux zip in the folder means the wrong one was passed (the `release`
   job downloads `zip-win-*` alone).
4. The notes are the commit log — `git log --oneline` since the previous tag when there is one
   (`git describe --tags --abbrev=0`), the whole log when there is not, with
   `--notes-start-tag <previous>` so GitHub's own generated notes follow on.
5. `gh release create v<version> <assets> --title v<version> --generate-notes --notes-file <notes>
   [--prerelease]`, or `gh release upload --clobber` when the release already exists — which is what
   a run that died after the push needs. That path also removes the assets a release an earlier run
   of this script should not have put there (`gh release delete-asset`): any `*.snupkg`, which belong
   to nuget.org, and any `indra-ocr-sharp_linux-*.zip`. That is how the first `v0.3.2-alpha.1`
   release loses the three symbol packages and the two Linux zips it carries.

`--dry-run` prints the whole plan (the assets, the notes' length, the exact `gh release create`
line) and creates nothing.

## The stand-in, and the gates against it

A machine without the platform toolchain (Windows: the MSVC linker + Windows SDK; Linux: clang)
cannot link native AOT. `INDRA_OCR_FAKE_AOT` makes `build/publish.*` publish a **trimmed
self-contained single-file** build instead:

```shell
dotnet publish ... -p:PublishAot=false -p:IndraOcrFakeAot=true -p:PublishSingleFile=true
```

`IndraOcrFakeAot` is turned into `PublishTrimmed` **by the two host projects**, not on the command
line: as a global property `PublishTrimmed=true` would reach the library projects' `netstandard2.0`
target too, where trimming does not exist (`NETSDK1124`). The stand-in gives the release its shape
(two executables, the native libraries, nothing else) without being AOT — the JIT still compiles
what the trimmer keeps — so it is marked everywhere:

1. `build/publish.*` says so in its banner, closes with `(stand-in, not AOT)`, and names the zip
   `indra-ocr-sharp_<rid>_notaot.zip`.
2. Every release-side job in `.github/workflows/release.yml` refuses the environment variable before
   it does any work.
3. `build/release.sh` refuses the environment variable *and* a `_notaot` asset by name.

## The workflows

`.github/workflows/ci.yml` — the quick check, on demand (`workflow_dispatch` only: nothing runs on a
push or on a pull request, so a branch cannot spend the repository's minutes):

- `test`: `dotnet test src/Indra.Ocr.slnx`.
- `pack`: `build/pack.sh` on Linux (pack, `check_nupkg.py`, the `net10.0` smoke — the `net462` half
  is skipped by design there).
- `hosts`: the stand-in publish (`INDRA_OCR_FAKE_AOT=1`, `--skip-smoke`) on all four RIDs
  `build/publish.*` knows — `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64` — which keeps both
  host projects and `build/publish.*` covered without the model download a release run pays for for
  real, and exercises each RID's own runtime pack on the architecture it belongs to
  (`docs/380-test-hosts.md`). The two Linux legs repeat what the release workflow's real AOT legs
  cover, without the download — and neither pair is a release asset (`docs/290-aot.md`).

`.github/workflows/release.yml` — manual (`workflow_dispatch`) and **dry by default**: pushing a
version to nuget.org cannot be undone, and neither can a published release.

| Job | Runs on | What it is |
|---|---|---|
| `preflight` | ubuntu | reads the version; asks nuget.org's flat-container index whether all three packages already have it |
| `test` | ubuntu | `dotnet test` |
| `publish` | one runner per RID (four) | `build/publish.ps1` / `build/publish.sh`: real AOT plus the platform smoke; only the Windows zips go on to the release |
| `pack` | windows | `build/pack.sh` (both smoke targets), then uploads the six files |
| `push` | ubuntu | nuget.org Trusted Publishing (OIDC → a short-lived API key), then `dotnet nuget push` per file with `--skip-duplicate` |
| `auth` | ubuntu | the same login, on a **dry run only**: proves the policy and `NUGET_USER` without publishing anything |
| `release` | ubuntu | `build/release.sh` with `GH_TOKEN` and `contents: write`, on a depth-0 checkout |

Details that are decisions, not accidents:

- **One runner per RID**, because Native AOT does not cross-compile: `windows-latest`,
  `windows-11-arm`, `ubuntu-24.04`, `ubuntu-24.04-arm`. The two Linux legs are built and smoked like
  the others and then stop: the `release` job downloads `zip-win-*` alone, so their zips stay job
  artifacts, which is what keeps the real Linux check without handing out a binary that fails on
  older distributions (`docs/290-aot.md`).
- **The order is "everything passes, then push"**: `push` needs `test`, `publish` and `pack`; the
  `publish` and `pack` halves run in parallel and neither happens before the other. A version already
  on nuget.org skips the push, and the release job then still runs (`!cancelled()` plus the results
  of every job before it) so a resumed run can finish the job.
- **`--skip-duplicate`** is what makes a re-run after a half-finished push possible.
- **The artifacts**: `zip-<rid>` per publish leg — all four, and only the two Windows ones reach the
  release — `packages` (six files) from
  the pack job, `logs-<job>` on failure — and always on the arm64 legs, the architecture nobody has
  locally.
- **Nothing is cached**: the models are resolved from the Hub, which is the path a release user
  takes.
- **The actions are the node24 majors** — `checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`,
  `download-artifact@v8`. Their node20 predecessors still run, but every job prints a deprecation
  warning while they do. The inputs these workflows pass are unchanged between the majors: their
  `action.yml` files only add inputs (`upload-artifact`'s `archive`, `download-artifact`'s
  `skip-decompress`/`digest-mismatch`, `setup-dotnet`'s `dotnet-channel`/`workloads`/`architecture`),
  and `checkout`'s list is identical. `setup-dotnet` v5's one breaking change is the node bump
  itself, which wants a runner of 2.327.1 or later.

The workflows only orchestrate. Choosing a runner, passing the version around and deciding what runs
after what is what they are for; the work itself is in `build/`, so the same steps can be run (and
are run) locally, and a workflow change can never change what a release *contains*.

### The nuget.org push needs no stored secret

The `push` job authenticates with **Trusted Publishing**: GitHub issues the job an OIDC token
(`permissions: id-token: write`), `NuGet/login@v1` exchanges that token with nuget.org for a
short-lived API key, and `dotnet nuget push` uses the key. One token buys one key and the key lasts an
hour, which is why the login step is the step immediately before the push, and why no `NUGET_API_KEY`
secret exists any more (if one is still configured on the repository, delete it: nothing reads it).
`id-token: write` has to sit on the **job**: a workflow-level `permissions:` block — this file's
`contents: read` — is otherwise the whole set of permissions, and GitHub would refuse the token.

The nuget.org half is a policy on the account that owns the packages — nuget.org → your username →
**Trusted Publishing** → add a policy. All four fields are case-insensitive, and the workflow file is
the **file name only** (`release.yml`, not `.github/workflows/release.yml`):

| field | value |
|---|---|
| Repository Owner | `cnbluefire` |
| Repository | `indra-ocr-sharp` |
| Workflow File | `release.yml` |
| Environment | *leave empty* — the job uses no GitHub Actions environment |

If the policy form asks who **owns** it, pick whoever owns `Bluefire.Indra.Ocr.Sharp*`: the individual
account, or the organization the packages belong to (an org-owned policy goes inactive while you are
not a member). Its **scopes** decide what the key may publish, so give it the three package ids —
`Bluefire.Indra.Ocr.Sharp*` covers `…Sharp`, `…Sharp.Core` and `…Sharp.Pdf`. A **private** repository
leaves the new policy "temporarily active for 7 days" until the first successful publish hands
nuget.org the repository's ID; a public repository's policy is active at once. If you ever add
`environment: release` to the job, set the policy's Environment to `release` in the same change — the
two must agree.

The GitHub half is one repository **variable** (Settings → Secrets and variables → Actions →
Variables — the repository's own, not an environment's):

| name | value |
|---|---|
| `NUGET_USER` | the nuget.org *profile name* (not the email address) that owns the three package ids |

The workflow reads `vars.NUGET_USER || secrets.NUGET_USER`, so a **secret** of the same name works
just as well and nothing else has to change — that is the one to use if you would rather the name did
not appear in the log. A variable is what it is by default because the name is public information.

Both jobs run a step called "the nuget.org profile name" before the login, which fails with these
instructions when neither is set. It is there because the login action's own message for that case
("Input required and not supplied: user") does not say whether the variable or the secret is the one
that is missing, or that a repository-level value was wanted at all.

The `auth` job is how you try that out. The push is skipped on a dry run, so the policy would
otherwise only ever be exercised by a release that had already published nothing yet — this job does
the login on the dry run instead and holds the key until it expires, publishing nothing. It is the
only job that runs on a dry run and has no dependents, so a policy that does not match, or a
`NUGET_USER` that is not set, turns a dry run red rather than failing a release half-way through; a
real run skips it. Creating the policy and the variable first is therefore the order that makes the
next dry run green.

## Running it all locally

```powershell
build/pack.ps1                       # six files in temp/packages, 89 + 85 (+85) checks
$env:INDRA_OCR_FAKE_AOT = "1"        # no platform toolchain here
build/publish.ps1 -Rid win-x64       # the zip plus 23 platform checks
```

```shell
bash build/pack.sh
INDRA_OCR_FAKE_AOT=1 bash build/publish.sh --rid linux-x64   # the leg the release workflow smokes
bash build/release.sh --dry-run      # the five assets, the notes, the gh command
```

On a machine with the toolchain, leave `INDRA_OCR_FAKE_AOT` unset and the same commands produce the
real thing: `dotnet publish` links a native binary and the zip is named without the suffix.

## Consuming the packages from .NET Framework

A .NET Framework project that references e.g. `Bluefire.Indra.Ocr.Sharp.Pdf` and
`Bluefire.Indra.Ocr.Sharp` gets the `netstandard2.0` assets, and needs three more things that the
packages cannot arrange for it (the smoke consumer, `tools/nuget-smoke`, is the worked example):

```xml
<PropertyGroup>
  <!-- The native libraries (onnxruntime, pdfium) ship x64/x86/arm64; say which one you are. -->
  <PlatformTarget>x64</PlatformTarget>
  <!-- System.Memory/Unsafe/Text.Json are newer than the framework's own assemblies. -->
  <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
  <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="[1.31.0,1.32.0)" />
  <PackageReference Include="System.ValueTuple" Version="4.5.0" />
</ItemGroup>
```

- `Microsoft.ML.OnnxRuntime` directly, because its native library is copied by a `build/` target
  that runs for a direct reference only: the package has no `buildTransitive` half for
  `netstandard2.0`, so a transitive reference gives you the managed assembly without
  `onnxruntime.dll` beside it.
- `System.ValueTuple` on .NET Framework 4.6.x only (4.7+ has the type). It stays out of the
  packages' `netstandard2.0` group on purpose: 4.7/4.8 would select it and collide with `mscorlib`.
- The two binding-redirect properties, because `Assembly.Load` of the `netstandard2.0`
  assets' dependencies otherwise picks the wrong assembly version.

Two things a consumer should know before choosing this:

- **Functionally identical, not bit-identical.** On .NET Framework there is no fused multiply-add
  and no CRT `expf` behind `MathF.Exp`, and `CharUnicodeInfo`'s tables are older than .NET 10's, so
  the same input can differ in the last bits. The `net10.0` asset is the reference
  (`docs/335-multitarget-netstandard20.md`).
- **ImageSharp 2.1.13 carries advisories** (`NU1902`/`NU1903`/`NU1904`) that are only fixed in 3.x,
  which is under the Six Labors Split License. The port takes that knowingly — see the comment in
  `src/Directory.Build.props` — and a consumer sees the same warnings, which is why they are
  suppressed there too and written down here.

## Verification

- `build/pack.ps1` and `build/pack.sh`: 89 checks in `check_nupkg.py`, and the smoke walks all 85
  checks on `net10.0` and 85 on `net462` (the `netstandard2.0` assets on .NET Framework 4.6.2).
- `build/publish.ps1 -Rid win-x64` with `INDRA_OCR_FAKE_AOT=1`: exit 0, the zip built, and
  `build/smoke-win.ps1` `OK (23 checks)` against both published binaries (a real AOT publish needs
  the platform toolchain, which this machine does not have, `docs/290-aot.md`).
- `build/release.sh --dry-run`: the five assets, the notes (the whole log with no tag; the commits
  since the tag when there is one, with `--prerelease` and `--notes-start-tag`), and exit 0 — plus
  its four refusals (the environment variable, a `_notaot.zip`, a missing asset, a Linux zip) each
  exiting 1.
- Both workflows parsed with PyYAML and checked for the shape Actions needs; `git ls-files -s` shows
  `100755` on the four `build/*.sh` (and `.gitattributes` keeps `*.sh` LF, which matters on a
  Windows checkout).
- The AOT publish itself, the arm64 runners and the Hugging Face/ASP.NET Core/ImageSharp AOT paths
  are verified on GitHub (the release workflow, dry run first).

## Files

- `build/Indra.Ocr.Sharp.Core.nuspec`, `Indra.Ocr.Sharp.nuspec`, `Indra.Ocr.Sharp.Pdf.nuspec`
- `build/pack.ps1`, `build/pack.sh`, `build/publish.ps1`, `build/publish.sh`,
  `build/smoke-win.ps1`, `build/smoke-linux.sh`, `build/release.sh`
- `build/smoke/latin.png`, `build/smoke/latin.pdf` (committed samples; `gen_smoke_samples.py`)
- `tools/scripts/test/check_nupkg.py`, `tools/nuget-smoke/` (`NugetSmoke.csproj`, `Program.cs`,
  `nuget.config`)
- `.github/workflows/ci.yml`, `.github/workflows/release.yml`
- `src/Directory.Build.props` (version, flavor, the ImageSharp/advisory suppressions),
  `src/Indra.Ocr.*/Indra.Ocr.*.csproj` (packing properties; `IsPackable=false` on the hosts)
