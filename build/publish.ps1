# Publish the two hosts as native AOT binaries for one RID, zip them, and smoke them.
# See docs/290-aot.md and docs/340-nuget-packages.md.
#
# Usage:
#   build/publish.ps1 -Rid win-x64 [-Configuration Release] [-Output temp/publish]
#                     [-SkipSmoke] [-DryRun]
#
# The RID has to be the machine's: Native AOT compiles with the platform's own linker, so it does
# not cross-compile.  The release workflow gives each of the four RIDs its own runner (win-x64 on
# windows-latest, win-arm64 on windows-11-arm, linux-x64 on ubuntu-24.04, linux-arm64 on
# ubuntu-24.04-arm) and calls this script once on each.
#
# Layer 1 of the three release gates (docs/340-nuget-packages.md): $env:INDRA_OCR_FAKE_AOT
# republishes with `-p:PublishAot=false -p:IndraOcrFakeAot=true --self-contained` for machines that
# have no platform toolchain -- this one, and any developer's clone.  The stand-in is not AOT (the
# JIT still compiles what the trimmer keeps), so the artifacts are named ..._notaot and the zip
# name carries it too; pack/push and the release refuse the suffix.
#
# What runs, in order:
#   1. dotnet publish of src/Indra.Ocr.Cli into temp/publish/<rid>/cli and of src/Indra.Ocr.Viewer
#      into temp/publish/<rid>/viewer -- one folder each, because publish owns its output folder
#      (the single-file bundler empties it) and because a trimmed publish would otherwise write
#      indra-ocr.dll twice, once as the CLI's app and once as the library the viewer links;
#   2. the zip temp/publish/indra-ocr-sharp_<rid>[_notaot].zip: both folders merged, minus their
#      symbol files (*.pdb, *.dbg), plus LICENSE and NOTICE, at the zip's root -- for native AOT
#      that is two executables and the native libraries they share, which is what a release carries;
#   3. build/smoke-win.ps1 (or build/smoke-linux.sh) against temp/publish/<rid>: the CLI on the
#      committed samples, then the viewer over HTTP.
#
# The logs go to temp/logs/ (publish-<rid>-<host>.binlog), which the workflows upload when a step
# fails.
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$Configuration = "Release",
    [string]$Output = "temp/publish",
    [switch]$SkipSmoke,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"

$root = (Resolve-Path "$PSScriptRoot/..").Path
$rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64")
if ($rids -notcontains $Rid) {
    Write-Host "[publish] FAIL unknown RID '$Rid'; the release has $(($rids -join ', '))"
    exit 1
}

$fake = [bool]$env:INDRA_OCR_FAKE_AOT
$suffix = if ($fake) { "_notaot" } else { "" }
$output = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$dir = Join-Path $output $Rid
$stage = Join-Path $output "$Rid-stage"
$zip = Join-Path $output "indra-ocr-sharp_$Rid$suffix.zip"
$logs = Join-Path $root "temp/logs"

# One publish folder per host, in the order that goes into the zip.
$hosts = @(
    @{ Project = "Indra.Ocr.Cli"; Folder = "cli" },
    @{ Project = "Indra.Ocr.Viewer"; Folder = "viewer" }
)

function Step([string]$Text) {
    Write-Host ""
    Write-Host "[publish] ==== $Text"
}

function Die([string]$Text) {
    Write-Host "[publish] FAIL $Text"
    exit 1
}

function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    Write-Host "[publish]   $Exe $($Arguments -join ' ')"
    if ($DryRun) {
        Write-Host "[publish]   (dry run: not executed)"
        return
    }

    # Native tools write progress and warnings to stderr, and Windows PowerShell turns a native
    # stderr line into a terminating error while ErrorActionPreference is Stop; it is relaxed for
    # the call itself and the exit code is what decides.
    $ErrorActionPreference = "Continue"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        Die "$Exe exited with $LASTEXITCODE (see the logs in $logs)"
    }
}

New-Item -ItemType Directory -Force -Path $logs | Out-Null

Step "the target ($Rid, $Configuration)"
if ($fake) {
    Write-Host "[publish] INDRA_OCR_FAKE_AOT is set: publishing the trimmed stand-in, NOT AOT."
    Write-Host "[publish] The artifacts are named ...$suffix and the release refuses them."
} else {
    Write-Host "[publish] Native AOT (PublishAot is set by the two host projects)."
}
Write-Host "[publish] folder: $dir/cli and $dir/viewer"
Write-Host "[publish] zip:    $zip"
Write-Host "[publish] binlog: $logs/publish-$Rid-<host>.binlog"

