"""Viewer command-line parity: the reference ``indra-ocr-viewer`` and the port on the same argv.

    tools\\\\pyenv\\\\Scripts\\\\python.exe tools\\\\scripts\\\\test\\\\viewer_cli_parity.py [name ...]

The viewer's *serving* surface is ``viewer_parity.py`` (both servers started once and driven over
HTTP); this is the part that runs and exits — ``--help``, ``--version``, the argparse errors, the
model folder and the refused options.  The machinery is ``parity.py``, the same ``Harness`` the
command line uses, so a case is an argv and what is compared is the exit code plus the two
streams, byte for byte.

Nothing that would serve can be a case: the reference blocks in ``serve_forever()``.  A case may
only fail *before* the server starts, which is why every case here either exits on its own
(``--help``) or fails (a bad option, a bad model folder).

    --list              the cases and their argv
    --jobs N            cases to run at once (default: one per core, capped at 8)
    --serial            one at a time
    NAME ...            case names or globs, default all
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import parity

ROOT = Path(__file__).resolve().parents[3]
PY = ROOT / "tools" / "pyenv" / "Scripts" / "python.exe"
#: The viewer's executable — the Debug build by default.  ``INDRA_OCR_VIEWER_EXE`` points the
#: harness at another one (a Release or NativeAOT publish).
EXE = Path(os.environ.get(
    "INDRA_OCR_VIEWER_EXE",
    ROOT / "src" / "Indra.Ocr.Viewer" / "bin" / "Debug" / "net10.0" / "indra-ocr-viewer.exe"))

ENV = dict(os.environ)
ENV["PYTHONPATH"] = str(ROOT / "resources" / "indra-src")
ENV["PYTHONIOENCODING"] = "utf-8"
ENV["COLUMNS"] = "80"


def mask(data: bytes) -> bytes:
    """Hide the one failure text that cannot match.

    ``--tesseract``: the reference dies importing ``pytesseract`` — a traceback naming a Python
    package that is not installed here — and the port refuses the flag in one line.  Both exit 1
    with an empty stdout, which is what the case checks; the wording is the only thing that
    differs, and it is recorded as a deviation.
    """
    if b"ModuleNotFoundError" in data or b"tesseract is not supported" in data:
        return b"<tesseract is not available here>\n"
    return data


def reference_command(argv: list[str]) -> list[str]:
    """The reference as its console script runs it.

    ``sys.argv[0]`` is set to the script's own name, because argparse prints the program name in
    the usage line and the error prefix (``python -c`` would print ``-c``).
    """
    code = ("import sys\n"
            f"sys.argv = ['indra-ocr-viewer'] + {argv!r}\n"
            "from indra_ocr.viewer import main\n"
            "main()\n")
    return [str(PY), "-c", code]


def port_command(argv: list[str]) -> list[str]:
    return [str(EXE), *argv]


#: A model folder that is missing the files ``model_dir`` insists on.  ``temp`` exists and holds
#: none of them, and order.onnx is never checked (the reference's own quirk, reproduced).
NOFOLDER = ("--model-dir", "temp")

CASES = [
    parity.Case("help", ("--help",)),
    parity.Case("help-short", ("-h",)),
    parity.Case("version", ("--version",)),
    parity.Case("unknown", ("--nope",)),
    parity.Case("unknown-short", ("-x",)),
    parity.Case("ambiguous", ("--t", "x")),
    parity.Case("dashdash", ("--", "--nope")),
    parity.Case("bad-port", ("--port", "x")),
    parity.Case("bad-threads", ("--threads", "x")),
    parity.Case("bad-dpi", ("--dpi", "x")),
    parity.Case("bad-max-pages", ("--max-pages", "x")),
    parity.Case("bad-mode", ("--mode", "nope")),
    parity.Case("missing-value", ("--threads",)),
    parity.Case("abbrev", ("--por", "9000", *NOFOLDER)),
    parity.Case("equals", ("--port=9000", *NOFOLDER)),
    parity.Case("nofolder", NOFOLDER),
    # --math asks model_dir for the math models too, so the missing list grows by five.
    parity.Case("nofolder-math", ("--math", *NOFOLDER)),
    parity.Case("source-args", ("--mode", "text", "--model-order", "--no-image-ocr",
                                "--keep-furniture", *NOFOLDER)),
    parity.Case("tesseract-lang", ("--tesseract-lang", "deu", *NOFOLDER)),
    # The reference cannot start with --tesseract at all (it imports pytesseract), so the case
    # only checks that the port refuses it the same way: exit 1, nothing on stdout.
    parity.Case("tesseract", ("--tesseract",)),
]


def main() -> int:
    options = parity.parse(sys.argv[1:])
    cases = parity.select(CASES, options.names)
    if options.listing:
        for case in cases:
            print(f"{case.name}\t{' '.join(case.argv)}")
        return 0

    harness = parity.Harness(
        reference=reference_command,
        port=port_command,
        env=ENV,
        cwd=ROOT,
        work=ROOT / "temp" / "viewer_cli_parity",
        mask=mask,
        jobs=options.jobs,
    )
    return harness.run(cases)


if __name__ == "__main__":
    sys.exit(main())
