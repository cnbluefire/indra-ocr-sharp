"""CLI parity harness: run the reference ``indra-ocr`` and the port with the same
argv and compare exit code, stdout and stderr byte for byte.

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\cli_parity.py [name ...]

The reference runs as ``python -m indra_ocr`` with ``PYTHONPATH`` pointing at
``resources/indra-src`` and ``PYTHONIOENCODING=utf-8`` (the port always writes
UTF-8; the reference's redirected streams would otherwise take the locale
encoding).  ``COLUMNS=80`` fixes the help width on both sides.

Timings are wall clock, so the harness masks them: the ``timings`` object of a
JSON line and the ``--timings`` stderr lines.  Everything else is compared
exactly, including the CRLF line endings the reference's text-mode streams have.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PY = ROOT / "tools" / "pyenv" / "Scripts" / "python.exe"
EXE = ROOT / "src" / "Indra.Ocr.Cli" / "bin" / "Debug" / "net10.0" / "indra-ocr.exe"
TEMP = ROOT / "temp"
MODELS = ROOT / "resources" / "models"

ENV = dict(os.environ)
ENV["PYTHONPATH"] = str(ROOT / "resources" / "indra-src")
ENV["PYTHONIOENCODING"] = "utf-8"
ENV["COLUMNS"] = "80"

#: The JSON's timings object: keys and values are wall clock.
_TIMINGS = re.compile(rb'"timings": \{[^}]*\}')
#: An ``indra-ocr --timings`` stderr line: "<path> page N: <source>, <ms> ms <note>".
_TIMING_LINE = re.compile(rb"^(.* page \d+: \w+, )[0-9.]+( ms .*)$", re.MULTILINE)


def mask(data: bytes) -> bytes:
    """Hide the wall-clock numbers so the rest can be compared exactly."""
    data = _TIMINGS.sub(b'"timings": {}', data)
    return _TIMING_LINE.sub(rb"\1<TIMING>\2", data)


def run(argv: list[str], out_dir: Path | None = None, reference: bool = False):
    """Run one side and return (exit code, stdout, stderr, files).

    The reference runs the way the installed console script does —
    ``sys.exit(main())`` — not ``python -m indra_ocr``, whose ``__main__.py``
    drops the return value and always exits 0.
    """
    if reference:
        command = [str(PY), "-c",
                   "import sys; from indra_ocr.cli import main; sys.exit(main())", *argv]
    else:
        command = [str(EXE), *argv]
    result = subprocess.run(command, capture_output=True, env=ENV, cwd=ROOT, timeout=1800)
    files = {}
    if out_dir is not None and out_dir.is_dir():
        for path in sorted(out_dir.iterdir()):
            files[path.name] = path.read_bytes()
    return result.returncode, result.stdout, result.stderr, files


def report(name: str, ok: bool, detail: str) -> bool:
    print(f"{'OK  ' if ok else 'FAIL'} {name}{detail}")
    return ok


def prepare_inputs() -> None:
    """The inputs the harness builds itself: fixed bytes, so both sides see the same."""
    out = TEMP / "cli_inputs"
    out.mkdir(parents=True, exist_ok=True)
    (out / "empty.png").write_bytes(b"")
    (out / "truncated.png").write_bytes((TEMP / "inputs" / "latin.png").read_bytes()[:200])
    (out / "notpdf.pdf").write_bytes(b"not a pdf at all\n")
    (out / "dir").mkdir(exist_ok=True)


def compare_bytes(label: str, expected: bytes, actual: bytes) -> bool:
    if expected == actual:
        return True
    print(f"     {label}: expected {len(expected)} bytes, got {len(actual)}")
    limit = max(len(expected), len(actual))
    for i in range(limit):
        e = expected[i] if i < len(expected) else None
        a = actual[i] if i < len(actual) else None
        if e != a:
            lo = max(0, i - 80)
            print(f"     first difference at byte {i}: expected {e!r}, got {a!r}")
            print(f"     expected ...{expected[lo:i + 80]!r}")
            print(f"     actual   ...{actual[lo:i + 80]!r}")
            break
    return False


def case(name: str, argv: list[str], outputs: bool = False) -> bool:
    """Run one argv on both sides and compare everything."""
    ref_out = TEMP / "cli_parity" / name / "ref" if outputs else None
    port_out = TEMP / "cli_parity" / name / "port" if outputs else None
    for directory in (ref_out, port_out):
        if directory is not None and directory.is_dir():
            for path in directory.iterdir():
                path.unlink()
    ref_argv = [a.replace("@OUT@", str(ref_out)) for a in argv]
    port_argv = [a.replace("@OUT@", str(port_out)) for a in argv]
    for directory in (ref_out, port_out):
        if directory is not None:
            directory.mkdir(parents=True, exist_ok=True)

    ref_code, ref_out_data, ref_err, ref_files = run(ref_argv, ref_out, reference=True)
    port_code, port_out_data, port_err, port_files = run(port_argv, port_out)

    ok = True
    detail = ""
    if ref_code != port_code:
        ok = False
        detail += f" (exit {ref_code} vs {port_code})"
    ok &= report(name, ok, detail)
    if ref_code != port_code:
        print(f"     argv: {argv}")
    ok &= compare_bytes(f"{name} stdout", mask(ref_out_data), mask(port_out_data))
    ok &= compare_bytes(f"{name} stderr", mask(ref_err), mask(port_err))
    if outputs:
        if set(ref_files) != set(port_files):
            ok = False
            print(f"     files: reference {sorted(ref_files)}, port {sorted(port_files)}")
        for key in sorted(set(ref_files) & set(port_files)):
            ok &= compare_bytes(f"{name} {key}", mask(ref_files[key]), mask(port_files[key]))
    return ok


#: argv -> cases that need no models (the parser and the errors).
ARGV_CASES = [
    ("help", ["--help"]),
    ("help-short", ["-h"]),
    ("version", ["--version"]),
    ("noargs", []),
    ("badfmt", ["--format", "xml", "a.pdf"]),
    ("badmode", ["--mode", "nope", "a.pdf"]),
    ("badopt", ["--nope", "a.pdf"]),
    ("baddpi", ["--dpi", "x", "a.pdf"]),
    ("nodpi", ["--dpi"]),
    ("badint", ["--max-pages", "x", "a.pdf"]),
    ("ambiguous", ["--m", "a.pdf"]),
    ("ambiguous-model", ["--model", "a.pdf"]),
    ("dashdash", ["--", "--nope"]),
    ("unknown-short", ["-x", "a.pdf"]),
    ("equals", ["--dpi=100", "missing.pdf", "--model-dir", "resources/models"]),
    ("eqfmt", ["--format=json", "missing.pdf", "--model-dir", "resources/models"]),
    ("missing", ["missing.pdf", "--model-dir", "resources/models"]),
    ("nofolder", ["missing.pdf", "--model-dir", "temp"]),
]

#: argv -> real conversions (they load the models from resources/models).
RUN_CASES = [
    ("image-md", ["temp/inputs/latin.png", "--model-dir", "resources/models"]),
    ("image-text", ["temp/inputs/latin.png", "--format", "text", "--model-dir", "resources/models"]),
    ("image-json", ["temp/inputs/latin.png", "--format", "json", "--model-dir", "resources/models"]),
    ("image-rot", ["temp/inputs/latin_rot90.png", "--model-dir", "resources/models"]),
    ("image-tilt", ["temp/inputs/latin_tilt.png", "--model-dir", "resources/models"]),
    ("image-columns", ["temp/inputs/two_columns.png", "--model-dir", "resources/models"]),
    ("image-blank", ["temp/inputs/blank.png", "--model-dir", "resources/models"]),
    ("image-dpi", ["temp/inputs/latin.png", "--dpi", "100", "--model-dir", "resources/models"]),
    ("image-cores", ["temp/inputs/latin.png", "--cores", "2", "--model-dir", "resources/models"]),
    ("tiff", ["temp/inputs/multi.tiff", "--model-dir", "resources/models"]),
    ("tiff-max2", ["temp/inputs/multi.tiff", "--max-pages", "2", "--model-dir", "resources/models"]),
    ("pdf-text", ["temp/pdf_samples/text.pdf", "--model-dir", "resources/models"]),
    ("pdf-text-mode", ["temp/pdf_samples/text.pdf", "--mode", "text", "--model-dir", "resources/models"]),
    ("pdf-text-ocr", ["temp/pdf_samples/text.pdf", "--mode", "ocr", "--model-dir", "resources/models"]),
    ("pdf-image", ["temp/pdf_samples/image.pdf", "--model-dir", "resources/models"]),
    ("pdf-rotated", ["temp/pdf_samples/rotated.pdf", "--model-dir", "resources/models"]),
    ("pdf-cropped", ["temp/pdf_samples/cropped.pdf", "--model-dir", "resources/models"]),
    ("pdf-invisible", ["temp/pdf_samples/invisible.pdf", "--model-dir", "resources/models"]),
    ("pdf-empty", ["temp/pdf_samples/empty.pdf", "--model-dir", "resources/models"]),
    ("pdf-broken", ["temp/pdf_samples/broken.pdf", "--model-dir", "resources/models"]),
    ("pdf-locked", ["temp/pdf_samples/locked.pdf", "--model-dir", "resources/models"]),
    ("pdf-locked-pw", ["temp/pdf_samples/locked.pdf", "--password", "secret", "--model-dir", "resources/models"]),
    ("pdf-json", ["temp/pdf_samples/text.pdf", "--format", "json", "--model-dir", "resources/models"]),
    ("pdf-max1", ["temp/pdf_samples/text.pdf", "--max-pages", "1", "--model-dir", "resources/models"]),
    ("pdf-maxneg", ["temp/pdf_samples/text.pdf", "--max-pages", "-1", "--model-dir", "resources/models"]),
    ("two-inputs", ["temp/inputs/latin.png", "temp/inputs/two_columns.png", "--model-dir", "resources/models"]),
    ("missing-and-good", ["missing.pdf", "temp/inputs/latin.png", "--model-dir", "resources/models"]),
    ("timings", ["temp/inputs/latin.png", "--timings", "--model-dir", "resources/models"]),
    ("furniture", ["temp/pdf_samples/text.pdf", "--keep-furniture", "--model-dir", "resources/models"]),
    ("order", ["temp/pdf_samples/text.pdf", "--model-order", "--model-dir", "resources/models"]),
    ("no-image-ocr", ["temp/pdf_samples/image.pdf", "--no-image-ocr", "--model-dir", "resources/models"]),
    ("math", ["temp/inputs/latin.png", "--math", "--model-dir", "resources/models"]),
    ("tiff-maxneg", ["temp/inputs/multi.tiff", "--max-pages", "-2", "--model-dir", "resources/models"]),
    ("image-maxneg", ["temp/inputs/latin.png", "--max-pages", "-1", "--model-dir", "resources/models"]),
    ("cores-neg", ["temp/inputs/latin.png", "--cores", "-1", "--model-dir", "resources/models"]),
    ("pw-wrong", ["temp/pdf_samples/locked.pdf", "--password", "nope", "--model-dir", "resources/models"]),
    ("pw-plain", ["temp/pdf_samples/text.pdf", "--password", "x", "--model-dir", "resources/models"]),
    ("dir", ["temp/cli_inputs/dir", "--model-dir", "resources/models"]),
    ("empty-file", ["temp/cli_inputs/empty.png", "--model-dir", "resources/models"]),
    ("truncated", ["temp/cli_inputs/truncated.png", "--model-dir", "resources/models"]),
    ("truncated-json", ["temp/cli_inputs/truncated.png", "--format", "json", "--model-dir", "resources/models"]),
    ("notpdf", ["temp/cli_inputs/notpdf.pdf", "--model-dir", "resources/models"]),
    ("cache", ["temp/inputs/latin.png"]),
]

OUT_CASES = [
    ("out-md", ["temp/inputs/latin.png", "--out", "@OUT@", "--model-dir", "resources/models"]),
    ("out-json", ["temp/pdf_samples/text.pdf", "--format", "json", "--out", "@OUT@", "--model-dir", "resources/models"]),
    ("out-dup", ["temp/inputs/latin.png", "temp/inputs/latin.png", "--out", "@OUT@", "--model-dir", "resources/models"]),
    ("out-bad", ["missing.pdf", "temp/inputs/latin.png", "--out", "@OUT@", "--model-dir", "resources/models"]),
]


def main() -> int:
    wanted = sys.argv[1:]
    prepare_inputs()
    ok = True
    for name, argv in ARGV_CASES:
        if wanted and name not in wanted:
            continue
        ok &= case(name, argv)
    for name, argv in RUN_CASES:
        if wanted and name not in wanted:
            continue
        ok &= case(name, argv)
    for name, argv in OUT_CASES:
        if wanted and name not in wanted:
            continue
        ok &= case(name, argv, outputs=True)
    print("all cases match" if ok else "differences found")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
