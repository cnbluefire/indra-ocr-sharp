# Smoke the published hosts of one RID on Windows, with nothing but PowerShell: the CLI on the
# committed samples, then the viewer over plain HTTP requests.
# See docs/340-nuget-packages.md.
#
# Usage: build/smoke-win.ps1 [-Dir temp/publish/win-x64] [-ModelDir <dir>] [-Port 8799]
#
# The checks are deliberately coarse.  Both hosts are the port end to end (native AOT in a release,
# the trimmed stand-in with INDRA_OCR_FAKE_AOT), so this answers "does the published binary start,
# read a PDF and an image, write its output, and serve the viewer's API" -- the byte-for-byte
# comparison against the reference lives in tools/scripts/test/cli_parity.py and the test suite, and
# needs the Python reference environment, which a release machine does not have.
#
# The model folder is resources/models when it is there (a developer's clone); otherwise the folder
# argument is left out and both hosts resolve the models themselves -- they download them from the
# Hub, which is what a release user does and what the CI runners exercise (TLS, JSON and the
# download path included).
#
# Versions are compared by prefix only: the CLI prints `indra-ocr <version>` and the viewer reports
# the assembly's version, and the exact value belongs to src/Directory.Build.props, which is read
# here rather than repeated.
#
# Calls are written as `Check "what" (<condition>) "<detail>"`: PowerShell builds an array out of a
# comma in argument position, so `Check "what", (<condition>)` would hand the whole thing to the
# first parameter and every check would read as a failure.
param(
    [string]$Dir = "",
    [string]$ModelDir = "",
    [int]$Port = 8799
)
$ErrorActionPreference = "Stop"

$root = (Resolve-Path "$PSScriptRoot/..").Path
if (-not $Dir) {
    $arm = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq `
           [System.Runtime.InteropServices.Architecture]::Arm64
    $Dir = Join-Path $root "temp/publish/$(if ($arm) { 'win-arm64' } else { 'win-x64' })"
}
$dir = if ([System.IO.Path]::IsPathRooted($Dir)) { $Dir } else { Join-Path $root $Dir }
if (-not $ModelDir) {
    $local = Join-Path $root "resources/models"
    if (Test-Path $local) { $ModelDir = $local }
}
$logs = Join-Path $root "temp/logs"
$work = Join-Path $root "temp/smoke"
New-Item -ItemType Directory -Force -Path $logs, $work | Out-Null

$png = Join-Path $root "build/smoke/latin.png"
$pdf = Join-Path $root "build/smoke/latin.pdf"
$props = Get-Content -Raw (Join-Path $root "src/Directory.Build.props")
$version = [regex]::Match($props, "(?m)^\s*<Version>([^<]+)</Version>").Groups[1].Value

$checks = 0
$failures = @()

# build/publish.* publishes one folder per host (cli/, viewer/); a RID folder that holds both hosts
# directly is accepted too, which is what a hand-run `dotnet publish ... -o <rid>` of the two
# projects produces.
function Find-Host([string]$Name) {
    $candidates = @(
        (Join-Path $dir "$Name.exe"),
        (Join-Path $dir "cli/$Name.exe"),
        (Join-Path $dir "viewer/$Name.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $candidates[0]
}

function Section([string]$Text) {
    Write-Host ""
    Write-Host "== $Text"
}

function Check([string]$What, [bool]$Ok, [string]$Detail = "") {
    $script:checks++
    if ($Ok) {
        Write-Host "  ok   $What"
        return
    }

    Write-Host "  FAIL $What$(if ($Detail) { ": $Detail" })"
    $script:failures += $What
}

function Show-Tail([string]$Path, [int]$Lines = 12) {
    if (-not (Test-Path $Path)) { return }
    Write-Host "  --- $Path (last $Lines lines)"
    Get-Content $Path -Tail $Lines | ForEach-Object { Write-Host "  | $_" }
}

# The CLI with its output merged (stderr carries --timings, so it has to be shown on failure), and
# with ErrorActionPreference relaxed: Windows PowerShell turns a native stderr line into a
# terminating error while it is Stop, and the CLI's exit code is what the checks look at.
function Run-Cli([string[]]$Arguments) {
    $ErrorActionPreference = "Continue"
    $text = & $script:cli @Arguments 2>&1 | Out-String
    return @{ Exit = $LASTEXITCODE; Text = $text }
}

$cli = Find-Host "indra-ocr"
$viewer = Find-Host "indra-ocr-viewer"

Write-Host "smoke-win: the published hosts in $dir"
Write-Host "  version:  $version (from src/Directory.Build.props)"
Write-Host "  models:   $(if ($ModelDir) { $ModelDir } else { 'none: the hosts resolve them themselves' })"
Write-Host "  viewer:   http://127.0.0.1:$Port"

$modelArgs = if ($ModelDir) { @("--model-dir", $ModelDir) } else { @() }

Section "the published hosts"
Check "indra-ocr.exe is there" (Test-Path $cli) $cli
Check "indra-ocr-viewer.exe is there" (Test-Path $viewer) $viewer
if (-not (Test-Path $cli) -or -not (Test-Path $viewer)) {
    Write-Host "smoke-win: FAILED (the hosts are not published)"
    exit 1
}

# The zip is what a release carries, so its contents are checked rather than the publish folder's:
# the folder keeps its symbol files on disk, the zip is where they are dropped.
$zips = @(Get-ChildItem (Split-Path -Parent $dir) -Filter "indra-ocr-sharp_*.zip" -ErrorAction SilentlyContinue)
if ($zips.Count -eq 0) {
    Write-Host "  (no zip beside the publish folder: the zip checks are skipped)"
} else {
    $zip = $zips | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    # Windows PowerShell 5.1 has to be told to load the assembly; PowerShell 7 already has it.
    if (-not ("System.IO.Compression.ZipFile" -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try { $entries = @($archive.Entries | ForEach-Object { $_.FullName }) } finally { $archive.Dispose() }
    $symbols = @($entries | Where-Object { $_ -like "*.pdb" -or $_ -like "*.dbg" })
    Check "the zip ($($zip.Name)) has no symbol files" ($symbols.Count -eq 0) ($symbols -join ", ")
    Check "the zip holds both hosts at its root" `
          (($entries -contains "indra-ocr.exe") -and ($entries -contains "indra-ocr-viewer.exe")) `
          "$($entries.Count) entries"
    Check "the zip carries the licence files" `
          (($entries -contains "LICENSE") -and ($entries -contains "NOTICE"))
}

