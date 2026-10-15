"""Generate src/Indra.Ocr.Tests/fixtures/rec_preproc.json.

Parity fixtures for ``indra_ocr.rec_preproc`` — the line crop rule (``crop_line``) and the
batching helpers ``pad_batch`` / ``width_batches`` — see docs/080-rec-preproc.md.

``crop_line`` is checked against the real module on real line boxes: the small pages below are
crops of ``temp/inputs/*.png`` (run ``python tools/scripts/gen_inputs.py`` first), and the boxes
come from the real ``det.onnx``, so both the geometry and the resize path are the ones the
pipeline actually takes.  Artificial boxes then cover what a page edge does (partially/fully
off-page, degenerate heights, the alignment of ``floor``/``ceil``, the 8-multiple rounding and
the ``max_width`` clamp).

Arrays are stored zlib+base64 of the raw little-endian bytes (see ``B64.ZBytes`` / ``ZFloats``);
each page appears once in ``pages`` and the crop cases reference it by name.
``pad_batch`` records only its widths and the batched width, since the placement is checked
directly in the test.
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
from indra_ocr.rec_preproc import LINE_HEIGHT, PAD_X, PAD_Y, crop_line, pad_batch, width_batches  # noqa: E402

DET_ONNX = ROOT / "resources" / "models" / "det.onnx"
INPUTS = ROOT / "temp" / "inputs"


def z(a) -> str:
    a = np.ascontiguousarray(a)
    return base64.b64encode(zlib.compress(a.tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


# --------------------------------------------------------------------------- pages


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
order = []
for name, size in [("latin.png", (96, 256)), ("cjk.png", (80, 200)), ("two_columns.png", (128, 256))]:
    path = INPUTS / name
    if not path.exists():
        raise SystemExit(f"missing {path}; run: python tools/scripts/gen_inputs.py")
    gray = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
    tag = f"page_{Path(name).stem}_{size[1]}x{size[0]}"
    pages[tag] = best_crop(gray, *size)
    order.append(tag)

# A page whose ink touches all four edges, plus a blank one.
small = np.full((40, 60), 255, np.uint8)
small[0:6, 0:60] = 0
small[34:40, 0:60] = 0
small[:, 0:4] = 0
small[:, 56:60] = 0
pages["page_edge_ink_60x40"] = small
pages["page_blank_64x64"] = np.full((64, 64), 255, np.uint8)
order += ["page_edge_ink_60x40", "page_blank_64x64"]

# --------------------------------------------------------------------------- crop_line

sess = ort.InferenceSession(str(DET_ONNX), providers=["CPUExecutionProvider"])
crop_cases = []


def add_crop(tag, page_name, box, height=LINE_HEIGHT, pad_x=PAD_X, pad_y=PAD_Y, max_width=4096):
    page = pages[page_name]
    ink = crop_line(page, box, height=height, pad_x=pad_x, pad_y=pad_y, max_width=max_width)
    assert ink.dtype == np.float32 and ink.shape[0] == height
    crop_cases.append(
        {
            "name": tag,
            "page": page_name,
            "box": [float(v) for v in box],
            "height": int(height),
            "pad_x": float(pad_x),
            "pad_y": float(pad_y),
            "max_width": int(max_width),
            "out_w": int(ink.shape[1]),
            "ink": f32(ink),
        }
    )


for tag in order:
    page = pages[tag]
    h, w = page.shape
    x = page_to_input(page, 256)[0]
    out = sess.run(None, {"x": np.ascontiguousarray(x)[None, None]})[0][0]
    boxes = decode(out)
    # The real detector boxes, scaled back to page pixels the way detect() does.
    boxes = boxes.copy()
    if len(boxes):
        boxes[:, :4] /= 256 / max(h, w)
        for k, b in enumerate(boxes[:4]):
            add_crop(f"{tag}_det{k}", tag, b)

    # Artificial boxes: off-page on every side, degenerate, tie-prone and wide.
    add_crop(f"{tag}_off_top", tag, [-5.0, -12.0, w + 5.0, 4.0])
    add_crop(f"{tag}_off_bottom", tag, [3.0, h - 3.0, w - 1.0, h + 17.0])
    add_crop(f"{tag}_off_both", tag, [-w, -h, 2 * w, 2 * h])
    add_crop(f"{tag}_outside", tag, [w + 10.0, h + 10.0, w + 40.0, h + 40.0])
    add_crop(f"{tag}_flat", tag, [10.0, 20.0, 40.0, 20.0], max_width=96)   # h -> 1.0
    add_crop(f"{tag}_inverted", tag, [40.0, 30.0, 10.0, 10.0])             # x1 < x0, y1 < y0
    add_crop(f"{tag}_tiny", tag, [4.0, 4.0, 4.5, 4.25])
    add_crop(f"{tag}_wide", tag, [-2000.0, 8.0, 2000.0, 24.0], max_width=128)
    add_crop(f"{tag}_nopad", tag, [6.0, 6.0, w - 6.0, h - 6.0], pad_x=0.0, pad_y=0.0)
    add_crop(f"{tag}_half", tag, [2.0, 18.5, w - 2.0, 21.5], height=16)
    add_crop(f"{tag}_tall", tag, [2.0, 2.0, w - 2.0, h - 2.0], height=64)
    print(f"  {tag}: {len(boxes)} detected boxes, {len(crop_cases)} crop cases so far")

# Boxes whose padding lands exactly on integer/ties for floor/ceil and for the width rounding.
for tag in order[:2]:
    h, w = pages[tag].shape
    add_crop(f"{tag}_align", tag, [1.0, 2.0, 30.0, 14.0])                    # h = 12 -> pad_y*h = 2.4
    add_crop(f"{tag}_align2", tag, [1.0, 2.0, 30.0, 12.0])                   # h = 10 -> exactly 2.0
    add_crop(f"{tag}_ratio_tie", tag, [0.0, 0.0, 31.0, 8.0], height=33)      # cw*33/ch rounds .5

# --------------------------------------------------------------------------- pad_batch
# The widths and the batched width are recorded; the test re-runs pad_batch on synthetic lines
# of the same widths and checks the values and the placement.

pad_cases = []
for tag, widths in [
    ("pad_exact_multiple", [8, 16, 24]),
    ("pad_rounds_up", [7, 8, 15, 33]),
    ("pad_single", [41]),
    ("pad_zero", [0, 5]),          # a crop_line result is never 0 wide, but pad_batch allows it
]:
    lines = [np.full((LINE_HEIGHT, wd), 0.5, np.float32) for wd in widths]
    batch, out_widths = pad_batch(lines)
    assert list(out_widths) == widths
    pad_cases.append(
        {
            "name": tag,
            "widths": [int(v) for v in widths],
            "height": LINE_HEIGHT,
            "multiple": 8,
            "expected_width": int(batch.shape[-1]),
            "expected_widths": [int(v) for v in out_widths],
        }
    )
    print(f"  {tag}: widths={widths} -> batch width {batch.shape[-1]}")

# --------------------------------------------------------------------------- width_batches

wb_cases = []
for tag, widths, kwargs in [
    ("wb_empty", [], {}),
    ("wb_one", [120], {}),
    ("wb_identical", [100] * 5, {}),                       # the sort has to stay stable
    ("wb_ascending", [64, 70, 80, 100, 130, 300], {}),
    ("wb_random", [70, 300, 120, 64, 65, 1000, 200, 900, 64, 66], {}),
    ("wb_max_batch", list(range(64, 64 + 30)), {"max_batch": 4}),
    ("wb_ratio", [64, 80, 120, 200, 400], {"max_ratio": 1.5}),
    ("wb_min_width", [1, 2, 3, 200], {"min_width": 100}),
    ("wb_max_area", [64] * 100, {"max_area": 5 * 64}),
    ("wb_tight", [64, 65, 66, 67, 68], {"max_ratio": 1.02, "max_batch": 2, "max_area": 200}),
    ("wb_duplicates", [90, 90, 90, 300, 91, 90, 92], {}),   # ties keep their input order
]:
    batches = width_batches(list(widths), **kwargs)
    assert sum(len(b) for b in batches) == len(widths)
    assert sorted(i for b in batches for i in b) == list(range(len(widths)))
    wb_cases.append(
        {
            "name": tag,
            "widths": [int(v) for v in widths],
            "max_batch": int(kwargs.get("max_batch", 64)),
            "max_ratio": float(kwargs.get("max_ratio", 1.3)),
            "min_width": int(kwargs.get("min_width", 64)),
            "max_area": int(kwargs.get("max_area", 64 * 640)),
            "expected": [[int(i) for i in b] for b in batches],
        }
    )
    print(f"  {tag}: {len(widths)} lines -> {len(batches)} batches")

doc = {
    "note": "fixtures for indra_ocr.rec_preproc (crop_line, pad_batch, width_batches); "
            "arrays are zlib+base64 of raw little-endian bytes "
            "(see tools/scripts/test/gen_recpreproc_fixtures.py)",
    "pages": {t: {"h": int(pages[t].shape[0]), "w": int(pages[t].shape[1]), "gray": z(pages[t])}
              for t in order},
    "crop": crop_cases,
    "pad": pad_cases,
    "width_batches": wb_cases,
}
path = fixtures_dir() / "rec_preproc.json"
path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {path.name}: {path.stat().st_size} bytes "
      f"({len(pages)} pages, {len(crop_cases)} crops, {len(pad_cases)} pad, {len(wb_cases)} batching)")
