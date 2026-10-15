"""Generate src/Indra.Ocr.Tests/fixtures/order_post.json and unicode_predicates.json.

Parity fixtures for ``indra_ocr.order_post`` (step 110): the line features, the pairwise-order
decoder, the two geometry baselines, the average-linkage block clustering and the layout-head
block decoder — see docs/110-order-post.md.

Every expectation is the reference's own output (``order_post`` itself), driven with

* real pages: the boxes and texts are the *real* detector/recognizer output recorded in
  ``recognize.json`` (so this generator needs no image inputs), and the order model's
  ``a`` / ``cls`` / ``same`` logits come from the real ``order.onnx``;
* synthetic layouts for the branches the real pages do not reach (single boxes, exact ties,
  overlapping boxes without a gap, degenerate sizes, every text-feature marker, ...).

The generator also replays each ``decode_order`` / ``decode_blocks_order`` case with a *portable*
simulation of the C# port (the CRT's float ``expf`` via ctypes + correctly rounded ``log1p`` +
numpy's portable pairwise sum + a stable argsort) and reports where the two disagree.  That is the
measurement behind the documented ``lp`` deviation: numpy's float32 ``logaddexp`` uses the CRT's
``log1pf``, which is not correctly rounded.
"""

from __future__ import annotations

import base64
import ctypes
import json
import math
import sys
import unicodedata
import zlib
from pathlib import Path

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import fixtures_dir  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "resources" / "indra-src"))
from indra_ocr.order_post import (  # noqa: E402
    N_FEATS,
    _cluster,
    decode_blocks_order,
    decode_order,
    line_features,
    order_top_down,
    order_xycut,
)

MODELS = ROOT / "resources" / "models"
FIX = fixtures_dir()
f32_ = np.float32


