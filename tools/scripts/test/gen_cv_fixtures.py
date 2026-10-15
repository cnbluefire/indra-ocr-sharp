"""Generate the OpenCV spot-checks used by the CV primitive tests.

Every case records what the Python/OpenCV reference produces; the C# tests recompute the
same thing with ``Indra.Ocr.Imaging`` and assert bit-parity, so the fixtures pin down the
exact arithmetic of ``cv::resize``, ``cv::warpAffine``, ``cv::getRotationMatrix2D`` and
``cv::connectedComponentsWithStats`` (see docs/040-opencv-primitives.md).

Arrays are stored as base64 of the raw little-endian bytes, which keeps the files small and
the comparison exact (no decimal round-trip in between).
"""

from __future__ import annotations

import base64
import json
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import fixtures_dir  # noqa: E402


def b64(a) -> str:
    return base64.b64encode(np.ascontiguousarray(a).tobytes()).decode()


def u8(a) -> str:
    return b64(np.ascontiguousarray(a, np.uint8))


def f32(a) -> str:
    return b64(np.ascontiguousarray(a, np.float32))


def f64(a) -> str:
    return b64(np.ascontiguousarray(a, np.float64))


def i32(a) -> str:
    return b64(np.ascontiguousarray(a, np.int32))


def write(name: str, doc: dict) -> None:
    path = fixtures_dir() / name
    path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {name}: {path.stat().st_size} bytes")


def rot_m(center, angle, scale):
    return cv2.getRotationMatrix2D(center, angle, scale)


# --------------------------------------------------------------------------- rotation matrices

rng = np.random.default_rng(11)

coarse = [-5.0, -3.7, -0.3, -0.0, 0.0, 0.1, 0.3, 0.5, 2.35, 5.0, 45.0, 90.0, 179.9, 359.0]
# The pipeline searches a 0.5 then 0.1 degree grid, so those exact doubles matter.
grid = []
for base in np.arange(-5.0, 5.5, 0.5):
    grid.extend(np.arange(base - 0.5, base + 0.5 + 1e-6, 0.1).tolist())

centers = [(400.5, 200.5), (0.0, 0.0), (826.5, 1169.0), (1.0, 1.0), (413.0, 275.5)]

rotation = {"cases": []}
for angle in grid:  # every angle the skew search probes, at the real page centre
    rotation["cases"].append(
        {"cx": 400.5, "cy": 200.5, "angle": float(angle), "scale": 1.0,
         "m": f64(rot_m((400.5, 200.5), float(angle), 1.0))}
    )
for angle in coarse + [float(a) for a in grid[::17]]:
    for scale in (1.0, 0.5, 2.0):
        for (cx, cy) in centers:
            m = rot_m((float(cx), float(cy)), float(angle), float(scale))
            rotation["cases"].append(
                {"cx": float(cx), "cy": float(cy), "angle": float(angle),
                 "scale": float(scale), "m": f64(m)}
            )
write("cv_rotation.json", rotation)

# --------------------------------------------------------------------------- resize

resize_cases = [
    # (sw, sh, dw, dh, interpolation): the integer / fractional / upscale shapes.
    (128, 64, 64, 32, cv2.INTER_AREA),     # 2x area fast (the (sum+2)>>2 kernel)
    (96, 96, 48, 48, cv2.INTER_AREA),      # 2x area fast, square
    (80, 60, 16, 12, cv2.INTER_AREA),      # 5x area fast, non-2x2 branch
    (130, 30, 40, 9, cv2.INTER_AREA),      # fractional area
    (101, 77, 37, 29, cv2.INTER_AREA),     # fractional area, odd sizes
    (128, 64, 64, 32, cv2.INTER_LINEAR),   # 2x linear -> promoted to area
    (51, 40, 102, 80, cv2.INTER_LINEAR),   # 2x linear upscale -> promoted to area
    (50, 40, 100, 80, cv2.INTER_LINEAR),   # upscale
    (137, 91, 85, 56, cv2.INTER_LINEAR),   # fractional downscale (generic bilinear)
    (37, 29, 19, 15, cv2.INTER_LINEAR),    # small odd
]

