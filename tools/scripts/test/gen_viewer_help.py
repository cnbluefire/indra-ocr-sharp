"""Capture the reference viewer's ``--help`` and ``--version`` for step 220.

The prog name argparse prints comes from ``sys.argv[0]``, so ``argv[0]`` is set the way
``tools/scripts/run-original-viewer.ps1`` sets it — ``"indra-ocr-viewer"``, which is the name
the console script carries.

Run with the reference environment:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_viewer_help.py
"""

import io
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / "src" / "Indra.Ocr.Tests" / "fixtures"


def capture(argv: list[str]) -> bytes:
    """Run the reference viewer's main() with argv and return what it wrote to stdout."""
    buf = io.BytesIO()
    # newline left alone: a real stdout translates "\n" to os.linesep, and the port reproduces
    # that, so the fixture is captured the same way.
    text = io.TextIOWrapper(buf, encoding="utf-8")
    saved, sys.stdout = sys.stdout, text
    try:
        sys.argv = argv
        from indra_ocr.viewer import main

        try:
            main()
        except SystemExit:
            pass
    finally:
        sys.stdout = saved
        text.flush()
    return buf.getvalue()


def main() -> None:
    for name, argv in [
        ("viewer_help.txt", ["indra-ocr-viewer", "--help"]),
        ("viewer_version.txt", ["indra-ocr-viewer", "--version"]),
    ]:
        data = capture(argv)
        (OUT / name).write_bytes(data)
        print(f"wrote {OUT / name}: {len(data)} bytes")


if __name__ == "__main__":
    main()