def z(a) -> str:
    return base64.b64encode(zlib.compress(np.ascontiguousarray(a).tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


def session(name: str):
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1
    so.inter_op_num_threads = 1
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.enable_cpu_mem_arena = False
    return ort.InferenceSession(str(MODELS / name), so, providers=["CPUExecutionProvider"])


# --------------------------------------------------------------------------- portable simulation

_u = ctypes.CDLL("ucrtbase.dll")
_u.expf.argtypes = [ctypes.c_float]
_u.expf.restype = ctypes.c_float


def crt_expf(x: float) -> float:
    """The CRT's float ``expf`` (what numpy's float32 ``logaddexp`` calls)."""
    return float(_u.expf(f32_(x)))


def pairwise(a) -> np.float32:
    """numpy's portable ``pairwise_sum_FLOAT`` (the port's ``Numerics.Sum32``)."""
    a = np.asarray(a, np.float32)
    n = len(a)
    if n < 8:
        s = f32_(0)
        for x in a:
            s = f32_(s + x)
        return s
    if n <= 128:
        r = [f32_(a[i]) for i in range(8)]
        i = 8
        limit = n - n % 8
        while i < limit:
            for k in range(8):
                r[k] = f32_(r[k] + a[i + k])
            i += 8
        res = f32_(f32_(f32_(r[0] + r[1]) + f32_(r[2] + r[3]))
                   + f32_(f32_(r[4] + r[5]) + f32_(r[6] + r[7])))
        while i < n:
            res = f32_(res + a[i])
            i += 1
        return res
    h = n // 2
    return f32_(pairwise(a[:h]) + pairwise(a[h:]))


LN2F = f32_(0.6931471805599453)


def portable_lp(a):
    """The port's ``LogSigmoid``: CRT ``expf`` (= MathF.Exp) + correctly rounded ``log1pf``."""
    src = np.asarray(a, np.float32).reshape(-1)
    out = np.empty(len(src), np.float32)
    for i, v in enumerate(src):
        v = f32_(v)
        if v == 0:
            out[i] = f32_(-LN2F)
        elif v > 0:
            out[i] = f32_(-(f32_(0.0) + f32_(math.log1p(crt_expf(-float(v))))))
        else:
            out[i] = f32_(-(f32_(-v) + f32_(math.log1p(crt_expf(float(v))))))
    return out.reshape(np.shape(a))


def portable_decode_order(a, sweeps: int = 4) -> list:
    a = np.asarray(a, np.float32)
    n = a.shape[0]
    if n <= 1:
        return list(range(n))
    lp = portable_lp(a)
    row = np.array([pairwise(lp[i]) for i in range(n)], np.float32)
    col = np.array([pairwise(lp[:, j]) for j in range(n)], np.float32)
    keys = -row + col
    order = np.array(sorted(range(n), key=lambda i: (float(keys[i]), i)), np.int64)
    for _ in range(sweeps):
        improved = False
        for x in order.copy():
            rest = order[order != x]
            gains = np.concatenate(([0.0], np.cumsum(lp[rest, x]))) + np.concatenate(
                (np.cumsum(lp[x, rest][::-1])[::-1], [0.0]))
            k = int(np.argmax(gains))
            cur = int(np.nonzero(order == x)[0][0])
            if gains[k] > gains[cur] + 1e-6:
                order = np.insert(rest, k, x)
                improved = True
        if not improved:
            break
    return [int(v) for v in order]


def portable_top_down(boxes) -> list:
    b = np.asarray(boxes, np.float32)
    n = len(b)
    cy = (b[:, 1] + b[:, 3]) / 2
    h = float(np.median(b[:, 3] - b[:, 1]))
    half = max(h * 0.5, 1e-3)
    return sorted(range(n), key=lambda i: (int(round(f32_(cy[i] / f32_(half)))), float(b[i, 0]), i))


def portable_xycut(boxes) -> list:
    b = np.asarray(boxes, np.float32)

    def best_cut(sub, axis):
        lo = sub[:, 1] if axis else sub[:, 0]
        hi = sub[:, 3] if axis else sub[:, 2]
        o = sorted(range(len(sub)), key=lambda i: (float(lo[i]), i))
        end = hi[o[0]]
        gap, kk = 0.0, -1
        for k in range(1, len(sub)):
            g = f32_(lo[o[k]] - f32_(end))
            if g > gap:
                gap, kk = float(g), k
            end = f32_(max(end, hi[o[k]]))
        return gap, kk, o

    def rec(ids):
        if len(ids) <= 1:
            return list(ids)
        sub = b[ids]
        gy, ky, oy = best_cut(sub, 1)
        gx, kx, ox = best_cut(sub, 0)
        if ky < 0 and kx < 0:
            return [ids[i] for i in portable_top_down(sub)]
        if gy >= gx:
            k, o = ky, oy
        else:
            k, o = kx, ox
        return rec([ids[i] for i in o[:k]]) + rec([ids[i] for i in o[k:]])

    return rec(list(range(len(b))))


def portable_blocks(a, same, boxes, cls) -> list:
    a = np.asarray(a, np.float32)
    same = np.asarray(same, np.float32)
    boxes = np.asarray(boxes, np.float32)
    cls = np.asarray(cls, np.float32)
    n = a.shape[0]
    if n <= 1:
        return list(range(n))
    blocks = _cluster(same)
    inner = []
    for blk in blocks:
        bx = boxes[blk]
        local = portable_top_down(bx) if int(cls[blk].sum(0).argmax()) in (5, 9, 8) \
            else portable_xycut(bx)
        inner.append([int(blk[k]) for k in local])
    m = len(blocks)
    mean = np.empty((m, m), np.float64)
    for k in range(m):
        for l in range(m):
            acc = 0.0
            for i in blocks[k]:
                for j in blocks[l]:
                    acc += float(a[i, j])
            mean[k, l] = acc / (len(blocks[k]) * len(blocks[l]))
    ab = np.zeros((m, m), np.float32)
    for k in range(m):
        for l in range(m):
            if k < l:
                ab[k, l] = f32_(mean[k, l])
            elif k > l:
                ab[k, l] = f32_(-mean[l, k])
    order = portable_decode_order(ab)
    return [i for k in order for i in inner[k]]


# --------------------------------------------------------------------------- cases

features: list = []
decode_orders: list = []
top_downs: list = []
xy_cuts: list = []
clusters: list = []
block_cases: list = []
reading_orders: list = []
stats = {"decode_order": 0, "decode_order_differ": 0, "blocks": 0, "blocks_differ": 0,
         "worst_blocks": 0}
warning = False


def feature_case(name, boxes, wh, texts, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    expected = line_features(b, wh, texts)
    fields = {"name": name, "page": [float(wh[0]), float(wh[1])], "n": int(len(b)),
              "boxes": f32(b), "expected": f32(expected), "note": note}
    if texts is not None:
        fields["texts"] = list(texts)
    features.append(fields)
    return expected


def decode_case(name, a, note=""):
    global warning
    a = np.ascontiguousarray(a, np.float32)
    expected = [int(v) for v in decode_order(a)]
    mine = portable_decode_order(a)
    stats["decode_order"] += 1
    if mine != expected:
        stats["decode_order_differ"] += 1
        warning = True
    decode_orders.append({"name": name, "n": int(a.shape[0]), "a": f32(a),
                          "expected": expected, "note": note})
    return expected


def top_down_case(name, boxes):
    b = np.ascontiguousarray(boxes, np.float32)
    top_downs.append({"name": name, "n": int(len(b)), "boxes": f32(b),
                      "expected": [int(v) for v in order_top_down(b)]})


def xy_cut_case(name, boxes):
    b = np.ascontiguousarray(boxes, np.float32)
    xy_cuts.append({"name": name, "n": int(len(b)), "boxes": f32(b),
                    "expected": [int(v) for v in order_xycut(b)]})


def cluster_case(name, same):
    s = np.ascontiguousarray(same, np.float32)
    clusters.append({"name": name, "n": int(s.shape[0]), "same": f32(s),
                     "expected": [[int(v) for v in blk] for blk in _cluster(s)]})


def block_case(name, a, same, boxes, cls, note=""):
    global warning
    a = np.ascontiguousarray(a, np.float32)
    same = np.ascontiguousarray(same, np.float32)
    boxes = np.ascontiguousarray(boxes, np.float32)
    cls = np.ascontiguousarray(cls, np.float32)
    expected = [int(v) for v in decode_blocks_order(a, same, boxes, cls)]
    mine = portable_blocks(a, same, boxes, cls)
    stats["blocks"] += 1
    if mine != expected:
        stats["blocks_differ"] += 1
        warning = True
        first = next((i for i in range(len(mine)) if mine[:i] != expected[:i]), 0)
        stats["worst_blocks"] = max(stats["worst_blocks"], 1)
        print(f"    ! {name}: portable simulation differs (first at {first})")
    block_cases.append({"name": name, "n": int(a.shape[0]), "a": f32(a), "same": f32(same),
                        "boxes": f32(boxes), "cls": f32(cls), "expected": expected, "note": note})
    return expected


# --------------------------------------------------------------------------- reading_order


class OrderStub:
    """Only the attributes ``OrtOCR.reading_order`` touches (step 115)."""

    def __init__(self, order, table_heads: bool = False):
        self.order = order
        self.table_heads = table_heads


def ro_case(name, boxes, texts, wh, order_sess_, model=True, table_heads=False, note=""):
    """Runs the *real* ``OrtOCR.reading_order`` against a stub carrying just the model."""
    b = np.ascontiguousarray(boxes, np.float32)
    stub = OrderStub(order_sess_ if model else None, table_heads)
    order, layout, logits = OrtOCR.reading_order(stub, b, list(texts), (int(wh[0]), int(wh[1])))
    fields = {"name": name, "n": int(len(b)), "boxes": f32(b), "texts": list(texts),
              "page": [int(wh[0]), int(wh[1])], "model": bool(model),
              "table_heads": bool(table_heads),
              "order": [int(v) for v in order], "cls": None, "same": None, "tab": None,
              "logits": None, "note": note}
    if layout is not None:
        fields["cls"], fields["same"] = f32(layout[0]), f32(layout[1])
        if layout[2] is not None:
            fields["tab"] = f32(layout[2])
    if logits is not None:
        fields["logits"] = f32(logits)
    reading_orders.append(fields)
    return fields


# --------------------------------------------------------------------------- real pages

order_sess = None
if (MODELS / "order.onnx").exists() and (FIX / "recognize.json").exists():
    from indra_ocr.pipeline import OrtOCR

    order_sess = session("order.onnx")
    rec_doc = json.loads((FIX / "recognize.json").read_text(encoding="utf-8"))
    page_wh = {n: (float(p["w"]), float(p["h"])) for n, p in rec_doc["pages"].items()}
    for case in rec_doc["cases"]:
        name = case["name"]
        if name == "handmade":
            continue                       # off-page boxes: kept for the synthetic section
        raw = np.frombuffer(zlib.decompress(base64.b64decode(case["boxes"])),
                            np.float32).reshape(-1, 5)
        boxes = np.ascontiguousarray(raw[:, :4], np.float32)
        texts = [ln["text"] for ln in case["lines"]]
        wh = page_wh[case["page"]]
        n = len(boxes)
        print(f"  {name:14s} n={n:3d}")
        ro_case(name, boxes, texts, wh, order_sess)
        ro_case(name + "_nomodel", boxes, texts, wh, order_sess, model=False)
        if n == 0:
            feature_case(name + "_empty", boxes, wh, texts, "no lines")
            continue
        f = feature_case(name, boxes, wh, texts, "real detector + recognizer")
        if n > 1:
            feature_case(name + "_notext", boxes, wh, None, "texts=None")
        outs = order_sess.run(None, {"feats": f[None], "mask": np.ones((1, n), bool)})
        a, cls, same = outs[0][0], outs[1][0], outs[2][0]
        if n > 1:
            decode_case(name, a, "raw order logits (no layout heads)")
            top_down_case(name, boxes)
            xy_cut_case(name, boxes)
            cluster_case(name, same)
            block_case(name, a, same, boxes, cls, "real layout heads")
            block_case(name + "_noheads", a, same, boxes, np.zeros((n, 10), np.float32),
                       "cls all Text: every block by XY-cut")
            grid = np.zeros((n, 10), np.float32)
            grid[:, 5] = 1.0
            block_case(name + "_grid", a, same, boxes, grid, "every block a Table (row by row)")
    # the table-heads gate: only then is outs[3] handed on as layout[2]
    for name in ("latin", "blank"):
        case = next(c for c in rec_doc["cases"] if c["name"] == name)
        raw = np.frombuffer(zlib.decompress(base64.b64decode(case["boxes"])),
                            np.float32).reshape(-1, 5)
        boxes = np.ascontiguousarray(raw[:, :4], np.float32)
        ro_case(name + "_table", boxes, [ln["text"] for ln in case["lines"]],
                page_wh[case["page"]], order_sess, table_heads=True,
                note="table_heads on: layout[2] is the model's tab")
    # the hand-made off-page / degenerate boxes
    case = next(c for c in rec_doc["cases"] if c["name"] == "handmade")
    raw = np.frombuffer(zlib.decompress(base64.b64decode(case["boxes"])), np.float32).reshape(-1, 5)
    ro_case("handmade", np.ascontiguousarray(raw[:, :4], np.float32),
            [ln["text"] for ln in case["lines"]], page_wh[case["page"]], order_sess,
            note="off-page and degenerate boxes")
else:
    print("  order.onnx / recognize.json missing: real cases skipped")


# synthetic reading_order cases: the fallbacks (fewer than two lines, or no model at all) and a
# two-line page that actually runs the model
one = np.array([[100.0, 100.0, 400.0, 130.0]], np.float32)
two = np.array([[100.0, 100.0, 400.0, 130.0], [100.0, 200.0, 400.0, 230.0]], np.float32)
empty_boxes = np.zeros((0, 4), np.float32)
present = order_sess is not None
ro_case("single_line", one, ["only line"], (700, 900), order_sess, model=present,
        note="N=1: XY-cut, the model is never called")
ro_case("empty", empty_boxes, [], (700, 900), order_sess, model=present, note="N=0")
ro_case("two_lines_nomodel", two, ["first", "second"], (700, 900), order_sess, model=False,
        note="N=2 without an order model: XY-cut")
if present:
    ro_case("two_lines", two, ["first", "second"], (700, 900), order_sess,
            note="N=2: the model runs")
    ro_case("two_lines_table", two, ["first", "second"], (700, 900), order_sess,
            table_heads=True, note="N=2 with the table heads")



# --------------------------------------------------------------------------- synthetic features

page = (700.0, 900.0)
synthetic_boxes = np.array(
    [
        [100.0, 100.0, 640.0, 130.0],        # normal line
        [100.0, 200.0, 100.0, 200.0],        # zero size: w and h clamp to 1e-3
        [0.0, 0.0, 700.0, 900.0],            # the whole page
        [-40.0, 400.0, 300.0, 440.0],        # starts left of the page
        [900.0, 500.0, 1000.0, 540.0],       # entirely right of the page
        [100.0, 860.0, 640.0, 899.0],        # bottom edge
        [10.0, 10.0, 690.0, 12.0],           # 2 px high: very flat
        [300.0, 300.0, 310.0, 420.0],        # tall column
    ],
    np.float32,
)
texts = [
    "The quick brown fox",
    "   ",
    "",
    "bullet item",
    "- dash item",
    "→ arrow item",
    "• middle dot",
    "1. numbered",
    "12) second numbered",
    "2023",
    "1.",
    "I. roman",
    "Item-",
    "Sentence.",
    "Question?",
    "Colon:",
    "Semi;",
    "UPPER CASE",
    "lower case",
    "Mixed Case",
    "Ⅷ roman numeral eight",
    "ⅷ small roman numeral eight",
    "١٢٣ arabic-indic",
    "一二三 hanzi",
    "① circled one",
    "² superscript two",
    "½ one half",
    "𝕬𝕭 astral uppercase",
    "😀 astral smiley",
    "  padded  ",
    "𝕬",
]
feature_case("synthetic_texts", synthetic_boxes, page, texts[:len(synthetic_boxes)],
             "every text-feature marker")
feature_case("synthetic_short_texts", synthetic_boxes, page, texts[:3],
             "texts shorter than the box list")
feature_case("synthetic_notext", synthetic_boxes, page, None)
feature_case("single_box", synthetic_boxes[:1], page, ["only line"], "N=1")
feature_case("no_boxes", synthetic_boxes[:0], page, [], "N=0")
feature_case("long_text", np.array([[0.0, 0.0, 100.0, 20.0]], np.float32), page,
             ["x" * 400 + " 1"], "log1p of a long text")
feature_case("tiny_page", np.array([[0.0, 0.0, 1.0, 1.0]], np.float32), (1.0, 1.0),
             ["a"], "1x1 page")


# --------------------------------------------------------------------------- synthetic layouts


def col2() -> np.ndarray:
    left = [[60.0, 100.0 + i * 50, 280.0, 140.0 + i * 50] for i in range(6)]
    right = [[360.0, 100.0 + i * 50, 620.0, 140.0 + i * 50] for i in range(6)]
    return np.array(left + right, np.float32)


def grid3() -> np.ndarray:
    out = []
    for r in range(3):
        for c in range(3):
            out.append([60.0 + c * 200, 80.0 + r * 120, 220.0 + c * 200, 140.0 + r * 120])
    return np.array(out, np.float32)


def jitter_rows() -> np.ndarray:
    out = []
    rng = np.random.default_rng(110)
    for r in range(4):
        for c in range(3):
            y = 100.0 + r * 60 + float(rng.uniform(-8, 8))
            out.append([50.0 + c * 180, y, 200.0 + c * 180, y + 34.0])
    return np.array(out, np.float32)


def uneven_rows() -> np.ndarray:
    return np.array(
        [
            [40.0, 40.0, 400.0, 90.0],
            [40.0, 95.0, 400.0, 120.0],
            [40.0, 125.0, 400.0, 210.0],
            [40.0, 215.0, 400.0, 240.0],
            [40.0, 400.0, 400.0, 440.0],
        ],
        np.float32,
    )


def overlapping() -> np.ndarray:
    return np.array(
        [
            [0.0, 0.0, 100.0, 100.0],
            [20.0, 20.0, 120.0, 120.0],
            [40.0, 40.0, 140.0, 140.0],
        ],
        np.float32,
    )


tied = np.array(
    [
        [100.0, 100.0, 200.0, 140.0],
        [300.0, 100.0, 400.0, 140.0],        # same row as the first
        [50.0, 100.0, 90.0, 140.0],          # same row, leftmost
        [100.0, 200.0, 200.0, 240.0],
    ],
    np.float32,
)

layouts = {"two_columns": col2(), "grid": grid3(), "jitter": jitter_rows(),
           "uneven": uneven_rows(), "overlap": overlapping(), "tied": tied,
           "single": synthetic_boxes[:1], "empty": synthetic_boxes[:0]}
for lname, lb in layouts.items():
    top_down_case(lname, lb)

xy_layouts = {k: v for k, v in layouts.items() if k != "tied"}   # tied: argsort tie order
for lname, lb in xy_layouts.items():
    xy_cut_case(lname, lb)

# a dense page: many blocks, exercises the block count without a model
rng = np.random.default_rng(7)
dense = np.array([[rng.uniform(0, 600), rng.uniform(0, 800),
                   rng.uniform(0, 600) + 100, rng.uniform(0, 800) + 30] for _ in range(60)],
                 np.float32)
dense[:, [0, 1]] = np.stack([dense[:, 0], dense[:, 1]], 1)
dense[:, 2] = dense[:, 0] + 150.0
dense[:, 3] = dense[:, 1] + 25.0
dense = np.ascontiguousarray(dense, np.float32)
top_down_case("dense", dense)
xy_cut_case("dense", dense)
decode_case("random_60", rng.normal(0, 3, (60, 60)).astype(np.float32), "random logits")


# --------------------------------------------------------------------------- synthetic order

for n in (1, 2, 3, 4, 5, 8, 13, 16, 32, 64, 100, 127, 128):
    a = rng.normal(0, 3, (n, n)).astype(np.float32)
    decode_case(f"random_{n}", a, f"random logits, n={n}")

# antisymmetric like decode_blocks_order builds them
for n in (2, 3, 5, 10, 30, 80):
    base = rng.normal(0, 2, (n, n)).astype(np.float32)
    decode_case(f"antisymmetric_{n}", (base - base.T) / 2, f"antisymmetric, n={n}")

# clear separation: a should be trivially ordered
n = 12
clear = -np.ones((n, n), np.float32) * 4.0
np.fill_diagonal(clear, 0.0)
for i in range(n):
    for j in range(i + 1, n):
        clear[i, j] = 6.0
decode_case("clear_order", clear, "triangular: every pair decided")


# --------------------------------------------------------------------------- synthetic clustering

cluster_case("empty", np.zeros((0, 0), np.float32))
cluster_case("single", np.zeros((1, 1), np.float32))
cluster_case("pair_same", np.array([[0.0, 5.0], [5.0, 0.0]], np.float32))
cluster_case("pair_diff", np.array([[0.0, -5.0], [-5.0, 0.0]], np.float32))
cluster_case("pair_zero", np.array([[0.0, 0.0], [0.0, 0.0]], np.float32))
cluster_case("two_groups", np.array(
    [[0, 4, 3, -6, -5],
     [4, 0, 3.5, -5, -6],
     [3, 3.5, 0, -4, -4],
     [-6, -5, -4, 0, 3],
     [-5, -6, -4, 3, 0]], np.float32))
cluster_case("chain", np.array(
    [[0, 2, -1, -1],
     [2, 0, 2, -1],
     [-1, 2, 0, 2],
     [-1, -1, 2, 0]], np.float32))
cluster_case("all_negative", np.full((4, 4), -1.0, np.float32) - np.diag(np.full(4, -1.0, np.float32)))
cluster_case("all_positive", np.ones((4, 4), np.float32) - np.diag(np.ones(4, np.float32)))

# synthetic block cases over the layouts above
same_clear = np.zeros((len(col2()), len(col2())), np.float32)
for i in range(len(same_clear)):
    for j in range(len(same_clear)):
        same_clear[i, j] = 4.0 if (i < 6) == (j < 6) and i != j else -4.0
a_clear = np.where(same_clear > 0, 3.0, -3.0).astype(np.float32)
np.fill_diagonal(a_clear, 0.0)
block_case("two_columns", a_clear, same_clear, col2(),
           np.zeros((len(same_clear), 10), np.float32), "two blocks by XY-cut")
grid_cls = np.zeros((len(same_clear), 10), np.float32)
grid_cls[:, 5] = 1.0
block_case("two_columns_grid", a_clear, same_clear, col2(), grid_cls, "grid class")
block_case("two_columns_oneblock", a_clear, np.full_like(same_clear, 2.0), col2(),
           np.zeros((len(same_clear), 10), np.float32), "every line one block")

# --------------------------------------------------------------------------- unicode predicates

digit, upper, space = [], [], []
for cp in range(sys.maxunicode + 1):
    ch = chr(cp)
    if ch.isdigit():
        digit.append(cp)
    if ch.isupper():
        upper.append(cp)
    if ch.isspace():
        space.append(cp)


def ranges(cps) -> list:
    out = []
    lo = prev = None
    for cp in cps:
        if lo is None:
            lo = prev = cp
        elif cp == prev + 1:
            prev = cp
        else:
            out.append([lo, prev])
            lo = prev = cp
    if lo is not None:
        out.append([lo, prev])
    return out


uni = {"note": "Python 3.14 / unicodedata " + unicodedata.unidata_version,
       "digit": ranges(digit), "upper": ranges(upper), "space": ranges(space)}
uni_path = FIX / "unicode_predicates.json"
uni_path.write_text(json.dumps(uni, indent=1) + "\n", encoding="utf-8")
print(f"wrote {uni_path.name}: digit {len(digit)} cps / upper {len(upper)} / space {len(space)}")

# --------------------------------------------------------------------------- np.log (float32)

# The port reproduces numpy's vectorised float32 log kernel (NpyLog).  Zero is left out: its
# result is -inf, which JSON cannot carry; the test checks it directly.
vals = np.concatenate([
    np.exp(rng.uniform(-80, 80, 700)).astype(np.float32),
    (rng.random(400) * 10000.0).astype(np.float32),
    (rng.random(300) * 1e-30).astype(np.float32),
    np.array([1.0, 2.0, 0.5, 1e-45, 1.17549435e-38, 1.17549421e-38, 3.4e38, 6.661338147750939e-16],
             np.float32),
    rng.uniform(0.99, 1.01, 400).astype(np.float32),
])
# Negative inputs (-> NaN), zero (-> -inf), +inf and NaN cannot be represented in JSON; the test
# checks those directly.  Everything in the corpus has to be finite on both sides.
expected_log = np.log(vals)
assert np.isfinite(vals).all() and np.isfinite(expected_log).all()
npylog = {
    "note": "np.log(np.float32) — numpy's vectorised kernel; zero/inf/nan are checked in the test",
    "inputs": [float(v) for v in vals],
    "expected": [float(v) for v in expected_log],
}
print(f"  npylog corpus: {len(vals)} values")

doc = {
    "note": "fixtures for indra_ocr.order_post; arrays are zlib+base64 of raw little-endian "
            "bytes (see tools/scripts/test/gen_order_fixtures.py)",
    "n_feats": N_FEATS,
    "features": features,
    "decode_order": decode_orders,
    "top_down": top_downs,
    "xy_cut": xy_cuts,
    "cluster": clusters,
    "decode_blocks": block_cases,
    "reading_order": reading_orders,
    "npylog": npylog,
    "stats": stats,
}
out = FIX / "order_post.json"
out.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"  portable simulation: decode_order {stats['decode_order_differ']}/{stats['decode_order']} "
      f"cases differ, decode_blocks_order {stats['blocks_differ']}/{stats['blocks']} cases differ")
print(f"wrote {out.name}: {out.stat().st_size} bytes ({len(features)} feature, "
      f"{len(decode_orders)} decode_order, {len(top_downs)} top_down, {len(xy_cuts)} xy_cut, "
      f"{len(clusters)} cluster, {len(block_cases)} decode_blocks, "
      f"{len(reading_orders)} reading_order cases)")
if warning:
    print("  NOTE: the portable simulation disagrees with the reference somewhere (see above)")
