"""Generate src/Indra.Ocr.Tests/fixtures/adaptive_detect.json.

Parity fixtures for ``pipeline.OrtOCR.detect`` — one detection pass, the adaptive second pass and
the scale/clip tail — see docs/095-adaptive-detect.md.

The expected boxes come from the *real* ``OrtOCR.detect``, reached through a stub object that only
carries what the method touches (``det``, ``long_side``, ``adaptive`` and the real ``_detect_at``),
and the generator replays the branch by hand first: the replay has to agree with ``detect``
bit-for-bit, which is what pins the reading of the source.

Two families of cases:

* ``cases`` — the real ``det.onnx``.  The pages are real ones from ``temp/inputs`` plus a big
  canvas (small text on a large page) that makes the second pass fire, and the long sides are
  chosen to hit every branch: a second pass with an uncapped long side, one capped at
  ``ADAPTIVE_MAX_SIDE``, one where the candidate equals the first pass (no rerun), a median above
  ``ADAPTIVE_MIN_H``, fewer than eight boxes, no boxes at all, and ``adaptive=False``.
* ``standin`` — a deterministic stand-in model (bars of fixed height and a fixed top/bottom
  distance) that needs no model file, so the branch logic and the clipping stay under test
  everywhere.  Its logits depend only on the input size, and the C# test implements the same
  function (``DetectTests.StandIn``).

Each case records the first pass's boxes and scale, the median height, the long side the second
pass used, and the final boxes, so a mismatch points at the pass it came from.
"""

from __future__ import annotations

import base64
import json
import sys
import zlib
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import fixtures_dir  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "resources" / "indra-src"))
from indra_ocr.pipeline import (  # noqa: E402
    ADAPTIVE_MAX_SIDE,
    ADAPTIVE_MIN_H,
    ADAPTIVE_TARGET_H,
    OrtOCR,
)

INPUTS = ROOT / "temp" / "inputs"
DET_ONNX = ROOT / "resources" / "models" / "det.onnx"


