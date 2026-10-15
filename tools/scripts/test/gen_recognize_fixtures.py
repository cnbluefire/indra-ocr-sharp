"""Generate src/Indra.Ocr.Tests/fixtures/recognize.json.

Parity fixtures for ``pipeline.OrtOCR.recognize`` / ``hanzi_lines`` — line cropping (with and
without CenterMask), the hanzi routing, width batching, the CTC collapse and the mapping of every
kept timestep back to a page x — see docs/100-recognize.md.

Every expectation is the reference's own output: the cases call the real ``OrtOCR.recognize``
through a stub that carries only the attributes the method touches (the sessions, the vocabs, the
mask, ``height``).  Two families:

* ``cases`` — the real ``rec.onnx`` / ``rec_cjk.onnx`` / ``script.onnx``.  The boxes come from the
  real detector (recorded in the fixture, so the test does not need ``det.onnx``), the pages are
  the ones from ``temp/inputs`` plus a page that mixes Latin and hanzi and one whose lines come in
  two very different widths, and a hand-made box list covers off-page and degenerate boxes.
* ``standin`` — a deterministic recognizer and script classifier over a synthetic vocabulary, so
  the CTC/route/x-mapping logic stays under test even without the model files.  Its confidences are
  dyadic (0.75 / 0.25), so the kept mean is exact in float32 whatever the summation order: those
  comparisons are bit-exact.

The generator also reports how far numpy's SIMD mean is from the portable pairwise one on the real
confidences (that is the documented deviation) and the closest confidence to ``MIN_CONF``.
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
from indra_ocr.line_mask import crop_lines  # noqa: E402
from indra_ocr.pipeline import MIN_CONF, OrtOCR  # noqa: E402
from indra_ocr.rec_preproc import PAD_X, pad_batch, width_batches  # noqa: E402
from indra_ocr.vocab import Vocab  # noqa: E402

MODELS = ROOT / "resources" / "models"
INPUTS = ROOT / "temp" / "inputs"


def z(a) -> str:
    return base64.b64encode(zlib.compress(np.ascontiguousarray(a).tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


def read(name: str) -> np.ndarray:
    img = cv2.imread(str(INPUTS / name), cv2.IMREAD_GRAYSCALE)
    assert img is not None, name
    return img


def session(name: str):
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1
    so.inter_op_num_threads = 1
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.enable_cpu_mem_arena = False
    return ort.InferenceSession(str(MODELS / name), so, providers=["CPUExecutionProvider"])


def pairwise(a) -> np.float32:
    """numpy's portable ``pairwise_sum_FLOAT`` (the port's ``Numerics.Sum32``)."""
    n = len(a)
    if n < 8:
        s = np.float32(0)
        for x in a:
            s = np.float32(s + x)
        return s
    if n <= 128:
        r = [np.float32(a[i]) for i in range(8)]
        i = 8
        limit = n - n % 8
        while i < limit:
            for k in range(8):
                r[k] = np.float32(r[k] + a[i + k])
            i += 8
        res = np.float32(
            np.float32(np.float32(r[0] + r[1]) + np.float32(r[2] + r[3]))
            + np.float32(np.float32(r[4] + r[5]) + np.float32(r[6] + r[7])))
        while i < n:
            res = np.float32(res + a[i])
            i += 1
        return res
    h = n // 2
    return np.float32(pairwise(a[:h]) + pairwise(a[h:]))


# --------------------------------------------------------------------------- the reference seams


class RecStub:
    """``OrtOCR`` carrying just what ``recognize`` / ``hanzi_lines`` touch."""

    recognize = OrtOCR.recognize
    hanzi_lines = OrtOCR.hanzi_lines

    def __init__(self, rec, vocab, mask=None, script=None, rec_cjk=None, vocab_cjk=None, height=32):
        self.rec = rec
        self.vocab = vocab
        self.mask = mask
        self.script = script
        self.rec_cjk = rec_cjk
        self.vocab_cjk = vocab_cjk
        self.height = height
        self.math = None


class DetStub:
    """``OrtOCR`` carrying just what ``detect`` touches (to produce realistic boxes)."""

    _detect_at = OrtOCR._detect_at
    detect = OrtOCR.detect

    def __init__(self, det, long_side=1024, adaptive=True):
        self.det = det
        self.long_side = long_side
        self.adaptive = adaptive


class StandinRec:
    """Deterministic stand-in for ``rec.onnx``; the C# test implements this identically.

    ``T = max(1, W // 4)`` timesteps for the padded batch width ``W`` and ``lengths[b]`` the
    line's own timestep count (``T + 5`` in ``overlong`` mode, so the port's clamp to ``T`` is
    exercised).  Every third timestep is the blank; the two others of a group carry the same id
    (``1 + (idbase + b * 5 + t // 3) % vocab``), so the CTC collapse has repeats to drop.  ``conf``
    is ``hi`` on even timesteps and ``lo`` on odd ones.
    """

    def __init__(self, vocab=36, hi=0.75, lo=0.25, mode="normal", idbase=0):
        self.vocab = vocab
        self.hi = hi
        self.lo = lo
        self.mode = mode
        self.idbase = idbase

    def run(self, _outputs, feed):
        x, widths = feed["x"], feed["widths"]
        b_n, _, _, w_pad = x.shape
        t_n = max(1, w_pad // 4)
        ids = np.zeros((b_n, t_n), np.int64)
        conf = np.zeros((b_n, t_n), np.float32)
        lengths = np.zeros(b_n, np.int64)
        for b in range(b_n):
            line = (int(widths[b]) + 3) // 4
            lengths[b] = t_n + 5 if self.mode == "overlong" else min(t_n, line)
            if self.mode == "blank":
                continue
            for t in range(t_n if self.mode == "overlong" else int(lengths[b])):
                if t % 3 != 2:
                    ids[b, t] = 1 + (self.idbase + b * 5 + t // 3) % self.vocab
                conf[b, t] = self.hi if t % 2 == 0 else self.lo
        return [ids, conf, lengths]


class StandinScript:
    """Per line: 1.0, 0.5 or 0.25 by position in the batch, so the ``> 0.5`` cut is on a boundary."""

    def __init__(self, pattern=(1.0, 0.5, 0.25)):
        self.pattern = pattern

    def run(self, _outputs, feed):
        b_n = feed["x"].shape[0]
        return [np.array([self.pattern[b % len(self.pattern)] for b in range(b_n)], np.float32)]


# --------------------------------------------------------------------------- pages


latin = read("latin.png")
cjk = read("cjk.png")
two_columns = read("two_columns.png")
blank = read("blank.png")

# one page holding Latin and hanzi lines, so both recognizer groups are non-empty
mixed = np.full((400, 900), 255, np.uint8)
mixed[60:140, 60:600] = latin[100:180, 100:640]
mixed[220:300, 60:600] = cjk[100:180, 100:640]

# narrow and wide lines on one page: width_batches has to split them (max_ratio 1.3)
widths_page = np.full((900, 1200), 255, np.uint8)
for i in range(8):
    widths_page[40 + i * 100 : 100 + i * 100, 40:200] = latin[100 + i * 20 : 160 + i * 20, 100:260]
    widths_page[40 + i * 100 : 100 + i * 100, 400:1100] = latin[100 + i * 20 : 160 + i * 20, 100:800]

canvas = np.full((1600, 2400), 255, np.uint8)     # the small-text page from step 095
for i in range(3):
    for j in range(4):
        canvas[100 + i * 500 : 180 + i * 500, 100 + j * 560 : 500 + j * 560] = latin[100:180, 100:500]

pages = {
    "latin": latin,
    "cjk": cjk,
    "two_columns": two_columns,
    "blank": blank,
    "mixed": mixed,
    "widths": widths_page,
    "canvas": canvas,
}

# hand-made boxes on the Latin page (700x900): a normal line, a one-pixel-high box, one that
# starts left of the page and one that lies completely outside it.
handmade = np.array(
    [
        [100.0, 100.0, 640.0, 130.0, 0.9],
        [100.0, 300.0, 640.0, 300.0, 0.9],       # y0 == y1: h collapses to 1.0
        [-40.0, 400.0, 300.0, 440.0, 0.9],       # starts left of the page
        [900.0, 500.0, 1000.0, 540.0, 0.9],      # entirely right of the page
    ],
    np.float32,
)


# --------------------------------------------------------------------------- runs


def tokens_of(toks) -> list:
    out = []
    for v, x in toks:
        out.append({"char": v, "id": 0, "x": float(x)} if isinstance(v, str)
                   else {"char": None, "id": int(v), "x": float(x)})
    return out


def lines_of(rec) -> list:
    return [{"text": t, "conf": float(c), "tokens": tokens_of(tk)} for t, c, tk in rec]


def crops_of(page, stub, boxes):
    return crop_lines(page, boxes[:, :4], stub.height, stub.mask)


def case(name, kind, page_name, boxes, stub, vocab_chars=None, note="", params=None):
    page = pages[page_name]
    rec = stub.recognize(page, boxes)
    hanzi = stub.hanzi_lines(crops_of(page, stub, boxes), 64) if stub.script is not None else []
    confs = [c for _, c, _ in rec]
    rec_case = {
        "name": name,
        "kind": kind,
        "page": page_name,
        "boxes": f32(boxes),
        "n": int(len(boxes)),
        "lines": lines_of(rec),
        "hanzi": [bool(h) for h in hanzi],
        "note": note,
    }
    if vocab_chars is not None:
        rec_case["vocab"] = vocab_chars
    if params is not None:
        rec_case["params"] = params
    print(f"  {name:22s} boxes={len(boxes):3d} lines={len(rec):3d} "
          f"tokens={sum(len(tk) for _, _, tk in rec):4d} hanzi={sum(rec_case['hanzi']):2d} "
          f"conf<.5={sum(1 for c in confs if 0.0 < c < MIN_CONF):2d} "
          f"conf=[{min(confs) if confs else 0:.4f},{max(confs) if confs else 0:.4f}]")
    return rec_case


# --------------------------------------------------------------------------- the real models

cases: list = []
stats = {"lines": 0, "differ": 0, "worst": 0.0, "closest": 1.0}
if (MODELS / "rec.onnx").exists() and (MODELS / "script.onnx").exists():
    meta = json.loads((MODELS / "meta.json").read_text())
    vocab = Vocab.from_json(meta["vocab"])
    vocab_cjk = Vocab.from_json(meta["vocab_cjk"])
    det_sess = session("det.onnx")
    real = RecStub(session("rec.onnx"), vocab, mask=None, script=session("script.onnx"),
                   rec_cjk=session("rec_cjk.onnx"), vocab_cjk=vocab_cjk,
                   height=meta["rec_height"])
    det = DetStub(det_sess)

    for page_name, long_side in [("latin", 1024), ("cjk", 1024), ("two_columns", 1024),
                                 ("blank", 256), ("mixed", 1024), ("widths", 1024),
                                 ("canvas", 1024)]:
        det.long_side = long_side
        cases.append(case(page_name, "real", page_name, det.detect(pages[page_name]), real))
    cases.append(case("handmade", "real", "latin", handmade, real))

    for c in cases:
        for line in c["lines"]:
            if line["conf"] > 0.0:
                stats["closest"] = min(stats["closest"], abs(line["conf"] - MIN_CONF))

    # Measure the conf deviation on the recognizer's own batches: numpy's mean of the kept peaks
    # against the portable pairwise sum the port uses.
    for c in cases:
        page = pages[c["page"]]
        boxes = np.frombuffer(zlib.decompress(base64.b64decode(c["boxes"])), np.float32).reshape(-1, 5)
        crops = crops_of(page, real, boxes)
        if not crops:
            continue
        for part in width_batches([cc.shape[1] for cc in crops], 64):
            arr, w = pad_batch([crops[j] for j in part])
            best, pmax, lengths = real.rec.run(None, {"x": arr, "widths": w})
            for k in range(len(part)):
                n = min(int(lengths[k]), best.shape[1])
                seq, pm = best[k, :n], pmax[k, :n]
                keep = (seq != 0) & np.concatenate([[True], seq[1:] != seq[:-1]])
                if not keep.any():
                    continue
                mine = np.float32(pairwise(pm[keep]) / np.float32(int(keep.sum())))
                theirs = pm[keep].mean()
                stats["lines"] += 1
                if mine != theirs:
                    stats["differ"] += 1
                    stats["worst"] = max(stats["worst"],
                                         abs(float(mine) - float(theirs)) / abs(float(theirs)))
    print(f"  real confidences: {stats['differ']}/{stats['lines']} differ from numpy's SIMD mean, "
          f"worst relative {stats['worst']:.3e}")
    print(f"  closest real confidence to MIN_CONF={MIN_CONF}: {stats['closest']:.3e}")
else:
    print("  rec.onnx / script.onnx missing: real cases skipped")


# --------------------------------------------------------------------------- the stand-ins


def standin_case(name, mode="normal", hi=0.75, lo=0.25, idbase=0, script=False, boxes=None,
                 page="latin"):
    latin_chars = [chr(ord("a") + i) for i in range(26)] + [str(i) for i in range(10)]
    cjk_chars = ["漢", "字", "中", "文", "測", "試", "東", "西", "南", "北"]
    stub = RecStub(StandinRec(vocab=len(latin_chars), hi=hi, lo=lo, mode=mode, idbase=idbase),
                   Vocab(latin_chars), height=32)
    vocab_chars = latin_chars
    params = {"hi": float(hi), "lo": float(lo), "mode": mode, "idbase": int(idbase)}
    if script:
        stub.script = StandinScript()
        stub.rec_cjk = StandinRec(vocab=len(cjk_chars), hi=hi, lo=lo, mode=mode, idbase=7)
        stub.vocab_cjk = Vocab(cjk_chars)
        vocab_chars = {"latin": latin_chars, "cjk": cjk_chars}
        params["cjk_idbase"] = 7
    return case(name, "standin", page, boxes, stub, vocab_chars=vocab_chars, note=mode,
                params=params)


# Similar widths, so all three lines fit one batch: the stand-in's script pattern varies *within*
# a batch (1.0 / 0.5 / 0.25 by position), and the recognizer's padded batch holds lines of
# slightly different widths.
box = np.array([[100.0, 100.0, 400.0, 140.0, 0.9],
                [100.0, 200.0, 420.0, 240.0, 0.9],
                [100.0, 300.0, 410.0, 330.0, 0.9]], np.float32)

print("  stand-in cases:")
standin_cases = [
    standin_case("standin_plain", boxes=box),
    standin_case("standin_highconf", hi=1.0, lo=1.0, boxes=box),
    standin_case("standin_gated", hi=0.5, lo=0.25, boxes=box, page="mixed"),
    standin_case("standin_blank", mode="blank", boxes=box),
    standin_case("standin_overlong", mode="overlong", boxes=box, page="widths"),
    standin_case("standin_cjk", script=True, boxes=box, page="mixed"),
    standin_case("standin_cjk_high", script=True, hi=0.75, lo=0.5, idbase=3, boxes=box,
                 page="mixed"),
    standin_case("standin_empty", boxes=box[:0]),
]

doc = {
    "note": "fixtures for pipeline.OrtOCR.recognize / hanzi_lines; arrays are zlib+base64 of raw "
            "little-endian bytes (see tools/scripts/test/gen_recognize_fixtures.py)",
    "min_conf": float(MIN_CONF),
    "pad_x": float(PAD_X),
    "pages": {n: {"h": int(p.shape[0]), "w": int(p.shape[1]), "gray": z(p)}
              for n, p in pages.items()},
    "cases": cases,
    "standin": standin_cases,
    "stats": stats,
}
path = fixtures_dir() / "recognize.json"
path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {path.name}: {path.stat().st_size} bytes "
      f"({len(pages)} pages, {len(cases)} real cases, {len(standin_cases)} stand-in cases)")
