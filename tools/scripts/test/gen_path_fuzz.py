"""Generate src/Indra.Ocr.Tests/fixtures/path_fuzz.json.

Parity fixture for ``Indra.Ocr.PathHelper`` (step 375, docs/375-path-parity.md): CPython's own
answers, for **both** pathlib flavours, over path-shaped strings drawn from a nasty alphabet
(drives, UNC and device prefixes, doubled separators, ``.``/``..``, ``~``, spaces, non-ASCII).
Four kinds, all read from ``pathlib`` 3.14 on the machine that generates the fixture:

* ``normalize``  - ``str(Pure*(a))`` (what the reference prints: messages, the JSON's ``file``);
* ``stem``       - ``Pure*(a).stem`` (the CLI's ``--out`` file name);
* ``join``       - ``str(Pure*(a) / b)`` (``--out`` / the name, ``--model-dir`` + a model file);
* ``expanduser`` - ``Pure*(a).expanduser()`` with ``HOME``/``USERPROFILE`` pinned (the fixture
                   records both, and the C# test passes them in, so neither side reads the
                   machine's real home).

``Pure*`` answers the POSIX rules on a Windows host too, so one run of this script pins both
flavours.  ``expanduser`` is a ``Path`` (not ``PurePath``) method, and a ``PosixPath`` cannot even
be built on Windows, so its 3.14 body is run here on a ``Pure*`` instance; for the Windows flavour
that reconstruction is checked against the real ``pathlib.Path(a).expanduser()`` case by case.

A first component that is not exactly ``~`` (``~user``, ``~x``) is left to the unit tests: cp looks
the user up and *raises* when it cannot, which the port deliberately does not reproduce
(docs/370-path-flavour.md), so those words are not fuzzed.

Run:  tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_path_fuzz.py [per-kind] [seed]

The default corpus is the committed one; a larger ``per-kind`` and another seed make a deeper
sweep (the answers come from cp either way, so only the fixture differs).
"""

from __future__ import annotations

import ntpath
import os
import pathlib
import platform
import posixpath
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import write  # noqa: E402

ALPHABET = [
    "", "/", "//", "\\", "\\\\", ":", ":.", ".", "..", "...", "a", "ab", "b b", "é", "~", "~x",
    "C", "C:", "C:/", "C:\\", "server", "share", "//srv", "\\\\srv", ".hidden", "a.b", "b.", "..a",
    "?", "UNC", "//?", "\\\\?", "//?/", "//./", "\\\\?\\", "\\\\?\\UNC\\", "\\\\.\\", "c:", "a/",
    "/a", " ", "  ", "0", "%", "*",
]

PARSERS = {"posix": (pathlib.PurePosixPath, posixpath), "windows": (pathlib.PureWindowsPath, ntpath)}

PER_KIND = 150
SEED = 20261017

# The home directories both sides work with: expanduser reads the environment, so the fixture
# records these and the C# test hands them to PathHelper.ExpandUser.
HOMES = {"posix": "/home/runner", "windows": "C:\\Users\\runner"}


def expanduser(pure, parser, path):
    """``pathlib.PurePath.expanduser``'s body (3.14), on a ``Pure*`` instance."""
    p = pure(path)
    tail = p._tail
    if not (p.drive or p.root) and tail and tail[0][:1] == "~":
        homedir = parser.expanduser(tail[0])
        if homedir[:1] == "~":
            raise RuntimeError("Could not determine home directory.")
        drv, root, rest = pure._parse_path(homedir)
        return str(p._from_parsed_parts(drv, root, rest + tail[1:]))
    return str(p)


def main() -> None:
    per_kind = int(sys.argv[1]) if len(sys.argv) > 1 else PER_KIND
    seed = int(sys.argv[2]) if len(sys.argv) > 2 else SEED
    rng = random.Random(seed)

    os.environ["HOME"] = HOMES["posix"]
    os.environ["USERPROFILE"] = HOMES["windows"]

    def word() -> str:
        return "".join(rng.choice(ALPHABET) for _ in range(rng.randint(0, 6)))

    pairs = []
    for _ in range(per_kind):
        pairs.append((word(), word()))
        # A tilde-led pair as well: bare `~` first components are what expanduser turns on, and the
        # nasty alphabet rarely produces one on its own.
        pairs.append(("~" + rng.choice(["", "/", "\\", "\\\\", "//", ".", "~", "user", "x"]) + word(),
                      word()))

    cases = []
    skipped = 0
    for a, b in pairs:
        for flavour in ("posix", "windows"):
            pure, parser = PARSERS[flavour]
            cases.append(["normalize", flavour, a, "", str(pure(a))])
            cases.append(["stem", flavour, a, "", pure(a).stem])
            cases.append(["join", flavour, a, b, str(pure(a) / b)])

            # The concrete `Path` of this host is the Windows flavour, so that is the one whose
            # pure-graded answer can be checked against the real thing — value and refusal alike.
            try:
                expanded = expanduser(pure, parser, a)
            except RuntimeError:
                expanded = None

            if flavour == "windows":
                try:
                    real = str(pathlib.Path(a).expanduser())
                except RuntimeError:
                    real = None

                assert real == expanded, (a, real, expanded)

            if pure(a)._tail[:1] == ["~"]:
                cases.append(["expanduser", flavour, a, "", expanded])
            else:
                skipped += 1

    write("path_fuzz.json", {
        "python": platform.python_version(),
        "seed": seed,
        "per_kind": per_kind,
        "homes": HOMES,
        "alphabet": ALPHABET,
        "cases": cases,
    })
    counts = {}
    for kind, flavour, *_ in cases:
        counts[kind + " " + flavour] = counts.get(kind + " " + flavour, 0) + 1
    print(f"{len(cases)} cases ({skipped} expanduser rows skipped as '~user'/non-'~')")
    for key in sorted(counts):
        print(f"  {key:<22} {counts[key]}")


if __name__ == "__main__":
    main()