Section "the command line"
$r = Run-Cli @("--version")
Check "indra-ocr --version is the port" `
      ($r.Text -match "(?m)^\s*indra-ocr " -and $r.Text.Contains($version)) $r.Text.Trim()

$r = Run-Cli @("--help")
Check "indra-ocr --help prints its usage" `
      ($r.Exit -eq 0 -and $r.Text.Contains("usage: indra-ocr")) "exit $($r.Exit)"

$r = Run-Cli (@($png, "--format", "text") + $modelArgs)
Check "the PNG reads back its invoice number" `
      ($r.Exit -eq 0 -and $r.Text.Contains("12345")) "exit $($r.Exit): $($r.Text.Trim())"

$r = Run-Cli (@($pdf, "--format", "text") + $modelArgs)
Check "the PDF's text layer is read" `
      ($r.Exit -eq 0 -and $r.Text.Contains("Invoice number: 12345")) "exit $($r.Exit): $($r.Text.Trim())"

$r = Run-Cli (@($pdf, "--mode", "ocr", "--format", "json") + $modelArgs)
Check "the PDF OCRs when asked (json lines)" `
      ($r.Exit -eq 0 -and $r.Text.Contains("12345") -and $r.Text.Contains('"page"')) "exit $($r.Exit)"

Section "the written output"
$out = Join-Path $work "out"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$r = Run-Cli (@($png, "--out", $out) + $modelArgs)
$written = Join-Path $out "latin.md"
Check "indra-ocr --out writes one markdown file per input" `
      ($r.Exit -eq 0 -and (Test-Path $written) -and (Get-Content -Raw $written).Contains("12345")) "exit $($r.Exit)"

$r = Run-Cli @(Join-Path $work "there-is-no-such-file.png")
Check "a missing input is an error" ($r.Exit -ne 0) "exit $($r.Exit)"

# The viewer: a real server on a real port, asked over HTTP.  HttpWebRequest is the one HTTP client
# that is present in both Windows PowerShell 5.1 and PowerShell 7, and it reports 4xx answers
# instead of throwing.  "n" is the page index the browser's page list sends, and it is 0-based:
# the reference reads the page with doc[n], pypdfium2's __getitem__.
function Http([string]$Method, [string]$Path, [byte[]]$Body, [string]$ContentType) {
    $request = [System.Net.HttpWebRequest]::Create("http://127.0.0.1:$Port$Path")
    $request.Method = $Method
    $request.Timeout = 900000
    $request.ReadWriteTimeout = 900000
    if ($ContentType) { $request.ContentType = $ContentType }
    if ($Body) {
        $request.ContentLength = $Body.Length
        $stream = $request.GetRequestStream()
        $stream.Write($Body, 0, $Body.Length)
        $stream.Dispose()
    }

    try {
        $response = $request.GetResponse()
    } catch [System.Net.WebException] {
        $response = $_.Exception.Response
    }
    if (-not $response) { throw "no answer from http://127.0.0.1:$Port$Path" }

    $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
    $text = $reader.ReadToEnd()
    $reader.Dispose()
    $status = [int]$response.StatusCode
    $response.Dispose()
    return @{ Status = $status; Text = $text }
}

function Json-Http([string]$Method, [string]$Path, [byte[]]$Body, [string]$ContentType) {
    $answer = Http $Method $Path $Body $ContentType
    $parsed = $null
    try { $parsed = $answer.Text | ConvertFrom-Json } catch { }
    return @{ Status = $answer.Status; Text = $answer.Text; Json = $parsed }
}

