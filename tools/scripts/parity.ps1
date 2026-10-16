# Run the CLI parity harness: the reference `indra-ocr` and the port on the same argv,
# compared byte for byte.  See docs/195-harness.md.
# Usage: tools/scripts/parity.ps1 [--jobs N] [--serial] [--list] [NAME ...]
param(
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$HarnessArgs
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/../.."
& (Join-Path $root "tools/pyenv/Scripts/python.exe") `
    (Join-Path $root "tools/scripts/test/cli_parity.py") @HarnessArgs
exit $LASTEXITCODE
