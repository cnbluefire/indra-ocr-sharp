"""Generate src/Indra.Ocr.Tests/fixtures/det_post.json.

Parity fixtures for ``indra_ocr.det_post`` — the LineNet page preprocessing
(``page_to_input``) and the line decoder (``decode``) — see docs/070-det-post.md.

The reference is the *real* module (imported, not copied) and every decoder case feeds it the
output of the **real** ``det.onnx`` forward pass, so the logits keep the statistics of a
genuine run: some pages are synthetic (ramps / text-like strokes / noise) and some are crops
of ``temp/inputs/*.png`` (run ``python tools/scripts/gen_inputs.py`` first).  A handful of
hand-built logit tensors pin the edge cases (exactly-at-threshold, areas below ``min_area``,
non-default ``thresh`` / ``grow_x`` / ``grow_y``).

Arrays are stored zlib+base64 of the raw little-endian bytes: small, and bit-exact.
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

DET_ONNX = ROOT / "resources" / "models" / "det.onnx"
INPUTS = ROOT / "temp" / "inputs"


def z(a) -> str:
    a = np.ascontiguousarray(a)
    return base64.b64encode(zlib.compress(a.tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


# --------------------------------------------------------------------------- reference steps


def resized_page(img: np.ndarray, long_side: int):
    """The resize step of page_to_input, kept apart so we can store the smaller u8 page."""
    h, w = img.shape
    scale = long_side / max(h, w)
    nh, nw = max(1, round(h * scale)), max(1, round(w * scale))
    if (nh, nw) != (h, w):
        interp = cv2.INTER_AREA if scale < 1 else cv2.INTER_LINEAR
        img = cv2.resize(img, (nw, nh), interpolation=interp)
    return np.ascontiguousarray(img), nh, nw, float(scale)


# --------------------------------------------------------------------------- synthetic pages


def synth(h: int, w: int, seed: int, style: str) -> np.ndarray:
    rng = np.random.default_rng(seed)
    if style == "text":
        img = np.full((h, w), 255, np.uint8)
        y = 2
        while y < h - 2:
            th = min(int(rng.integers(3, 7)), h - 2 - y)
            if th <= 0:
                break
            x = 2
            while x < w - 2:
                wd = min(int(rng.integers(4, 18)), w - 2 - x)
                if wd <= 0:
                    break
                if rng.random() < 0.85:
                    img[y : y + th, x : x + wd] = 0
                x += wd + int(rng.integers(1, 4))
            y += th + int(rng.integers(2, 6))
        return np.ascontiguousarray(img)
    if style == "ramp":
        yy, xx = np.mgrid[0:h, 0:w]
        img = ((xx * 255) // max(1, w - 1)).astype(np.uint8)
        img = ((img.astype(np.int32) + (yy * 255) // max(1, h - 1)) // 2).astype(np.uint8)
        img[::7, :] = 0
        return np.ascontiguousarray(img)
    if style == "noise":
        return np.ascontiguousarray(rng.integers(0, 256, (h, w), dtype=np.uint8))
    if style == "white":
        return np.full((h, w), 255, np.uint8)
    raise ValueError(style)


def best_crop(img: np.ndarray, h: int, w: int) -> np.ndarray:
    """The h x w window of ``img`` carrying the most ink, so det actually finds lines."""
    ih, iw = img.shape
    h, w = min(h, ih), min(w, iw)
    best = None
    for y0 in range(0, ih - h + 1, 3):
        for x0 in range(0, iw - w + 1, 6):
            ink = int((255 - img[y0 : y0 + h, x0 : x0 + w].astype(np.int32)).sum())
            if best is None or ink > best[0]:
                best = (ink, y0, x0)
    return np.ascontiguousarray(img[best[1] : best[1] + h, best[2] : best[2] + w])


# --------------------------------------------------------------------------- cases

pages: list[tuple[str, np.ndarray, int]] = [
    # Synthetic pages: the awkward resize arithmetic (ties-to-even, 1-px clamps, no-op).
    ("synth_37x53_ls128", synth(37, 53, 1, "text"), 128),   # upscale, INTER_LINEAR
    ("synth_64x64_ls64", synth(64, 64, 2, "text"), 64),     # exact size, no resize
    ("synth_100x33_ls96", synth(100, 33, 3, "text"), 96),   # downscale, INTER_AREA
    ("synth_17x64_ls64", synth(17, 64, 4, "text"), 64),     # no resize, thin page
    ("synth_5x8_ls4", synth(5, 8, 5, "ramp"), 4),           # h*scale == 2.5 -> ties to even
    ("synth_3x6_ls1", synth(3, 6, 6, "ramp"), 1),           # h*scale == 0.5 -> clamped to 1
    ("synth_1x8_ls4", synth(1, 8, 7, "ramp"), 4),           # 1-px tall page
    ("synth_201x133_ls128", synth(201, 133, 8, "text"), 128),  # many lines
    ("synth_129x97_noise_ls64", synth(129, 97, 9, "noise"), 64),
    ("synth_64x96_white_ls64", synth(64, 96, 10, "white"), 64),  # blank page
    # Every possible byte in one row, so the `1 - gray/255` float32 path is pinned exactly
    # (the resized u8 page alone would not distinguish a float64 intermediate).
    ("ink_all_bytes_1x256", np.arange(256, dtype=np.uint8).reshape(1, 256), 256),
]

for img_name, crop_hw, ls in [
    ("latin.png", (48, 160), 160),          # exact size, no resize
    ("latin.png", (48, 160), 96),           # downscale, INTER_AREA
    ("cjk.png", (48, 160), 160),
    ("two_columns.png", (64, 192), 128),
]:
    path = INPUTS / img_name
    if not path.exists():
        raise SystemExit(f"missing {path}; run: python tools/scripts/gen_inputs.py")
    gray = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
    pages.append((f"real_{path.stem}_{crop_hw[1]}x{crop_hw[0]}_ls{ls}", best_crop(gray, *crop_hw), ls))

print(f"det.onnx: {DET_ONNX}")
sess = ort.InferenceSession(str(DET_ONNX), providers=["CPUExecutionProvider"])

cases = []
for name, img, ls in pages:
    ink, scale = page_to_input(img, ls)
    ref_gray, nh, nw, scale2 = resized_page(img, ls)
    assert scale == scale2
    ph, pw = ink.shape
    # the stored u8 page must reproduce the float32 ink map exactly
    assert np.array_equal(ink[:nh, :nw], 1.0 - ref_gray.astype(np.float32) / np.float32(255.0))
    assert not ink[nh:, :].any() and not ink[:, nw:].any(), "padding must stay white"

    out = sess.run(None, {"x": np.ascontiguousarray(ink)[None, None]})[0][0]
    boxes = decode(out)
    assert boxes.dtype == np.float32 and boxes.shape[1] == 5

    cases.append(
        {
            "name": name,
            "h": int(img.shape[0]),
            "w": int(img.shape[1]),
            "image": z(img),
            "long_side": int(ls),
            "scale": scale,
            "nh": int(nh),
            "nw": int(nw),
            "ph": int(ph),
            "pw": int(pw),
            "gray": z(ref_gray),
            "oh": int(out.shape[1]),
            "ow": int(out.shape[2]),
            "logits": f32(out),
            "thresh": 0.5,
            "min_area": 2,
            "grow_x": 0.0,
            "grow_y": 0.0,
            "boxes": f32(boxes.reshape(-1)),
        }
    )
    # For the small pages also store the whole float32 ink map, so the C# test checks the
    # exact floats rather than the (compact) resized bytes.
    if ink.size <= 8192:
        cases[-1]["ink_map"] = f32(ink)
    print(f"  {name}: page {img.shape} -> {ink.shape}, {boxes.shape[0]} boxes")

# --------------------------------------------------------------------------- hand-built logits


def stack(core: np.ndarray, top, bot) -> np.ndarray:
    return np.stack(
        [np.asarray(core, np.float32), np.full_like(core, top, np.float32), np.full_like(core, bot, np.float32)]
    )


decode_only = []


def add_manual(name, out, thresh=0.5, min_area=2, grow_x=0.0, grow_y=0.0):
    boxes = decode(out, thresh=thresh, min_area=min_area, grow_x=grow_x, grow_y=grow_y)
    decode_only.append(
        {
            "name": name,
            "oh": int(out.shape[1]),
            "ow": int(out.shape[2]),
            "logits": f32(out),
            "thresh": float(thresh),
            "min_area": int(min_area),
            "grow_x": float(grow_x),
            "grow_y": float(grow_y),
            "boxes": f32(boxes.reshape(-1)),
        }
    )
    print(f"  {name}: {out.shape} -> {boxes.shape[0]} boxes")


# No ink at all: sigmoid(-20) is tiny -> connectedComponentsWithStats sees one background label.
add_manual("manual_empty", stack(np.full((8, 8), -20.0), 0.0, 0.0))

# core logit 0 -> prob exactly 0.5, which is *not* > thresh; a hair above it is kept.
c = np.full((8, 8), -10.0, np.float32)
c[1:3, 1:4] = 0.0        # excluded
c[4:6, 1:4] = 1e-4       # included
add_manual("manual_threshold_exact", stack(c, 0.25, 0.5))

# Areas below min_area are dropped, the smallest kept area is min_area.
c = np.full((8, 8), -10.0, np.float32)
c[3, 3] = 5.0            # 1 px -> dropped
c[5, 5:7] = 5.0          # 2 px -> kept
add_manual("manual_min_area", stack(c, 0.0, 0.0))

# A 4-connected diagonal is three separate labels, not one blob; the horizontal pair next to
# it shows the sizes that survive min_area.  An 8-connected implementation would merge the
# diagonal into one kept blob and produce a different box.
c = np.full((10, 10), -10.0, np.float32)
c[1, 1] = c[2, 2] = c[3, 3] = 5.0     # 1 px each -> all dropped
c[6, 1] = c[6, 2] = 5.0               # 2 px -> kept
add_manual("manual_connectivity4", stack(c, 0.1, 0.2))

# Non-default parameters, to pin thresh / grow_x / grow_y.
c = np.full((12, 12), -4.0, np.float32)
c[2:5, 2:9] = 0.2
c[7:9, 3:6] = 0.05
add_manual("manual_grow", stack(c, -0.3, 0.7), thresh=0.25, grow_x=0.05, grow_y=0.04)

# Distances large enough to make the boxes cross / go negative (no clamping in decode).
c = np.full((10, 10), -1.0, np.float32)
c[2:8, 2:8] = 4.0
add_manual("manual_wide_dist", stack(c, 6.0, 2.0), grow_x=1.0, grow_y=1.0)

doc = {
    "note": "fixtures for indra_ocr.det_post (page_to_input + decode); arrays are "
            "zlib+base64 of raw little-endian bytes (see tools/scripts/test/gen_det_fixtures.py)",
    "cases": cases,
    "decode_only": decode_only,
}
path = fixtures_dir() / "det_post.json"
path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {path.name}: {path.stat().st_size} bytes ({len(cases)} pages, {len(decode_only)} manual)")
