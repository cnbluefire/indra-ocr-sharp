# Run a Python script with the local reference interpreter, with PYTHONPATH pointing at
# the (read-only) indra source tree so `import indra_ocr` works.
# Usage: tools/scripts/py.ps1 <script.py> [args...]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Script,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ScriptArgs
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/../.."
$env:PYTHONPATH = Join-Path $root "resources/indra-src"
& (Join-Path $root "tools/pyenv/Scripts/python.exe") $Script @ScriptArgs
exit $LASTEXITCODE
