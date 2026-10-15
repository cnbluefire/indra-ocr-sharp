# Build the local Python reference environment (gitignored, under tools/pyenv) so the
# original indra-ocr / indra-ocr-viewer can be run for alignment.
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/../.."
$pyenv = Join-Path $root "tools/pyenv"

if (-not (Test-Path $pyenv)) {
    uv venv $pyenv --python 3.14
}
uv pip install --python (Join-Path $pyenv "Scripts/python.exe") `
    numpy opencv-python-headless onnxruntime pypdfium2 huggingface_hub pillow pikepdf
Write-Host "Python reference env ready at $pyenv"