Step "an empty folder"
if (Test-Path $dir) {
    Write-Host "[publish] removing $dir"
    if (-not $DryRun) { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }
}
if ((Test-Path $zip) -or (Test-Path $stage)) {
    Write-Host "[publish] removing the previous zip/stage"
    # -ErrorAction SilentlyContinue: one of the two is usually already gone.
    if (-not $DryRun) { Remove-Item -Recurse -Force $zip, $stage -ErrorAction SilentlyContinue }
}
if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

# PublishAot is a property the projects set, so the AOT run needs no switches; the stand-in turns it
# back off and asks the two hosts for trimming (-p:IndraOcrFakeAot, which they turn into
# PublishTrimmed: as a global property it would reach the libraries' netstandard2.0 target, where
# trimming does not exist).  Single file as well, so the stand-in's two folders merge the way native
# AOT's do -- one executable per host plus the native libraries they share, no second indra-ocr.dll.
function Publish-Args([string]$Project, [string]$Out, [string]$Binlog) {
    $arguments = @(
        "publish", $Project,
        "-c", $Configuration,
        "-r", $Rid,
        "-o", $Out,
        "--self-contained", "true",
        "-v:n", "-bl:$Binlog"
    )
    if ($fake) {
        $arguments += @("-p:PublishAot=false", "-p:IndraOcrFakeAot=true", "-p:PublishSingleFile=true")
    }
    return $arguments
}

foreach ($hostSpec in $hosts) {
    $hostProject = $hostSpec.Project
    Step "dotnet publish $hostProject ($Rid) -> $($hostSpec.Folder)"
    $hostBinlog = Join-Path $logs "publish-$Rid-$hostProject.binlog"
    $hostOut = Join-Path $dir $hostSpec.Folder
    Invoke-Tool "dotnet" (Publish-Args (Join-Path $root "src/$hostProject/$hostProject.csproj") `
                                      $hostOut $hostBinlog)
}

Step "the zip"
if ($DryRun) {
    Write-Host "[publish]   (dry run: the folders are not there to zip)"
} else {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $kept = 0
    foreach ($hostSpec in $hosts) {
        $hostOut = Join-Path $dir $hostSpec.Folder
        if (-not (Test-Path $hostOut)) { Die "nothing was published into $hostOut" }
        Get-ChildItem $hostOut -Recurse -File |
            Where-Object { $_.Extension -notin ".pdb", ".dbg" } |
            ForEach-Object {
                $relative = $_.FullName.Substring($hostOut.Length + 1)
                $target = Join-Path $stage $relative
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                Copy-Item $_.FullName $target -Force
                $kept++
            }
    }
    Copy-Item (Join-Path $root "LICENSE") $stage
    Copy-Item (Join-Path $root "NOTICE") $stage
    Write-Host "[publish] $kept published file(s) (symbol files dropped) + LICENSE + NOTICE"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal -Force
    Remove-Item -Recurse -Force $stage
    Write-Host "[publish] $zip ($((Get-Item $zip).Length) bytes)"
}

if ($SkipSmoke) {
    Step "the smoke (skipped: -SkipSmoke)"
} elseif ($DryRun) {
    Step "the smoke (skipped: dry run)"
} elseif ($env:OS -eq "Windows_NT") {
    Step "the smoke (build/smoke-win.ps1)"
    & (Join-Path $root "build/smoke-win.ps1") -Dir $dir
    if ($LASTEXITCODE -ne 0) { Die "the smoke failed (exit $LASTEXITCODE)" }
} else {
    Step "the smoke (build/smoke-linux.sh)"
    & (Join-Path $root "build/smoke-linux.sh") --dir $dir
    if ($LASTEXITCODE -ne 0) { Die "the smoke failed (exit $LASTEXITCODE)" }
}

Step "the artifacts"
if (Test-Path $output) {
    Get-ChildItem $output -File | Sort-Object Name | ForEach-Object {
        Write-Host ("[publish]   {0,12}  {1}" -f $_.Length, $_.Name)
    }
}
Write-Host ""
Write-Host "[publish] OK: $Rid published$(if ($fake) { ' (stand-in, not AOT)' } else { ' as native AOT' })"
