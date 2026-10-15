# Run the original Python `indra-ocr-viewer`. NOTE: this blocks (HTTP server).
# Run it in the background; do not wait for it in automation.
# Usage: tools/scripts/run-original-viewer.ps1 <indra-ocr-viewer args...>
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/../.."
$env:PYTHONPATH = Join-Path $root "resources/indra-src"
$argv = @("indra-ocr-viewer") + $args
$code = @"
import sys
sys.argv = $($argv | ConvertTo-Json -Compress)
from indra_ocr.viewer import main
main()
"@
& (Join-Path $root "tools/pyenv/Scripts/python.exe") -c $code
exit $LASTEXITCODE
