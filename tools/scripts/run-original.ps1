# Run the original Python `indra-ocr` CLI against the local source tree (read-only).
# Usage: tools/scripts/run-original.ps1 <indra-ocr args...>
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/../.."
$env:PYTHONPATH = Join-Path $root "resources/indra-src"
& (Join-Path $root "tools/pyenv/Scripts/python.exe") -m indra_ocr @args
exit $LASTEXITCODE