function Start-Viewer {
    $out = Join-Path $logs "smoke-viewer.out"
    $err = Join-Path $logs "smoke-viewer.err"
    Remove-Item $out, $err -ErrorAction SilentlyContinue
    # Start-Process joins the arguments with spaces and quotes nothing, so anything holding a space
    # (a --model-dir under a path like C:\Program Files, say) is quoted here.
    $arguments = (@("--port", "$Port", "--threads", "2") + $modelArgs) |
        ForEach-Object { if ($_ -match "\s") { '"' + $_ + '"' } else { $_ } }
    $arguments = $arguments -join " "
    Write-Host "  starting: $viewer $arguments"
    return Start-Process -FilePath $viewer -ArgumentList $arguments -PassThru -NoNewWindow `
                         -RedirectStandardOutput $out -RedirectStandardError $err
}

Section "the viewer"
$process = Start-Viewer
try {
    # The models are resolved (and downloaded) before the server listens, so the wait is generous:
    # a cold CI runner downloads them here.
    $info = $null
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) { break }
        try {
            $answer = Json-Http "GET" "/info"
            if ($answer.Status -eq 200) { $info = $answer.Json; break }
        } catch {
            # not listening yet
        }
        Start-Sleep -Milliseconds 500
    }

    Check "the viewer answers GET /info" ($null -ne $info) `
          "exit code $($process.ExitCode); log: $logs/smoke-viewer.err"
    if ($null -eq $info) {
        Show-Tail (Join-Path $logs "smoke-viewer.out")
        Show-Tail (Join-Path $logs "smoke-viewer.err")
    } else {
        Check "tesseract is off" ($info.tesseract -eq $false)
        Check "the version is the assembly's" ($info.version -eq $version) "$($info.version)"
        Check "the defaults come back" `
              (($info.defaults.mode -in "auto", "ocr", "text") -and ($info.defaults.max_pages -eq 20)) `
              "$($info.defaults | ConvertTo-Json -Compress)"

        $page = Http "GET" "/" $null $null
        Check "GET / serves the page" ($page.Status -eq 200 -and $page.Text.Contains("indra-ocr")) `
              "status $($page.Status)"

        $upload = Json-Http "POST" "/upload" ([System.IO.File]::ReadAllBytes($png)) "application/octet-stream"
        Check "POST /upload takes the PNG" ($upload.Status -eq 200 -and $upload.Json.pages -eq 1) `
              "status $($upload.Status): $($upload.Text)"

        if ($upload.Json.id) {
            $body = [System.Text.Encoding]::UTF8.GetBytes(
                (@{ id = $upload.Json.id; n = 0 } | ConvertTo-Json -Compress))
            $read = Json-Http "POST" "/page" $body "application/json"
            Check "POST /page reads the image" `
                  ($read.Status -eq 200 -and "$($read.Json.markdown)".Contains("12345")) `
                  "status $($read.Status): $($read.Text)"
            Check "the answer carries the preview, the boxes and the timings" `
                  ($read.Json.img.StartsWith("data:image/jpeg;base64,") -and
                   $read.Json.lines.Count -gt 0 -and $read.Json.page -eq 1 -and
                   $read.Json.timings.total_ms -gt 0) `
                  "status $($read.Status)"
        } else {
            Check "POST /upload answers with an id" $false $upload.Text
        }

        $upload = Json-Http "POST" "/upload" ([System.IO.File]::ReadAllBytes($pdf)) "application/octet-stream"
        Check "POST /upload takes the PDF" ($upload.Status -eq 200 -and $upload.Json.pages -eq 1) `
              "status $($upload.Status): $($upload.Text)"
        if ($upload.Json.id) {
            $body = [System.Text.Encoding]::UTF8.GetBytes(
                (@{ id = $upload.Json.id; n = 0 } | ConvertTo-Json -Compress))
            $read = Json-Http "POST" "/page" $body "application/json"
            Check "POST /page reads the PDF's text layer" `
                  ($read.Status -eq 200 -and "$($read.Json.markdown)".Contains("Invoice number: 12345")) `
                  "status $($read.Status): $($read.Text)"
        } else {
            Check "POST /upload answers with an id" $false $upload.Text
        }

        $bad = Json-Http "POST" "/page" ([System.Text.Encoding]::UTF8.GetBytes('{"n": 1}')) "application/json"
        Check "a page request without an id is a 400" ($bad.Status -eq 400 -and $bad.Json.error) `
              "status $($bad.Status): $($bad.Text)"
    }
} finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(10000) | Out-Null
        Write-Host "  stopped the viewer (pid $($process.Id))"
    }
}

Write-Host ""
if ($failures.Count) {
    Write-Host "smoke-win: FAILED ($($failures.Count) of $checks checks)"
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 1
}

Write-Host "smoke-win: OK ($checks checks)"
# An explicit exit: PowerShell leaves $LASTEXITCODE at the last native command's code otherwise (a
# check that deliberately runs the CLI on a missing file leaves it at 1), and the caller -- and the
# workflow step -- read the script's exit code.
exit 0