resize = {"cases": []}
for (sw, sh, dw, dh, interp) in resize_cases:
    src = rng.integers(0, 256, size=(sh, sw), dtype=np.uint8)
    dst = cv2.resize(src, (dw, dh), interpolation=int(interp))
    resize["cases"].append({
        "sw": sw, "sh": sh, "dw": dw, "dh": dh,
        "interpolation": int(interp), "dtype": "u8",
        "src": u8(src), "dst": u8(dst),
    })

# float32: only the bilinear upscale line_mask asks for.
for (sw, sh, dw, dh) in [(50, 40, 100, 80), (37, 29, 74, 58)]:
    src = (rng.random((sh, sw)) * 4).astype(np.float32) - 1.5
    dst = cv2.resize(src, (dw, dh), interpolation=cv2.INTER_LINEAR)
    resize["cases"].append({
        "sw": sw, "sh": sh, "dw": dw, "dh": dh,
        "interpolation": int(cv2.INTER_LINEAR), "dtype": "f32",
        "src": f32(src), "dst": f32(dst),
    })
write("cv_resize.json", resize)

# --------------------------------------------------------------------------- warpAffine

warp = {"cases": []}
warp_plan = [
    (53, 37, -3.7, cv2.BORDER_REPLICATE),
    (53, 37, 2.35, cv2.BORDER_REPLICATE),
    (53, 37, 0.3, cv2.BORDER_REPLICATE),
    (53, 37, -3.7, cv2.BORDER_CONSTANT),
    (53, 37, 2.35, cv2.BORDER_CONSTANT),
    (53, 37, 0.0, cv2.BORDER_CONSTANT),
    (200, 129, -3.7, cv2.BORDER_REPLICATE),
    (200, 129, 2.35, cv2.BORDER_CONSTANT),
    (64, 64, 5.0, cv2.BORDER_REPLICATE),
    (64, 64, 5.0, cv2.BORDER_CONSTANT),
]
for (w, h, angle, border) in warp_plan:
    src = rng.integers(0, 256, size=(h, w), dtype=np.uint8)
    m = rot_m((float(w) / 2, float(h) / 2), float(angle), 1.0)
    dst = cv2.warpAffine(src, m, (w, h), flags=cv2.INTER_LINEAR, borderMode=int(border))
    warp["cases"].append({
        "sw": w, "sh": h, "dw": w, "dh": h, "dtype": "u8",
        "m": f64(m), "border": int(border), "borderValue": 0,
        "src": u8(src), "dst": u8(dst),
    })

for (w, h, angle) in [(53, 37, -3.7), (53, 37, 0.3), (37, 29, 2.35), (64, 64, 5.0)]:
    src = rng.random((h, w), dtype=np.float32)
    m = rot_m((float(w) / 2, float(h) / 2), float(angle), 1.0)
    dst = cv2.warpAffine(src, m, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
    warp["cases"].append({
        "sw": w, "sh": h, "dw": w, "dh": h, "dtype": "f32",
        "m": f64(m), "border": int(cv2.BORDER_CONSTANT), "borderValue": 0,
        "src": f32(src), "dst": f32(dst),
    })
write("cv_warp.json", warp)

# --------------------------------------------------------------------------- connected components

ccl = {"cases": []}


def add_ccl(mask, tag):
    n, labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=4)
    h, w = mask.shape
    ccl["cases"].append({
        "tag": tag, "w": w, "h": h,
        "mask": u8(mask), "count": int(n),
        "labels": i32(labels), "stats": i32(stats),
    })


for (w, h) in [(25, 20), (40, 30)]:
    for p in (0.2, 0.35, 0.6, 0.9):
        add_ccl((rng.random((h, w)) < p).astype(np.uint8), f"random {w}x{h} p={p}")

strokes = np.zeros((60, 100), np.uint8)
for row in range(0, 60, 7):
    for seg in range(0, 100, 13):
        strokes[row:row + 3, seg:seg + 9] = 1
add_ccl(strokes, "text strokes")

diag = np.zeros((20, 20), np.uint8)
for i in range(20):
    diag[i, i] = 1
add_ccl(diag, "diagonal (4-connectivity splits it)")

add_ccl(np.ones((3, 4), np.uint8), "all foreground")
add_ccl(np.zeros((5, 5), np.uint8), "all background")
add_ccl(np.ones((1, 9), np.uint8), "single row")
add_ccl(np.ones((9, 1), np.uint8), "single column")
write("cv_ccl.json", ccl)
