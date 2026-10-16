"""Self-check for the parity machinery: does it actually catch a mismatch?

Every case here is synthetic — both sides are the local interpreter, told to emit chosen
bytes and exit codes — so a bug in the harness cannot quietly turn the real cases green.
The check covers what the harness compares (exit code, stdout, stderr, `--out` files) and
the two paths that keep a broken run from hanging: a hung side is killed, and the summary
is the exit code.

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\parity_selftest.py
"""

from __future__ import annotations

import io
import os
import sys
from pathlib import Path
from typing import Callable

sys.path.insert(0, str(Path(__file__).resolve().parent))

import parity

WORK = Path(__file__).resolve().parents[3] / "temp" / "parity_selftest"


def emits(code: int = 0, out: str = "", err: str = "") -> Callable:
    """A command line for a side that writes ``out``/``err`` and exits ``code``."""
    body = (f"import sys; sys.stdout.write({out!r}); "
            f"sys.stderr.write({err!r}); sys.exit({code})")
    return lambda argv: [sys.executable, "-c", body]


def writes(content: bytes, name: str = "a.txt") -> Callable:
    """A command line for a side that writes one file into its ``--out`` directory."""
    body = (f"import pathlib, sys; "
            f"pathlib.Path(sys.argv[1], {name!r}).write_bytes({content!r})")
    return lambda argv: [sys.executable, "-c", body, *argv]


def hangs() -> Callable:
    return lambda argv: [sys.executable, "-c", "import time; time.sleep(30)"]


def run(cases, reference, port, timeout: float = 30) -> tuple[int, str]:
    """Run the cases and return the exit code and everything the report printed."""
    stream = io.StringIO()
    harness = parity.Harness(reference=reference, port=port, env=dict(os.environ),
                             cwd=WORK, work=WORK, timeout=timeout,
                             report=parity.Report(stream))
    return harness.run(cases), stream.getvalue()


def main() -> int:
    failures: list[str] = []

    def check(name: str, ok: bool) -> None:
        print(f"{'OK  ' if ok else 'FAIL'} {name}")
        if not ok:
            failures.append(name)

    case = parity.Case("c", ())
    files = parity.Case("c", ("@OUT@",), outputs=True)

    code, text = run([case], emits(0, "same", "e"), emits(0, "same", "e"))
    check("identical sides pass", code == 0 and "OK   c" in text and "1 passed" in text)

    code, text = run([case], emits(0, "one"), emits(0, "two"))
    check("stdout mismatch fails", code == 1 and "FAIL c" in text and "stdout" in text)

    code, text = run([case], emits(0, "", "one"), emits(0, "", "two"))
    check("stderr mismatch fails", code == 1 and "FAIL c" in text and "stderr" in text)

    code, text = run([case], emits(0), emits(3))
    check("exit code mismatch fails", code == 1 and "exit code: reference 0, port 3" in text)

    code, text = run([files], writes(b"x"), writes(b"x"))
    check("identical files pass",
          code == 0 and "1 passed" in text and "stderr" not in text)

    code, text = run([files], writes(b"x"), writes(b"y"))
    check("file mismatch fails", code == 1 and "a.txt" in text)

    code, text = run([files], writes(b"x"), writes(b"x", "b.txt"))
    check("missing file fails", code == 1 and "files: reference" in text)

    code, text = run([case], hangs(), emits(0), timeout=1)
    check("a hung side fails instead of hanging",
          code == 1 and "timed out after 1s" in text)

    # A case the harness itself cannot handle must fail that case, not abort the run.
    blocked = WORK / "blocked" / "ref"
    blocked.parent.mkdir(parents=True, exist_ok=True)
    blocked.write_bytes(b"")
    code, text = run([parity.Case("blocked", ("@OUT@",), outputs=True)], emits(0), emits(0))
    check("a harness error fails just that case",
          code == 1 and "harness error" in text)

    print("all checks passed" if not failures else f"{len(failures)} failed: "
          f"{', '.join(failures)}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
