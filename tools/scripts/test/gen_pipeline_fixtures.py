"""Generate src/Indra.Ocr.Tests/fixtures/pipeline.json — the parity fixture for step 155
(the ``OrtOCR`` driver: ``detect`` -> ``recognize`` -> ``assemble`` -> ``__call__``).

The inputs are the example pages shipped with the reference under
``resources/indra-src/assets/examples`` (external material, never committed), so the fixture
records each file's sha256 and the sha256 of the grayscale the reference read from it.  The C#
test decodes the same file with the host decoder, checks the hashes, and compares the whole
pipeline against what the reference produced here.

Per case (one input x one option set) it records:

* ``detect``  — the detector's boxes ``[N, 5]`` in page pixels (zlib+base64 float32);
* ``rec``     — the recognizer's output: text and confidence per line, and every token
  (id or character, and the page x) as flat arrays with the per-line counts;
* ``page``    — the assembled page: lines (box, text, confidence), markdown, blocks
  (label, bbox, text), source, note, rotation, skew and the timing keys.

Only the timing *keys* are recorded: the values are wall clock.

Run with the reference environment:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_pipeline_fixtures.py
"""

from __future__ import annotations

import base64
import hashlib
import json
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "resources" / "indra-src"))

import numpy as np  # noqa: E402
from indra_ocr.pipeline import OrtOCR, read_images, rotate_page  # noqa: E402

EXAMPLES = ROOT / "resources" / "indra-src" / "assets" / "examples"
MODELS = ROOT / "resources" / "models"
OUT = ROOT / "src" / "Indra.Ocr.Tests" / "fixtures" / "pipeline.json"

# (example file, derivation, OrtOCR kwargs, turn).  ``derivation`` is applied to the decoded
# page: ``{"rot90": k}`` (np.rot90) or ``{"rotate_page": degrees}`` — both reproducible bit for
# bit by the C# port (steps 090/030), so the derived page is not stored.
CASES = [
    ("multi_column", None, {}, True),
    ("multi_column", None, {"furniture": True}, True),
    ("table_pmc", None, {}, True),
    ("table_pmc", None, {"table_heads": True}, True),
    ("table_pmc", None, {"adaptive": False}, True),
    ("chinese_essay", None, {}, True),
    ("chinese_essay", {"rot90": 1}, {}, True),
    ("chinese_essay", {"rot90": 1}, {}, False),
    ("rotated_letter", None, {}, True),
    ("rotated_letter", {"rotate_page": 2.0}, {}, True),
]


def derive(gray, derivation: dict | None):
    if derivation is None:
        return gray
    if "rot90" in derivation:
        return np.ascontiguousarray(np.rot90(gray, derivation["rot90"]))
    return rotate_page(gray, derivation["rotate_page"])


def z(raw: bytes) -> str:
    return base64.b64encode(zlib.compress(raw, 9)).decode("ascii")


def z_f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32).tobytes())


def z_f64(a) -> str:
    return z(np.ascontiguousarray(a, np.float64).tobytes())


def z_i32(a) -> str:
    return z(np.ascontiguousarray(a, np.int32).tobytes())


def sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def page_json(page) -> dict:
    return {
        "lines": [
            {"box": [float(v) for v in ln.box], "text": ln.text, "conf": float(ln.conf)}
            for ln in page.lines
        ],
        "markdown": page.markdown,
        "blocks": [
            {
                "label": b["label"],
                "bbox": [float(v) for v in b["bbox"]],
                "text": b["text"],
            }
            for b in page.blocks
        ],
        "source": page.source,
        "note": page.note,
        "rotation": int(page.rotation),
        "skew": float(page.skew),
        # The values are wall clock; the keys are part of the output (the CLI prints them).
        "timings": list(page.timings.keys()),
    }


def rec_json(rec: list) -> dict:
    ids, xs, chars = [], [], []
    for _, _, toks in rec:
        for value, x in toks:
            if isinstance(value, str):  # the CJK recognizer's tokens are characters
                chars.append({"i": len(ids), "char": value})
                ids.append(0)
            else:
                ids.append(int(value))
            xs.append(float(x))
    return {
        "lines": [{"text": t, "conf": float(c)} for t, c, _ in rec],
        "tok_counts": [len(tk) for _, _, tk in rec],
        "tok_ids": z_i32(ids),
        "tok_xs": z_f64(xs),
        # Sparse: only the tokens that carry a character (the CJK path).
        "tok_chars": chars,
    }


def main() -> None:
    inputs: dict[str, dict] = {}
    cases = []
    for name, derivation, options, turn in CASES:
        path = EXAMPLES / f"{name}.png"
        raw = path.read_bytes()
        base = read_images(path)[0]
        rel = path.relative_to(ROOT / "resources").as_posix()
        info = inputs.get(name)
        if info is None:
            inputs[name] = info = {
                "name": name,
                "file": rel,
                "sha256": sha(raw),
                "h": int(base.shape[0]),
                "w": int(base.shape[1]),
                "gray_sha256": sha(np.ascontiguousarray(base).tobytes()),
            }

        gray = derive(base, derivation)
        ocr = OrtOCR(MODELS, threads=4, **options)
        # The same steps __call__ runs, so the recorded intermediates are the ones it used.
        upright, turns, skew = ocr.upright(gray, turn)
        boxes = ocr.detect(upright)
        rec = ocr.recognize(upright, boxes)
        page = ocr(gray, turn)

        label = name + "".join(f"_{k}={v}" for k, v in sorted(options.items()))
        if derivation:
            label += "".join(f"_{k}={v}" for k, v in sorted(derivation.items()))
        if not turn:
            label += "_turn=False"
        cases.append(
            {
                "name": label,
                "input": name,
                "derive": derivation,
                "options": {**options, "turn": turn},
                "gray_sha256": sha(np.ascontiguousarray(gray).tobytes()),
                "detect": {"n": int(boxes.shape[0]), "boxes": z_f32(boxes)},
                "rec": rec_json(rec),
                "page": page_json(page),
            }
        )
        print(
            f"  {label:44s} {gray.shape[0]:4d}x{gray.shape[1]:4d} "
            f"boxes={boxes.shape[0]:3d} lines={len(page.lines):3d} "
            f"blocks={len(page.blocks):2d} rot={page.rotation:3d} skew={page.skew:+.2f} "
            f"note={page.note!r} markdown={len(page.markdown or ''):5d}B"
        )

    doc = {
        "note": __doc__.strip().splitlines()[0],
        "inputs": list(inputs.values()),
        "cases": cases,
    }
    text = json.dumps(doc, ensure_ascii=False, indent=1)
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {OUT.relative_to(ROOT)}: {len(text)} bytes ({len(cases)} cases)")


if __name__ == "__main__":
    main()
