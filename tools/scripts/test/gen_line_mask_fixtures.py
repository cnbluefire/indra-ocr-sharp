"""Generate src/Indra.Ocr.Tests/fixtures/line_mask.json.

Parity fixtures for ``indra_ocr.line_mask`` — the CenterMask windowing (``window_box`` /
``_window`` / ``resize_window`` / ``mask_windows`` / ``masked_windows`` / ``crop_lines``) — see
docs/085-line-mask.md.

CenterMask needs ``mask.onnx``, which the shipped model set does not have, so the reference is
driven with a *stand-in* model that both sides implement identically and exactly:

    "flip"      p = 1 - x
    "parabola"  p = 4*x*(1-x)        (all in float32)

The stand-in replaces only the convolution stack; the batching, the padding, the bilinear
upscale of the probabilities, the 0.5 threshold, the paper-level percentile and the whitening
are the real code paths.  Windows are real: the small pages are crops of ``temp/inputs/*.png``
(run ``python tools/scripts/gen_inputs.py`` first) and the boxes come from the real ``det.onnx``,
clipped to the page the way ``OcrOCR.detect`` clips them.

Arrays are stored zlib+base64 of the raw little-endian bytes (see ``B64.ZBytes``/``ZFloats``).
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
from indra_ocr.det_post import decode, page_to_input  # noqa: E402
from indra_ocr.line_mask import _window, crop_lines, mask_windows, masked_windows  # noqa: E402
from indra_ocr.rec_preproc import LINE_HEIGHT  # noqa: E402

DET_ONNX = ROOT / "resources" / "models" / "det.onnx"
INPUTS = ROOT / "temp" / "inputs"


def z(a) -> str:
    a = np.ascontiguousarray(a)
    return base64.b64encode(zlib.compress(a.tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


class StandIn:
    """Deterministic replacement for mask.onnx (see the module docstring)."""

    def __init__(self, kind: str):
        self.kind = kind

    def run(self, output_names, feeds):
        x = feeds["x"]
        if self.kind == "flip":
            p = np.float32(1.0) - x
        elif self.kind == "parabola":
            p = (np.float32(4.0) * x) * (np.float32(1.0) - x)
        else:
            raise ValueError(self.kind)
        assert p.dtype == np.float32 and p.shape == x.shape
        return [p]


def best_crop(img: np.ndarray, h: int, w: int) -> np.ndarray:
    ih, iw = img.shape
    h, w = min(h, ih), min(w, iw)
    best = None
    for y0 in range(0, ih - h + 1, 3):
        for x0 in range(0, iw - w + 1, 6):
            ink = int((255 - img[y0 : y0 + h, x0 : x0 + w].astype(np.int32)).sum())
            if best is None or ink > best[0]:
                best = (ink, y0, x0)
    return np.ascontiguousarray(img[best[1] : best[1] + h, best[2] : best[2] + w])


pages: dict[str, np.ndarray] = {}
crops: dict[str, list[np.ndarray]] = {}

for name, size in [("latin.png", (96, 256)), ("cjk.png", (80, 200))]:
    path = INPUTS / name
    if not path.exists():
        raise SystemExit(f"missing {path}; run: python tools/scripts/gen_inputs.py")
    gray = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
    tag = f"page_{Path(name).stem}_{size[1]}x{size[0]}"
    pages[tag] = best_crop(gray, *size)
    crops[tag] = []

small = np.full((72, 96), 255, np.uint8)
small[8:16, 6:88] = 0
small[30:36, 10:80] = 0
small[54:60, 20:70] = 0
pages["page_bands_96x72"] = small
crops["page_bands_96x72"] = []

# A grey page: a gradient with text-like bands of intermediate levels, so a stand-in model
# produces mixed keep patterns (a binary page only ever keeps everything or nothing).
rng = np.random.default_rng(2026)
yy, xx = np.mgrid[0:72, 0:96]
grey = (60 + 120 * xx / 95).astype(np.uint8)
grey[10:18, 8:86] = 30
grey[28:36, 8:86] = 140
grey[46:54, 8:86] = 200
grey = np.clip(grey.astype(np.int16) + rng.integers(-25, 26, grey.shape), 0, 255).astype(np.uint8)
pages["page_grey_96x72"] = np.ascontiguousarray(grey)
crops["page_grey_96x72"] = []

# --------------------------------------------------------------------------- boxes

sess = ort.InferenceSession(str(DET_ONNX), providers=["CPUExecutionProvider"])

for tag, page in pages.items():
    h, w = page.shape
    x = page_to_input(page, 256)[0]
    out = sess.run(None, {"x": np.ascontiguousarray(x)[None, None]})[0][0]
    boxes = decode(out).copy()
    if len(boxes):
        boxes[:, :4] /= 256 / max(h, w)
        # What OcrOCR.detect does before recognize() sees them.
        boxes[:, [0, 2]] = boxes[:, [0, 2]].clip(0, w)
        boxes[:, [1, 3]] = boxes[:, [1, 3]].clip(0, h)
        crops[tag] = np.ascontiguousarray(boxes[:, :4])
    print(f"  {tag}: {len(boxes)} detected boxes")

# Hand-placed boxes: touching every page edge and spanning the full width.
edge = [
    [0.0, 0.0, 60.0, 14.0],
    [36.0, 60.0, 96.0, 72.0],
    [-20.0, 30.0, 40.0, 38.0],
    [50.0, 36.0, 130.0, 44.0],
    [6.0, 30.0, 90.0, 36.0],
    [2.0, 54.5, 94.0, 60.5],
]
crops["page_bands_96x72"] = [np.asarray(b, np.float32) for b in edge]
crops["page_grey_96x72"] = [np.asarray(b, np.float32) for b in edge]

# --------------------------------------------------------------------------- cases

cases = []
for tag, model_kind in [("page_latin_256x96", "flip"),
                        ("page_cjk_200x80", "flip"),
                        ("page_bands_96x72", "parabola"),
                        ("page_grey_96x72", "parabola")]:
    page = pages[tag]
    boxes = np.asarray(crops[tag], np.float32).reshape(-1, 4)
    if not len(boxes):
        continue
    stand = StandIn(model_kind)

    wins = [_window(page, b) for b in boxes]
    keeps = mask_windows(stand, [w[0] for w in wins])
    masked = masked_windows(page, boxes, stand)
    lines = crop_lines(page, boxes, LINE_HEIGHT, stand)

    assert len(keeps) == len(boxes) == len(masked) == len(lines)
    case = {
        "name": f"{tag}_{model_kind}",
        "model": model_kind,
        "page": tag,
        "height": LINE_HEIGHT,
        "boxes": f32(boxes.reshape(-1)),
        "keeps": [z(np.asarray(k, np.uint8).reshape(-1)) for k in keeps],
        "keep_sizes": [[int(k.shape[1]), int(k.shape[0])] for k in keeps],
        "windows": [
            {"x": int(ox), "y": int(oy), "w": int(m.shape[1]), "h": int(m.shape[0]),
             "pixels": z(m), "box": [float(v) for v in b]}
            for (m, b), (_, (ox, oy)) in zip(masked, wins)
        ],
        "lines": [{"w": int(ln.shape[1]), "ink": f32(ln)} for ln in lines],
    }
    cases.append(case)
    fracs = ", ".join(f"{float(k.mean()):.2f}" for k in keeps)
    print(f"  {case['name']}: {len(boxes)} boxes, keep fractions [{fracs}], "
          f"line widths {[int(l.shape[1]) for l in lines]}")

doc = {
    "note": "fixtures for indra_ocr.line_mask (CenterMask windowing); the model is a "
            "deterministic stand-in for the absent mask.onnx - see "
            "tools/scripts/test/gen_line_mask_fixtures.py; arrays are zlib+base64 of raw "
            "little-endian bytes",
    "pages": {t: {"h": int(p.shape[0]), "w": int(p.shape[1]), "gray": z(p)} for t, p in pages.items()},
    "cases": cases,
}
path = fixtures_dir() / "line_mask.json"
path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {path.name}: {path.stat().st_size} bytes ({len(cases)} cases)")
