# Pack the three NuGet packages, check what each one holds and run the smoke consumer against them.
# See docs/340-nuget-packages.md.
#
# Usage:
#   build/pack.ps1 [-Configuration Release] [-Output temp/packages] [-ModelDir <dir>]
#                  [-SkipSmoke] [-SkipNet462] [-DryRun]
#
# The version is not an argument: it comes from src/Directory.Build.props (Version + PackageFlavor),
# which is also what the .nuspecs substitute into the packages, so the three packages, their file
# names and everything downstream carry one version and nobody has to type it twice.
#
# What runs, in order:
#   1. dotnet pack src/Indra.Ocr.slnx -c Release -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg
#      (the solution, so the two hosts are compiled against the libraries that are being packed too);
#   2. tools/scripts/test/check_nupkg.py, which checks the six files entry by entry (clean layout,
#      licence files, dependency groups per target framework, no build/ or tools/ directory);
#   3. tools/nuget-smoke, a consumer that references the packages -- never the projects -- and walks
#      their public surface, on net10.0 and (on Windows) on net462 against the netstandard2.0 assets.
#
# The logs go to temp/logs/ (pack.binlog), which the workflows upload when a step fails.
param(
    [string]$Configuration = "Release",
    [string]$Output = "temp/packages",
    [string]$ModelDir = "",
    [switch]$SkipSmoke,
    [switch]$SkipNet462,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"

$root = (Resolve-Path "$PSScriptRoot/..").Path
$packages = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$logs = Join-Path $root "temp/logs"
$smoke = Join-Path $root "tools/nuget-smoke"
$assets = Join-Path $root "build/smoke"

function Step([string]$Text) {
    Write-Host ""
    Write-Host "[pack] ==== $Text"
}

function Die([string]$Text) {
    Write-Host "[pack] FAIL $Text"
    exit 1
}

# Runs one external command, logging it first.  -DryRun only logs (the workflow's dry runs and a
# quick look at what a release would do).
function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    Write-Host "[pack]   $Exe $($Arguments -join ' ')"
    if ($DryRun) {
        Write-Host "[pack]   (dry run: not executed)"
        return
    }

    # Native tools write progress and warnings to stderr, and Windows PowerShell turns a native
    # stderr line into a terminating error while ErrorActionPreference is Stop; it is relaxed for
    # the call itself and the exit code is what decides.
    $ErrorActionPreference = "Continue"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        Die "$Exe exited with $LASTEXITCODE"
    }
}

function Find-Python {
    if ($env:INDRA_OCR_PYTHON) { return $env:INDRA_OCR_PYTHON }

    $venv = Join-Path $root "tools/pyenv/Scripts/python.exe"
    if (Test-Path $venv) { return $venv }

    foreach ($name in @("python", "python3")) {
        $found = Get-Command $name -ErrorAction SilentlyContinue
        if ($found) { return $found.Source }
    }

    Die "no Python found; set INDRA_OCR_PYTHON or run tools/scripts/setup-pyenv.ps1"
}

New-Item -ItemType Directory -Force -Path $logs | Out-Null

Step "the version (src/Directory.Build.props)"
$props = Get-Content -Raw (Join-Path $root "src/Directory.Build.props")
$version = [regex]::Match($props, "(?m)^\s*<Version>([^<]+)</Version>").Groups[1].Value
$flavor = [regex]::Match($props, "(?m)^\s*<PackageFlavor>([^<]+)</PackageFlavor>").Groups[1].Value
if (-not $version) { Die "src/Directory.Build.props has no <Version>" }
$packageVersion = if ($flavor) { "$version-$flavor" } else { $version }
Write-Host "[pack] PackageVersion $packageVersion ($version + $flavor)"

Step "the package folder ($packages)"
if (Test-Path $packages) {
    $stale = @(Get-ChildItem $packages -File)
    Write-Host "[pack] removing the folder and its $($stale.Count) file(s), so nothing stale survives"
    if (-not $DryRun) {
        Remove-Item -Recurse -Force $packages
    }
}
if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $packages | Out-Null
}

Step "dotnet pack ($Configuration)"
$binlog = Join-Path $logs "pack.binlog"
Write-Host "[pack] binlog: $binlog"
Invoke-Tool "dotnet" @(
    "pack", (Join-Path $root "src/Indra.Ocr.slnx"),
    "-c", $Configuration,
    "-o", $packages,
    "-p:IncludeSymbols=true", "-p:SymbolPackageFormat=snupkg",
    "-v:n", "-bl:$binlog"
)

Step "the packages, entry by entry"
$python = Find-Python
Invoke-Tool $python @(
    (Join-Path $root "tools/scripts/test/check_nupkg.py"), $packages, $packageVersion
)

if ($SkipSmoke) {
    Step "the smoke consumer (skipped: -SkipSmoke)"
} else {
    Step "the smoke consumer (tools/nuget-smoke)"
    if (-not $ModelDir) {
        $local = Join-Path $root "resources/models"
        if (Test-Path $local) { $ModelDir = $local }
    }
    if ($ModelDir) {
        Write-Host "[pack] model folder: $ModelDir"
    } else {
        Write-Host "[pack] no model folder: the smoke resolves one from INDRA_OCR_MODEL_DIR or the Hub"
    }

    $smokeArgs = @(
        "run", "--project", $smoke,
        "-c", $Configuration,
        "-p:SmokePackageVersion=$packageVersion",
        # The feed, in case -Output moved the packages away from tools/nuget-smoke/nuget.config.
        "-p:RestoreAdditionalProjectSources=$packages"
    )
    $passed = @("--", $assets)
    if ($ModelDir) { $passed += $ModelDir }

    Write-Host "[pack] net10.0:"
    Invoke-Tool "dotnet" ($smokeArgs + @("-f", "net10.0") + $passed)

    if ($SkipNet462) {
        Write-Host "[pack] net462: skipped (-SkipNet462)"
    } elseif ($env:OS -ne "Windows_NT") {
        Write-Host "[pack] net462: skipped (only .NET Framework runs it; the Windows job does)"
    } else {
        Write-Host "[pack] net462 (the netstandard2.0 assets):"
        Invoke-Tool "dotnet" ($smokeArgs + @("-f", "net462") + $passed)
    }
}

Step "the artifacts"
if (Test-Path $packages) {
    Get-ChildItem $packages | Sort-Object Name | ForEach-Object {
        Write-Host ("[pack]   {0,10}  {1}" -f $_.Length, $_.Name)
    }
}
Write-Host ""
Write-Host "[pack] OK: $packageVersion packed, checked$(if ($SkipSmoke) { '' } else { ' and smoked' })"