def z(a) -> str:
    a = np.ascontiguousarray(a)
    return base64.b64encode(zlib.compress(a.tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


def read(name: str) -> np.ndarray:
    img = cv2.imread(str(INPUTS / name), cv2.IMREAD_GRAYSCALE)
    assert img is not None, name
    return img


# --------------------------------------------------------------------------- pages

latin = read("latin.png")
canvas = np.full((1600, 2400), 255, np.uint8)
for i in range(3):
    for j in range(4):
        canvas[100 + i * 500 : 180 + i * 500, 100 + j * 560 : 500 + j * 560] = \
            latin[100:180, 100:500]

pages: dict[str, np.ndarray] = {
    "latin": latin,
    "cjk": read("cjk.png"),
    "two_columns": read("two_columns.png"),
    "blank": read("blank.png"),
    "rot90": read("latin_rot90.png"),
    "small": latin[200:260, 300:450],          # 60x150: a few lines, upscaled by the detector
    "canvas": canvas,                          # 1600x2400 of small text -> second pass
    "stub": latin[300:334, 400:444],           # the stand-in cases only need a size
}


# --------------------------------------------------------------------------- the model seam


class DetStub:
    """``OrtOCR`` carrying just what ``detect`` touches: the session, long_side, adaptive."""

    _detect_at = OrtOCR._detect_at
    detect = OrtOCR.detect

    def __init__(self, model, long_side: int, adaptive: bool):
        self.det = model
        self.long_side = long_side
        self.adaptive = adaptive


class StandinDet:
    """A deterministic stand-in for ``det.onnx``: ``bars`` horizontal bars of ``hb`` rows,
    in the core channel at 10 (sigmoid -> 1) and in both distance channels at ``dist``, so every
    box comes back ``2 * dist`` tall.  ``edge`` spans the full width, which makes the boxes stick
    out of the page and the clipping matter.  Only the input size is used, and the C# test
    implements this function identically."""

    def __init__(self, bars: int, dist: float, edge: bool = False):
        self.bars = bars
        self.dist = dist
        self.edge = edge

    def run(self, _outputs, feed):
        x = feed["x"]
        h, w = x.shape[-2], x.shape[-1]
        oh, ow = h // 2, w // 2
        out = np.zeros((1, 3, oh, ow), np.float32)
        bars = self.bars
        if bars > 0:
            gap = max(2, oh // (bars * 3))
            hb = max(1, oh // (bars * 4))
            for i in range(bars):
                y = 2 + i * (hb + gap)
                if y + hb > oh:
                    break
                x0 = 0 if self.edge else ow // 8
                x1 = ow if self.edge else ow - ow // 8
                out[0, 0, y : y + hb, x0:x1] = 10.0
                out[0, 1, y : y + hb, x0:x1] = self.dist
                out[0, 2, y : y + hb, x0:x1] = self.dist
        return [out]


class RealDet:
    """The real session, wrapped so it looks like ``StandinDet`` to ``_detect_at``."""

    def __init__(self, session):
        self.session = session

    def run(self, _outputs, feed):
        return self.session.run(None, feed)


# --------------------------------------------------------------------------- detect, replayed


def replay(model, gray: np.ndarray, long_side: int, adaptive: bool):
    """A hand-written ``detect``.  Returns the pieces the fixture records, and the final boxes,
    which the caller checks against the real method."""
    stub = DetStub(model, long_side, adaptive)
    boxes0, s0 = stub._detect_at(gray, long_side)

    median_h = None
    ls = long_side
    if adaptive and len(boxes0) >= 8:
        median_h = float(np.median(boxes0[:, 3] - boxes0[:, 1]))
        if median_h < ADAPTIVE_MIN_H:
            wanted = int(min(ADAPTIVE_MAX_SIDE,
                             long_side * ADAPTIVE_TARGET_H / max(median_h, 1.0))) // 32 * 32
            if wanted > long_side:
                ls = wanted

    boxes, s = stub._detect_at(gray, ls) if ls != long_side else (boxes0, s0)
    final = boxes.copy()
    clipped = 0
    if len(final):
        final[:, :4] /= s
        h, w = gray.shape
        before = final[:, :4].copy()
        final[:, [0, 2]] = final[:, [0, 2]].clip(0, w)
        final[:, [1, 3]] = final[:, [1, 3]].clip(0, h)
        clipped = int(np.sum(np.any(before != final[:, :4], axis=1)))

    return stub, boxes0, s0, median_h, ls, final, clipped


def case(name, model, page_name, long_side, adaptive):
    gray = pages[page_name]
    stub, boxes0, s0, median_h, ls, final, clipped = replay(model, gray, long_side, adaptive)
    reference = stub.detect(gray)
    assert reference.shape == final.shape and np.array_equal(reference, final), \
        f"{name}: replay of detect() differs from the real method"
    rec = {
        "name": name,
        "page": page_name,
        "long_side": int(long_side),
        "adaptive": bool(adaptive),
        "model": "standin" if isinstance(model, StandinDet) else "real",
        "boxes0": f32(boxes0),
        "n0": int(len(boxes0)),
        "scale0": float(s0),
        "median_h": median_h,
        "ls": int(ls),
        "boxes": f32(final),
        "n": int(len(final)),
        "clipped": clipped,
    }
    if isinstance(model, StandinDet):
        rec["standin"] = {"bars": int(model.bars), "dist": float(model.dist),
                          "edge": bool(model.edge)}
    print(f"  {name:30s} pass1={len(boxes0):4d} med={median_h if median_h is None else round(median_h, 2)}"
          f" -> ls={ls:5d} boxes={len(final):4d} clipped={clipped}")
    return rec


sess = None
if DET_ONNX.exists():
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1        # matches OnnxSessions.CreateOptions(1) in the tests
    so.inter_op_num_threads = 1
    sess = ort.InferenceSession(str(DET_ONNX), so, providers=["CPUExecutionProvider"])
    so.log_severity_level = 3

real_model = RealDet(sess) if sess is not None else None

cases = []
if real_model is not None:
    cases = [
        case("canvas_256", real_model, "canvas", 256, True),          # second pass -> 544
        case("canvas_1024", real_model, "canvas", 1024, True),        # second pass -> 1408
        case("canvas_1024_static", real_model, "canvas", 1024, False),  # adaptive off
        case("rot90_512", real_model, "rot90", 512, True),            # med 9.38: no second pass
        case("rot90_1024", real_model, "rot90", 1024, True),          # med 14.55: no second pass
        case("latin_256", real_model, "latin", 256, True),            # 4 boxes: count gate
        case("two_columns_1024", real_model, "two_columns", 1024, True),  # 5 boxes
        case("small_512", real_model, "small", 512, True),            # 3 boxes
        case("cjk_1024", real_model, "cjk", 1024, True),              # 3 boxes
        case("blank_256", real_model, "blank", 256, True),            # no boxes at all
    ]
else:
    print("  det.onnx missing: the fixture has stand-in cases only")

standin_cases = [
    case("standin_grid10_d3_256", StandinDet(10, 3.0), "stub", 256, True),
    case("standin_grid10_d3_1024", StandinDet(10, 3.0), "stub", 1024, True),   # capped at 2048
    case("standin_grid10_d3_2048", StandinDet(10, 3.0), "stub", 2048, True),   # no rerun
    case("standin_grid10_d1_256", StandinDet(10, 1.0), "stub", 256, True),     # -> 1536
    case("standin_grid10_d5_256", StandinDet(10, 5.0), "stub", 256, True),     # med 10: none
    case("standin_grid10_d15_300", StandinDet(10, 15.0), "stub", 300, True),   # med 30, odd side
    case("standin_grid5_d3_256", StandinDet(5, 3.0), "stub", 256, True),       # < 8 boxes
    case("standin_grid0_256", StandinDet(0, 3.0), "stub", 256, True),          # no boxes
    case("standin_edge10_d3_256", StandinDet(10, 3.0, edge=True), "stub", 256, True),
    case("standin_grid10_d3_256_static", StandinDet(10, 3.0), "stub", 256, False),
]

doc = {
    "note": "fixtures for pipeline.OrtOCR.detect (one pass, the adaptive second pass and the "
            "scale/clip tail); arrays are zlib+base64 of raw little-endian bytes "
            "(see tools/scripts/test/gen_detect_fixtures.py)",
    "adaptive_min_h": float(ADAPTIVE_MIN_H),
    "adaptive_target_h": float(ADAPTIVE_TARGET_H),
    "adaptive_max_side": int(ADAPTIVE_MAX_SIDE),
    "det_long_side": 1024,
    "pages": {n: {"h": int(p.shape[0]), "w": int(p.shape[1]), "gray": z(p)}
              for n, p in pages.items()},
    "cases": cases,
    "standin": standin_cases,
}
path = fixtures_dir() / "adaptive_detect.json"
path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {path.name}: {path.stat().st_size} bytes "
      f"({len(pages)} pages, {len(cases)} real cases, {len(standin_cases)} stand-in cases)")
