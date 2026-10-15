"""Compare a reference output (Python) with a port output (C#).

Deterministic text (markdown / text) must match exactly. JSON-lines are compared
structurally: timing fields are ignored (they vary run to run) and floats are compared
with a small tolerance; everything else must match exactly.

Usage:
    python compare_outputs.py REF GOT [--tol 1e-3] [--mode auto|text|jsonl|dir]

Exit code 0 when equal, 1 otherwise (with a short report on stderr).
"""

from __future__ import annotations

import argparse
import json
import sys
import math
from pathlib import Path

TIMING_SUFFIX = "_ms"


def read_text_auto(path: Path) -> str:
    """Read text from a file, tolerating the BOMs PowerShell redirection adds."""
    raw = path.read_bytes()
    if raw.startswith(b"\xff\xfe") or raw.startswith(b"\xfe\xff"):
        return raw.decode("utf-16")
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw.decode("utf-8-sig")
    return raw.decode("utf-8")


def load(path: Path):
    text = read_text_auto(path)
    stripped = text.lstrip()
    if path.suffix == ".jsonl" or (stripped.startswith("{") and "\n" in text.strip()):
        return "jsonl", [json.loads(l) for l in text.splitlines() if l.strip()]
    return "text", text


def drop_timings(obj):
    if isinstance(obj, dict):
        return {
            k: drop_timings(v)
            for k, v in obj.items()
            if not (k == "timings" or k.endswith(TIMING_SUFFIX))
        }
    if isinstance(obj, list):
        return [drop_timings(v) for v in obj]
    return obj


def cmp_json(a, b, tol, path="$"):
    """Return None when equal, else a string describing the first difference."""
    if isinstance(a, dict) and isinstance(b, dict):
        ka, kb = set(a), set(b)
        if ka != kb:
            return f"{path}: keys differ: only-ref={sorted(ka - kb)} only-got={sorted(kb - ka)}"
        for k in a:
            r = cmp_json(a[k], b[k], tol, f"{path}.{k}")
            if r:
                return r
        return None
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return f"{path}: length {len(a)} != {len(b)}"
        for i, (x, y) in enumerate(zip(a, b)):
            r = cmp_json(x, y, tol, f"{path}[{i}]")
            if r:
                return r
        return None
    if isinstance(a, bool) or isinstance(b, bool):
        return None if a == b else f"{path}: {a!r} != {b!r}"
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        if a == b:
            return None
        if math.isfinite(a) and math.isfinite(b) and abs(a - b) <= tol:
            return None
        return f"{path}: {a!r} != {b!r} (tol {tol})"
    if a != b:
        return f"{path}: {a!r} != {b!r}"
    return None


def compare_units(ref_mode, ref_data, got_mode, got_data, tol) -> str | None:
    if ref_mode == "text" and got_mode == "text":
        if ref_data == got_data:
            return None
        rl, gl = ref_data.splitlines(), got_data.splitlines()
        for i in range(max(len(rl), len(gl))):
            r = rl[i] if i < len(rl) else "<missing>"
            g = gl[i] if i < len(gl) else "<missing>"
            if r != g:
                return f"line {i + 1}:\n  ref: {r!r}\n  got: {g!r}"
        return "text differs"
    if ref_mode == "jsonl" and got_mode == "jsonl":
        if len(ref_data) != len(got_data):
            return f"page count {len(ref_data)} != {len(got_data)}"
        for i, (r, g) in enumerate(zip(ref_data, got_data)):
            d = cmp_json(drop_timings(r), drop_timings(g), tol, f"page[{i}]")
            if d:
                return d
        return None
    return f"mode mismatch: ref={ref_mode} got={got_mode}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("ref", type=Path)
    ap.add_argument("got", type=Path)
    ap.add_argument("--tol", type=float, default=1e-3)
    a = ap.parse_args()

    if a.ref.is_dir() != a.got.is_dir():
        print("compare: ref and got must both be files or both be directories", file=sys.stderr)
        return 2

    if a.ref.is_dir():
        names = sorted(p.name for p in a.ref.iterdir() if p.is_file())
        gnames = sorted(p.name for p in a.got.iterdir() if p.is_file())
        if names != gnames:
            print(f"compare: file sets differ: {names} != {gnames}", file=sys.stderr)
            return 1
        failures = 0
        for n in names:
            rm, rd = load(a.ref / n)
            gm, gd = load(a.got / n)
            d = compare_units(rm, rd, gm, gd, a.tol)
            if d:
                print(f"FAIL {n}:\n  {d}", file=sys.stderr)
                failures += 1
        return 1 if failures else 0

    rm, rd = load(a.ref)
    gm, gd = load(a.got)
    d = compare_units(rm, rd, gm, gd, a.tol)
    if d:
        print(f"FAIL:\n  {d}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
