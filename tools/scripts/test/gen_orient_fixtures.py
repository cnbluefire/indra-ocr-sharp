"""Generate src/Indra.Ocr.Tests/fixtures/orient.json.

Parity fixtures for the page-uprighting half of ``indra_ocr.pipeline`` — ``orient_input``,
``orientation`` (orient.onnx), ``estimate_skew``, ``rotate_page``, ``unrotate_box`` and
``upright`` — see docs/090-orientation-deskew.md.

Every expected value comes from the reference implementation on the pages below (real pages from
``temp/inputs`` plus synthetic ones with a known tilt, noise and degenerate shapes).  The pages
are stored once, zlib+base64 of the raw uint8 pixels, and referenced by name; float32 arrays use
the same encoding (see ``B64.ZBytes``/``ZFloats``).

``estimate_skew`` records the angle it returns *and* the whole sharpness profile it ranks, so a
mismatch points at the thumbnail, the float32 sums or the search order.

``orient.onnx`` drives the ``turns``/``upright`` cases; the ``*_standin`` cases use a
deterministic stand-in model (the mean ink of each quadrant of the input) that both languages
implement identically, so the orientation logic stays testable when the model is absent.
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
    DESKEW_MAX,
    DESKEW_MIN,
    ORIENT_MIN,
    ORIENT_SIDE,
    estimate_skew,
    orient_input,
    rotate_page,
    unrotate_box,
)

INPUTS = ROOT / "temp" / "inputs"
ORIENT_ONNX = ROOT / "resources" / "models" / "orient.onnx"


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

pages: dict[str, np.ndarray] = {
    "latin": read("latin.png"),
    "cjk": read("cjk.png"),
    "two_columns": read("two_columns.png"),
    "tilt": read("latin_tilt.png"),
    "blank": read("blank.png"),
    "rot90": read("latin_rot90.png"),
    "rot180": np.ascontiguousarray(np.rot90(read("latin.png"), 2)),
    "rot270": np.ascontiguousarray(np.rot90(read("latin.png"), 1)),   # counter-clockwise 90
    "small": read("latin.png")[100:160, 200:290],          # 60x90 crop
    "strip": read("latin.png")[300:303, 100:340],          # 3 rows
    "onepx": read("latin.png")[400:401, 400:401],          # 1x1
}


def synth_page(w: int, h: int, tilt: float, seed: int) -> np.ndarray:
    """A page of horizontal ink bars: a strong row profile that lives at exactly -tilt."""
    img = np.full((h, w), 255, np.uint8)
    rng = np.random.default_rng(seed)
    for i in range(max(1, h // 16)):
        y = 6 + i * 15
        if y + 6 >= h:
            break
        x0 = int(rng.integers(0, max(1, w // 4)))
        x1 = x0 + int(rng.integers(max(1, w // 3), max(2, w - 20)))
        img[y : y + 6, x0 : min(x1, w)] = 0
    if tilt:
        m = cv2.getRotationMatrix2D((w / 2, h / 2), tilt, 1.0)
        img = cv2.warpAffine(img, m, (w, h), flags=cv2.INTER_LINEAR,
                             borderMode=cv2.BORDER_REPLICATE)
    return np.ascontiguousarray(img)


pages["synth"] = synth_page(300, 200, 0.0, 1)
pages["synth_tilt"] = synth_page(300, 200, 3.0, 2)          # tilted counter-clockwise by 3
pages["noise"] = np.ascontiguousarray(
    np.random.default_rng(7).integers(0, 256, (90, 120), dtype=np.uint8)
)

order = list(pages)


# --------------------------------------------------------------------------- orientation model

sess = None
if ORIENT_ONNX.exists():
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1          # matches OnnxSessions.CreateOptions(1) in the tests
    so.inter_op_num_threads = 1
    sess = ort.InferenceSession(str(ORIENT_ONNX), so, providers=["CPUExecutionProvider"])


def orientation(gray: np.ndarray) -> tuple[np.ndarray, int]:
    """pipeline.OrtOCR.orientation, spelled out (returns the probabilities too)."""
    p = sess.run(None, {"x": orient_input(gray)[None, None]})[0][0]
    k = int(p.argmax())
    return p, (k if p[k] > ORIENT_MIN else 0)


def standin_p(ink: np.ndarray) -> np.ndarray:
    """The deterministic stand-in for orient.onnx: mean ink of each quadrant."""
    h, w = ink.shape[0] // 2, ink.shape[1] // 2
    q = [ink[:h, :w], ink[:h, w:], ink[h:, :w], ink[h:, w:]]
    return np.array([np.float32(q[0].mean()), np.float32(q[1].mean()),
                     np.float32(q[2].mean()), np.float32(q[3].mean())], np.float32)


def standin_turns(ink: np.ndarray) -> tuple[np.ndarray, int]:
    p = standin_p(ink)
    k = int(p.argmax())
    return p, (k if p[k] > ORIENT_MIN else 0)


# --------------------------------------------------------------------------- estimate_skew

def thumbnail(gray: np.ndarray, side: int) -> np.ndarray:
    h, w = gray.shape
    s = side / max(h, w)
    return cv2.resize(gray, (max(1, round(w * s)), max(1, round(h * s))),
                      interpolation=cv2.INTER_AREA)


def ink_of(small: np.ndarray) -> np.ndarray:
    return (255.0 - small.astype(np.float32)) / 255.0


def sharpness(ink: np.ndarray, a: float) -> float:
    size = (ink.shape[1], ink.shape[0])
    r = cv2.warpAffine(ink, cv2.getRotationMatrix2D((size[0] / 2, size[1] / 2), a, 1.0), size)
    p = r.sum(1)
    return float(((p[1:] - p[:-1]) ** 2).sum())


sides = {
    "latin": [ORIENT_SIDE],       # a downscale (900 -> 768) through the area tables
    "cjk": [128],                 # a big downscale
    "blank": [ORIENT_SIDE],       # all zeros: the padded square keeps its padding
    "synth": [96, 300],           # up, and the exact identity scale
    "small": [ORIENT_SIDE, 96],   # a small page grown to 768 (upscale: INTER_LINEAR)
    "strip": [64],                # 1-row thumbnail
    "onepx": [64],                # 1x1 -> 64x64
}

orient_cases = []
for name, side_list in sides.items():
    gray = pages[name]
    for side in side_list:
        orient_cases.append({
            "name": f"{name}_{side}",
            "page": name,
            "side": int(side),
            "ink": f32(orient_input(gray, side)),
        })

turn_cases, standin_cases = [], []
for name in order:
    gray = pages[name]
    ink = orient_input(gray)
    if sess is not None:
        p, k = orientation(gray)
        turn_cases.append({"name": name, "page": name, "p": [float(v) for v in p],
                           "turns": int(k)})
    p, k = standin_turns(ink)
    standin_cases.append({"name": name, "page": name, "p": [float(v) for v in p],
                          "turns": int(k)})
    print(f"  turns {name}: {'real ' + str(turn_cases[-1]['turns']) if sess is not None else ''}"
          f" standin {k} p={np.round(p, 4).tolist()}")

skew_cases = []
for name, side, want_thumb in [
    ("latin", 800, False),
    ("tilt", 400, False),
    ("two_columns", 400, False),
    ("cjk", 160, True),
    ("blank", 160, True),
    ("synth", 200, False),
    ("synth_tilt", 200, False),
    ("noise", 160, True),
    ("small", 100, True),
    ("strip", 100, True),
    ("onepx", 64, True),
    ("rot90", 200, False),
]:
    gray = pages[name]
    small = thumbnail(gray, side)
    ink = ink_of(small)
    mean = float(ink.mean())
    angle = estimate_skew(gray, side)

    def profile(angles):
        return [[float(a), sharpness(ink, float(a))] for a in angles]

    coarse = np.arange(-DESKEW_MAX, DESKEW_MAX + 1e-6, 0.5)
    coarse_p = profile(coarse)
    best = max(coarse_p, key=lambda t: t[1])[0]
    fine = np.arange(best - 0.5, best + 0.5 + 1e-6, 0.1)
    fine_p = profile(fine)
    # estimate_skew short-circuits on a blank page: no rows to straighten.
    if not mean < 0.003:
        assert angle == max(fine_p, key=lambda t: t[1])[0], (name, angle)

    case = {
        "name": f"{name}_{side}",
        "page": name,
        "side": int(side),
        "thumbnail": {"h": int(small.shape[0]), "w": int(small.shape[1])},
        "mean_ink": mean,
        "blank": bool(mean < 0.003),
        "angle": float(angle),
        "coarse": coarse_p,
        "fine": fine_p,
    }
    if want_thumb:
        case["ink"] = f32(ink)
    skew_cases.append(case)
    print(f"  skew {case['name']}: {small.shape} mean={mean:.6f} angle={angle:+.1f} "
          f"coarse best {best:+.1f} fine {len(fine)} candidates")

# --------------------------------------------------------------------------- rotate_page

rot_cases = []
for name, degrees in [
    ("latin", 0.0), ("latin", 0.3), ("latin", -0.3), ("latin", 2.5), ("latin", -4.9),
    ("latin", 15.0), ("latin", 45.0), ("latin", 90.0), ("latin", 179.9), ("latin", -37.5),
    ("synth", 1.7), ("small", -2.2), ("strip", 12.0), ("onepx", 33.0), ("blank", 5.0),
    ("rot90", -90.0),
]:
    out = rotate_page(pages[name], degrees)
    assert out.shape == pages[name].shape
    rot_cases.append({"name": f"{name}_{degrees:+.1f}", "page": name, "degrees": float(degrees),
                      "out": z(out)})

# --------------------------------------------------------------------------- unrotate_box

box_cases = []
rng = np.random.default_rng(3)
for name in ["latin", "synth", "small", "strip", "onepx"]:
    h, w = pages[name].shape
    boxes = [
        [10.0, 20.0, 100.0, 50.0],
        [0.0, 0.0, float(w), float(h)],
        [-20.0, -30.0, 12.0, 8.0],
        [w - 3.0, h - 4.0, w + 40.0, h + 30.0],
        [4.0, 4.0, 4.0, 4.0],
        [30.0, 12.0, 10.0, 40.0],                       # x1 < x0, y1 > y0
    ]
    for _ in range(6):
        x0, x1 = sorted(rng.uniform(-20, w + 20, 2))
        y0, y1 = sorted(rng.uniform(-20, h + 20, 2))
        boxes.append([float(x0), float(y0), float(x1), float(y1)])
    for degrees in [0.3, -0.3, 2.5, -4.9, 15.0, 45.0, 90.0, -37.5]:
        for box in boxes:
            out = unrotate_box(np.array(box, np.float32), degrees, (w, h))
            box_cases.append({"name": f"{name}_{degrees:+.1f}_{box[0]:.1f}", "page": name,
                              "box": [float(v) for v in box], "degrees": float(degrees),
                              "w": int(w), "h": int(h), "out": [float(v) for v in out]})
print(f"  unrotate_box: {len(box_cases)} boxes")

# --------------------------------------------------------------------------- round(x, 1)

round_values = [0.05, 0.15, 0.25, 0.35, 0.45, 1.25, 2.675, 1.005, 3.14159, 99.95, 0.0, -0.0,
                -0.05, -0.15, -1.25, -2.675, -99.95, 0.949999988079071, 123.456, 7.0]
round_cases = [{"v": float(np.float32(v)), "out": float(round(float(np.float32(v)), 1))}
               for v in round_values]
for c in round_cases:
    print(f"  round({c['v']!r}, 1) = {c['out']!r}")

# --------------------------------------------------------------------------- upright

upright_cases = []
for name, use_model in [("latin", "real"), ("latin", None), ("tilt", "real"),
                        ("synth_tilt", "real"), ("rot90", "real"), ("rot90", None),
                        ("rot180", "real"), ("rot270", "real"), ("blank", "real"),
                        ("small", "real"), ("strip", "standin"), ("onepx", "standin"),
                        ("noise", "standin"), ("synth", "standin")]:
    gray = pages[name]
    k = 0
    if use_model == "real" and sess is not None:
        k = orientation(gray)[1]
    elif use_model == "standin":
        k = standin_turns(orient_input(gray))[1]
    if k:
        gray = np.ascontiguousarray(np.rot90(gray, -k))
    # The deskew *search* is covered by the skew cases above; the composition only needs enough
    # of it to exercise the two rotations, so most cases run on a small side.
    deskew_side = 800 if name in ("latin", "rot180") else 160
    a = estimate_skew(gray, deskew_side)
    if abs(a) >= DESKEW_MIN:
        gray = rotate_page(gray, a)
    else:
        a = 0.0
    upright_cases.append({
        "name": f"{name}_{use_model}",
        "page": name,
        "model": use_model or "none",
        "undone": int(k % 4) if k else 0,          # quarter turns upright undid
        "skew": float(a),
        "side": int(deskew_side),
        "h": int(gray.shape[0]),
        "w": int(gray.shape[1]),
        "out": z(gray),
    })
    print(f"  upright {name} ({use_model}): undone={k} skew={a:+.1f} side={deskew_side} "
          f"-> {gray.shape}")

doc = {
    "note": "fixtures for the orient/deskew half of indra_ocr.pipeline (orient_input, "
            "orientation, estimate_skew, rotate_page, unrotate_box, upright); arrays are "
            "zlib+base64 of raw little-endian bytes "
            "(see tools/scripts/test/gen_orient_fixtures.py)",
    "orient_min": float(ORIENT_MIN),
    "deskew_min": float(DESKEW_MIN),
    "pages": {n: {"h": int(pages[n].shape[0]), "w": int(pages[n].shape[1]), "gray": z(pages[n])}
              for n in order},
    "orient_input": orient_cases,
    "turns": turn_cases,
    "turns_standin": standin_cases,
    "skew": skew_cases,
    "rotate_page": rot_cases,
    "unrotate_box": box_cases,
    "round": round_cases,
    "upright": upright_cases,
}
path = fixtures_dir() / "orient.json"
path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {path.name}: {path.stat().st_size} bytes "
      f"({len(pages)} pages, {len(orient_cases)} orient_input, {len(turn_cases)} turns, "
      f"{len(standin_cases)} standin, {len(skew_cases)} skew, {len(rot_cases)} rotate_page, "
      f"{len(box_cases)} unrotate_box, {len(upright_cases)} upright)")
