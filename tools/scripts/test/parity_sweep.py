"""End-to-end parity sweep: the reference's own material through both commands.

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\parity_sweep.py [name ...]

``cli_parity.py`` pins the command line on inputs the harness generates itself — fixed bytes,
one option per case.  This sweep goes the other way: it takes everything the reference ships
under ``resources/indra-src/assets`` (the four example pages, the two benchmark charts and the
logo) and runs each of them through both commands in all three output formats, then all of them
at once through the worker pool (parallel and serial) and through ``--out``.  The examples are
real material — a Chinese essay, a two-column paper page, a rotated letter and a table — which is
what the orientation, layout and CJK paths exist for.

The comparison is ``cli_parity.py``'s: the exit code, stdout, stderr and any files written, byte
for byte, with the wall-clock numbers masked.  The case list is discovered from the asset folder,
so a new asset joins the sweep by itself, and every case is skipped when ``resources/`` is not
there (it is externally downloaded material and not in git).

    --list              the cases and their argv
    --jobs N            cases to run at once (default: one per core, capped at 8)
    --serial            one at a time
    NAME ...            case names or globs (``examples-*``), default all
"""

from __future__ import annotations

import sys
from pathlib import Path

import cli_parity
import parity

ROOT = Path(__file__).resolve().parents[3]
ASSETS = ROOT / "resources" / "indra-src" / "assets"
MODELS = "resources/models"

#: The three output formats.  ``--dpi`` is not an axis here: an image file is already pixels, so
#: the reference ignores the flag for one (``image-md`` and ``image-dpi`` differ only in the PDF
#: cases), and the PDF samples have their own harness.
FORMATS = ("md", "text", "json")


def assets() -> list[Path]:
    """Every PNG the reference ships, in a stable order."""
    return sorted(ASSETS.rglob("*.png")) if ASSETS.is_dir() else []


def missing() -> str | None:
    """Why the sweep cannot run here, or ``None`` when it can."""
    if not assets():
        return "resources/indra-src/assets is not there"
    if not (ROOT / MODELS / "meta.json").exists():
        return "resources/models is not there"
    return None


def name(path: Path) -> str:
    """``examples/multi_column.png`` -> ``examples-multi_column``."""
    return path.relative_to(ASSETS).with_suffix("").as_posix().replace("/", "-")


def build() -> list[parity.Case]:
    paths = assets()
    relative = [path.relative_to(ROOT).as_posix() for path in paths]
    cases: list[parity.Case] = []
    for path in paths:
        for fmt in FORMATS:
            extra = () if fmt == "md" else ("--format", fmt)
            cases.append(parity.Case(f"{name(path)}-{fmt}",
                                     (path.relative_to(ROOT).as_posix(), *extra,
                                      "--model-dir", MODELS),
                                     skip_if=missing))
    if paths:
        # Seven units is past MIN_POOL_PAGES, so this is the worker pool: once with the default
        # worker count and once with the serial fallback.  ``--out`` then writes one file per
        # input through the same pool.
        cases.append(parity.Case("pool-assets", (*relative, "--model-dir", MODELS),
                                 skip_if=missing))
        cases.append(parity.Case("pool-assets-cores1",
                                 (*relative, "--cores", "1", "--model-dir", MODELS),
                                 skip_if=missing))
        cases.append(parity.Case("out-assets",
                                 (*relative, "--out", "@OUT@", "--model-dir", MODELS),
                                 outputs=True, skip_if=missing))
    return cases


CASES = build()


def main() -> int:
    options = parity.parse(sys.argv[1:])
    cases = parity.select(CASES, options.names)
    if options.listing:
        for case in cases:
            print(f"{case.name}\t{' '.join(case.argv)}")
        return 0

    harness = parity.Harness(
        reference=cli_parity.reference_command,
        port=cli_parity.port_command,
        env=cli_parity.ENV,
        cwd=ROOT,
        work=ROOT / "temp" / "parity_sweep",
        mask=cli_parity.mask,
        jobs=options.jobs,
        timeout=600,
    )
    return harness.run(cases)


if __name__ == "__main__":
    sys.exit(main())
