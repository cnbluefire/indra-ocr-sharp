"""CLI parity harness: the reference ``indra-ocr`` and the port on the same argv.

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\cli_parity.py [name ...]

Every case runs both commands and compares the exit code, stdout, stderr and — with
``--out`` — the files written, byte for byte.  Wall-clock timings are masked; everything
else matches exactly, including the CRLF line endings the reference's text-mode streams
carry.  The machinery lives in ``parity.py``; ``docs/195-harness.md`` has the details.

    --list              the cases and their argv
    --jobs N            cases to run at once (default: one per core, capped at 8)
    --serial            one at a time
    NAME ...            case names or globs (``pool-*``), default all

The reference runs the way the installed console script does — ``sys.exit(main())`` — and
not ``python -m indra_ocr``, whose ``__main__.py`` drops the return value and always exits
0.  ``PYTHONIOENCODING=utf-8`` because the port always writes UTF-8 (the reference's
redirected streams would otherwise take the locale encoding), and ``COLUMNS=80`` to fix
the help width on both sides.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

import parity

ROOT = Path(__file__).resolve().parents[3]
PY = ROOT / "tools" / "pyenv" / "Scripts" / "python.exe"
#: The port's executable — the Debug build by default.  ``INDRA_OCR_EXE`` points the harness at
#: another one (a Release or NativeAOT publish), which is how a published binary is compared
#: against the reference.
EXE = Path(os.environ.get(
    "INDRA_OCR_EXE",
    ROOT / "src" / "Indra.Ocr.Cli" / "bin" / "Debug" / "net10.0" / "indra-ocr.exe"))
TEMP = ROOT / "temp"
MODELS = ROOT / "resources" / "models"
SCRIPTS = ROOT / "tools" / "scripts"
TEST = ROOT / "tools" / "scripts" / "test"

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


def reference_command(argv: list[str]) -> list[str]:
    """The reference as its console script runs it (its ``main()`` return value is the
    exit code; ``python -m indra_ocr`` would swallow it)."""
    return [str(PY), "-c",
            "import sys; from indra_ocr.cli import main; sys.exit(main())", *argv]


def port_command(argv: list[str]) -> list[str]:
    return [str(EXE), *argv]


def ensure_inputs() -> None:
    """Build the generated inputs once when they are missing (``temp/`` is not in git)."""
    generated = [
        (SCRIPTS / "gen_inputs.py", TEMP / "inputs" / "latin.png"),
        (TEST / "pdf_samples.py", TEMP / "pdf_samples" / "text.pdf"),
    ]
    for script, marker in generated:
        if not marker.exists():
            subprocess.run([str(PY), str(script)], check=True, cwd=ROOT, env=ENV)


#: ``temp/math-models`` is not in git either: it is downloaded on demand (``models.model_dir``
#: fetches the Hugging Face snapshot), so the math cases are skipped when it is absent.
MATH_MODELS = TEMP / "math-models"

#: The reference's math loader reads ``math_meta.json`` with ``read_text()``, i.e. in the locale
#: encoding, which on a Chinese Windows cannot decode the file's UTF-8 and makes ``--math``
#: raise.  ``PYTHONUTF8`` has the reference read it the way the port always does; the port
#: ignores the variable.
MATH_ENV = {"PYTHONUTF8": "1"}


def no_math_models() -> str | None:
    """Why the math cases cannot run here, or ``None`` when they can."""
    if not (MATH_MODELS / "math_meta.json").exists():
        return "temp/math-models is not downloaded"
    if not (TEMP / "inputs" / "math.png").exists():
        return "temp/inputs/math.png is missing (delete temp/inputs and rerun)"
    return None


def prepare_inputs() -> None:
    """The inputs the harness builds itself: fixed bytes, so both sides see the same."""
    out = TEMP / "cli_inputs"
    out.mkdir(parents=True, exist_ok=True)
    (out / "empty.png").write_bytes(b"")
    (out / "truncated.png").write_bytes((TEMP / "inputs" / "latin.png").read_bytes()[:200])
    (out / "notpdf.pdf").write_bytes(b"not a pdf at all\n")
    (out / "dir").mkdir(exist_ok=True)
    if not (out / "many.pdf").exists():
        # Six pages of text, so convert_many's worker pool is the path taken (MIN_POOL_PAGES).
        from fpdf import FPDF

        pdf = FPDF(unit="pt", format="letter")
        pdf.set_creation_date(datetime(2026, 1, 1, tzinfo=timezone.utc))
        for page in range(6):
            pdf.add_page()
            pdf.set_font("Helvetica", size=14)
            pdf.cell(0, 22, f"Pooled page {page + 1}", new_x="LMARGIN", new_y="NEXT")
            pdf.set_font("Helvetica", size=12)
            pdf.cell(0, 18, "A line of text for the detector.", new_x="LMARGIN", new_y="NEXT")
        pdf.output(str(out / "many.pdf"))


#: argv -> cases that need no models (the parser and the errors).
ARGV_CASES = [
    parity.Case("help", ("--help",)),
    parity.Case("help-short", ("-h",)),
    parity.Case("version", ("--version",)),
    parity.Case("noargs", ()),
    parity.Case("badfmt", ("--format", "xml", "a.pdf")),
    parity.Case("badmode", ("--mode", "nope", "a.pdf")),
    parity.Case("badopt", ("--nope", "a.pdf")),
    parity.Case("baddpi", ("--dpi", "x", "a.pdf")),
    parity.Case("nodpi", ("--dpi",)),
    parity.Case("badint", ("--max-pages", "x", "a.pdf")),
    parity.Case("ambiguous", ("--m", "a.pdf")),
    parity.Case("ambiguous-model", ("--model", "a.pdf")),
    parity.Case("dashdash", ("--", "--nope")),
    parity.Case("unknown-short", ("-x", "a.pdf")),
    parity.Case("equals", ("--dpi=100", "missing.pdf", "--model-dir", "resources/models")),
    parity.Case("eqfmt", ("--format=json", "missing.pdf", "--model-dir", "resources/models")),
    parity.Case("missing", ("missing.pdf", "--model-dir", "resources/models")),
    parity.Case("nofolder", ("missing.pdf", "--model-dir", "temp")),
]

#: real conversions (they load the models from resources/models).
RUN_CASES = [
    parity.Case("image-md", ("temp/inputs/latin.png", "--model-dir", "resources/models")),
    parity.Case("image-text", ("temp/inputs/latin.png", "--format", "text",
                               "--model-dir", "resources/models")),
    parity.Case("image-json", ("temp/inputs/latin.png", "--format", "json",
                               "--model-dir", "resources/models")),
    parity.Case("image-rot", ("temp/inputs/latin_rot90.png", "--model-dir", "resources/models")),
    parity.Case("image-tilt", ("temp/inputs/latin_tilt.png", "--model-dir", "resources/models")),
    parity.Case("image-columns", ("temp/inputs/two_columns.png", "--model-dir", "resources/models")),
    parity.Case("image-blank", ("temp/inputs/blank.png", "--model-dir", "resources/models")),
    parity.Case("image-dpi", ("temp/inputs/latin.png", "--dpi", "100",
                              "--model-dir", "resources/models")),
    parity.Case("image-cores", ("temp/inputs/latin.png", "--cores", "2",
                                "--model-dir", "resources/models")),
    parity.Case("tiff", ("temp/inputs/multi.tiff", "--model-dir", "resources/models")),
    parity.Case("tiff-max2", ("temp/inputs/multi.tiff", "--max-pages", "2",
                              "--model-dir", "resources/models")),
    parity.Case("pdf-text", ("temp/pdf_samples/text.pdf", "--model-dir", "resources/models")),
    parity.Case("pdf-text-mode", ("temp/pdf_samples/text.pdf", "--mode", "text",
                                  "--model-dir", "resources/models")),
    parity.Case("pdf-text-ocr", ("temp/pdf_samples/text.pdf", "--mode", "ocr",
                                 "--model-dir", "resources/models")),
    parity.Case("pdf-image", ("temp/pdf_samples/image.pdf", "--model-dir", "resources/models")),
    parity.Case("pdf-rotated", ("temp/pdf_samples/rotated.pdf", "--model-dir", "resources/models")),
    parity.Case("pdf-cropped", ("temp/pdf_samples/cropped.pdf", "--model-dir", "resources/models")),
    parity.Case("pdf-invisible", ("temp/pdf_samples/invisible.pdf",
                                  "--model-dir", "resources/models")),
    parity.Case("pdf-empty", ("temp/pdf_samples/empty.pdf", "--model-dir", "resources/models")),
    parity.Case("pdf-broken", ("temp/pdf_samples/broken.pdf", "--model-dir", "resources/models")),
    parity.Case("pdf-locked", ("temp/pdf_samples/locked.pdf", "--model-dir", "resources/models")),
    parity.Case("pdf-locked-pw", ("temp/pdf_samples/locked.pdf", "--password", "secret",
                                  "--model-dir", "resources/models")),
    parity.Case("pdf-json", ("temp/pdf_samples/text.pdf", "--format", "json",
                             "--model-dir", "resources/models")),
    parity.Case("pdf-max1", ("temp/pdf_samples/text.pdf", "--max-pages", "1",
                             "--model-dir", "resources/models")),
    parity.Case("pdf-maxneg", ("temp/pdf_samples/text.pdf", "--max-pages", "-1",
                               "--model-dir", "resources/models")),
    parity.Case("two-inputs", ("temp/inputs/latin.png", "temp/inputs/two_columns.png",
                               "--model-dir", "resources/models")),
    parity.Case("missing-and-good", ("missing.pdf", "temp/inputs/latin.png",
                                     "--model-dir", "resources/models")),
    parity.Case("timings", ("temp/inputs/latin.png", "--timings",
                            "--model-dir", "resources/models")),
    parity.Case("furniture", ("temp/pdf_samples/text.pdf", "--keep-furniture",
                              "--model-dir", "resources/models")),
    parity.Case("order", ("temp/pdf_samples/text.pdf", "--model-order",
                          "--model-dir", "resources/models")),
    parity.Case("no-image-ocr", ("temp/pdf_samples/image.pdf", "--no-image-ocr",
                                 "--model-dir", "resources/models")),
    parity.Case("math", ("temp/inputs/latin.png", "--math", "--model-dir", "resources/models")),
    # The math models themselves: the classifier fires on the formula lines of math.png and on
    # the text layer of math.pdf, and MathRec reads them as LaTeX.  latin.png with the models
    # present is the classifier's all-negative path.
    parity.Case("math-page", ("temp/inputs/math.png", "--math",
                              "--model-dir", "temp/math-models"),
                env=MATH_ENV, skip_if=no_math_models),
    parity.Case("math-page-json", ("temp/inputs/math.png", "--math", "--format", "json",
                                   "--model-dir", "temp/math-models"),
                env=MATH_ENV, skip_if=no_math_models),
    parity.Case("math-text", ("temp/inputs/math.pdf", "--math",
                              "--model-dir", "temp/math-models"),
                env=MATH_ENV, skip_if=no_math_models),
    parity.Case("math-plain", ("temp/inputs/latin.png", "--math",
                               "--model-dir", "temp/math-models"),
                env=MATH_ENV, skip_if=no_math_models),
    parity.Case("math-pool", ("temp/inputs/math.png", "temp/inputs/math.pdf",
                              "temp/inputs/latin.png", "temp/inputs/two_columns.png",
                              "--math", "--model-dir", "temp/math-models"),
                env=MATH_ENV, skip_if=no_math_models),
    parity.Case("tiff-maxneg", ("temp/inputs/multi.tiff", "--max-pages", "-2",
                                "--model-dir", "resources/models")),
    parity.Case("image-maxneg", ("temp/inputs/latin.png", "--max-pages", "-1",
                                 "--model-dir", "resources/models")),
    parity.Case("cores-neg", ("temp/inputs/latin.png", "--cores", "-1",
                              "--model-dir", "resources/models")),
    parity.Case("pw-wrong", ("temp/pdf_samples/locked.pdf", "--password", "nope",
                             "--model-dir", "resources/models")),
    parity.Case("pw-plain", ("temp/pdf_samples/text.pdf", "--password", "x",
                             "--model-dir", "resources/models")),
    parity.Case("dir", ("temp/cli_inputs/dir", "--model-dir", "resources/models")),
    parity.Case("empty-file", ("temp/cli_inputs/empty.png", "--model-dir", "resources/models")),
    parity.Case("truncated", ("temp/cli_inputs/truncated.png", "--model-dir", "resources/models")),
    parity.Case("truncated-json", ("temp/cli_inputs/truncated.png", "--format", "json",
                                   "--model-dir", "resources/models")),
    parity.Case("notpdf", ("temp/cli_inputs/notpdf.pdf", "--model-dir", "resources/models")),
    parity.Case("cache", ("temp/inputs/latin.png",)),
    # The worker pool (MIN_POOL_PAGES = 4 pages): several inputs, a six-page PDF, and the
    # serial fallbacks (few pages, one worker).
    parity.Case("pool-images", ("temp/inputs/latin.png", "temp/inputs/latin_rot90.png",
                                "temp/inputs/latin_tilt.png", "temp/inputs/two_columns.png",
                                "temp/inputs/blank.png", "temp/inputs/cjk.png",
                                "--model-dir", "resources/models")),
    parity.Case("pool-pdf", ("temp/cli_inputs/many.pdf", "--model-dir", "resources/models")),
    parity.Case("pool-pdf-json", ("temp/cli_inputs/many.pdf", "--format", "json",
                                  "--model-dir", "resources/models")),
    parity.Case("pool-pdf-text", ("temp/cli_inputs/many.pdf", "--format", "text",
                                  "--model-dir", "resources/models")),
    parity.Case("pool-pdf-mode", ("temp/cli_inputs/many.pdf", "--mode", "text",
                                  "--model-dir", "resources/models")),
    parity.Case("pool-pdf-max", ("temp/cli_inputs/many.pdf", "--max-pages", "5",
                                 "--model-dir", "resources/models")),
    parity.Case("pool-mixed", ("temp/cli_inputs/many.pdf", "temp/inputs/latin.png",
                               "missing.pdf", "temp/pdf_samples/locked.pdf",
                               "temp/pdf_samples/text.pdf", "temp/cli_inputs/truncated.png",
                               "--model-dir", "resources/models")),
    parity.Case("pool-cores2", ("temp/cli_inputs/many.pdf", "--cores", "2",
                                "--model-dir", "resources/models")),
    parity.Case("pool-cores1", ("temp/cli_inputs/many.pdf", "--cores", "1",
                                "--model-dir", "resources/models")),
    parity.Case("pool-small", ("temp/inputs/latin.png", "temp/inputs/two_columns.png",
                               "temp/inputs/blank.png", "--model-dir", "resources/models")),
    parity.Case("pool-timings", ("temp/cli_inputs/many.pdf", "--timings", "--cores", "3",
                                 "--model-dir", "resources/models")),
]

#: the cases that write files: ``@OUT@`` is each side's own directory.
OUT_CASES = [
    parity.Case("out-md", ("temp/inputs/latin.png", "--out", "@OUT@",
                           "--model-dir", "resources/models"), outputs=True),
    parity.Case("out-json", ("temp/pdf_samples/text.pdf", "--format", "json", "--out", "@OUT@",
                             "--model-dir", "resources/models"), outputs=True),
    parity.Case("out-dup", ("temp/inputs/latin.png", "temp/inputs/latin.png",
                            "--out", "@OUT@", "--model-dir", "resources/models"), outputs=True),
    parity.Case("out-bad", ("missing.pdf", "temp/inputs/latin.png", "--out", "@OUT@",
                            "--model-dir", "resources/models"), outputs=True),
    parity.Case("out-pool", ("temp/cli_inputs/many.pdf", "temp/inputs/latin.png",
                             "--out", "@OUT@", "--model-dir", "resources/models"), outputs=True),
    parity.Case("out-math", ("temp/inputs/math.pdf", "--math", "--out", "@OUT@",
                             "--model-dir", "temp/math-models"),
                outputs=True, env=MATH_ENV, skip_if=no_math_models),
]

CASES = [*ARGV_CASES, *RUN_CASES, *OUT_CASES]


def main() -> int:
    options = parity.parse(sys.argv[1:])
    cases = parity.select(CASES, options.names)
    if options.listing:
        for case in cases:
            print(f"{case.name}\t{' '.join(case.argv)}")
        return 0

    ensure_inputs()
    prepare_inputs()
    harness = parity.Harness(
        reference=reference_command,
        port=port_command,
        env=ENV,
        cwd=ROOT,
        work=TEMP / "cli_parity",
        mask=mask,
        jobs=options.jobs,
    )
    return harness.run(cases)


if __name__ == "__main__":
    sys.exit(main())
