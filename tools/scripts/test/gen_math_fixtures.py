"""Generate src/Indra.Ocr.Tests/fixtures/math.json — parity fixtures for step 200
(``math.MathReader``: the math line classifier and MathRec, the attention decoder).

Three groups:

* ``detokenize`` — the pure token-to-text rule, ``$`` pairing included;
* ``real`` — the four models of ``temp/math-models`` (not in git) over the math page of
  ``temp/inputs``: the classifier's raw probabilities and flags, and MathRec's readings.  The
  page and the detector's boxes travel in the fixture (zlib+base64), so the C# test needs
  neither ``det.onnx`` nor an image codec;
* ``standin`` — deterministic stand-ins for the four graphs, so the decoding loop (batching,
  the scalar ``pos``, the feed filtering, the cache feedback, EOS, the early stop, the mean
  probability) stays under test without the model files.  The C# test implements the same
  stand-ins.

Run with the reference environment:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_math_fixtures.py
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
from indra_ocr.math import EARLY_STOP_MARGIN, MATH_MIN, MATH_MIN_CONF, MathReader, detokenize  # noqa: E402
from indra_ocr.pipeline import OrtOCR  # noqa: E402
from indra_ocr.rec_preproc import pad_batch, width_batches  # noqa: E402

MODELS = ROOT / "resources" / "models"
MATH_MODELS = ROOT / "temp" / "math-models"
INPUTS = ROOT / "temp" / "inputs"


def z(a) -> str:
    """zlib+base64 of the raw little-endian bytes of an array (uint8 or float32)."""
    return base64.b64encode(zlib.compress(np.ascontiguousarray(a).tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


def options() -> ort.SessionOptions:
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1
    so.inter_op_num_threads = 1
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.enable_cpu_mem_arena = False
    return so


class DetStub:
    """``OrtOCR`` carrying just what ``detect`` touches (to produce realistic boxes)."""

    _detect_at = OrtOCR._detect_at
    detect = OrtOCR.detect

    def __init__(self, det):
        self.det = det
        self.long_side = 1024
        self.adaptive = True


# --------------------------------------------------------------------------- detokenize


DETOKENIZE = [
    ([], ""),
    (["a"], "a"),
    (["a", "b"], "ab"),
    (["", "a", ""], "a"),
    (["$", "x", "$"], "$x$"),
    (["$", "x"], "$x$"),
    (["$"], ""),
    (["$", "$"], "$$"),
    (["we", "have", "$", "x", "$", "and", "$", "y", "_", "1", "$"], "wehave$x$and$y _ 1$"),
    (["$", "\\frac", "{", "1", "}", "{", "2", "}", "$"], "$\\frac { 1 } { 2 }$"),
    (["a ", "b", "\t", "c"], "ab c"),
    ([" ", " ", "x", " ", " "], "x"),
    (["\u4f60", "\u597d", " ", "\u4e16"], "\u4f60\u597d \u4e16"),
    (["a", "\u00a0", "b"], "a b"),
    (["\n", "a", "\r\n", "b", "\n"], "a b"),
]


# --------------------------------------------------------------------------- the real models


def real_section() -> dict | None:
    """The four models over the math page: probabilities, flags and readings."""
    if not (MATH_MODELS / "math_meta.json").exists():
        print("  temp/math-models missing: the real case is skipped")
        return None

    meta = json.loads((MATH_MODELS / "meta.json").read_text(encoding="utf-8"))
    math_meta = json.loads((MATH_MODELS / "math_meta.json").read_text(encoding="utf-8"))
    cfg = math_meta["config"]
    so = options()
    reader = MathReader(MATH_MODELS, so, ["CPUExecutionProvider"])
    page = cv2.imread(str(INPUTS / "math.png"), cv2.IMREAD_GRAYSCALE)
    assert page is not None, "temp/inputs/math.png is missing (run tools/scripts/gen_inputs.py)"
    det = DetStub(ort.InferenceSession(str(MATH_MODELS / "det.onnx"), so,
                                       providers=["CPUExecutionProvider"]))
    boxes = det.detect(page)

    # The recognizer's crops (rec_height, no CenterMask): what `flags` sees.
    crops = crop_lines(page, boxes[:, :4], meta["rec_height"], None)
    probabilities = np.zeros(len(crops), np.float32)
    for part in width_batches([c.shape[1] for c in crops], 64):
        arr, w = pad_batch([crops[j] for j in part])
        probabilities[part] = reader.cls.run(None, {"x": arr, "widths": w})[0]
    flags = reader.flags(crops)
    assert np.array_equal(flags, probabilities > MATH_MIN), "flags is not the cut of the p's"

    idx = [i for i, f in enumerate(flags) if f]
    readings = reader.read(page, boxes[idx])
    print(f"  real: {len(crops)} lines, {len(idx)} flagged, "
          f"p=[{probabilities.min():.4f},{probabilities.max():.4f}], "
          f"{sum(1 for t, c in readings if '$' in t and c >= MATH_MIN_CONF)} replaced")

    return {
        "page": {"h": int(page.shape[0]), "w": int(page.shape[1]), "gray": z(page)},
        "rec_height": int(meta["rec_height"]),
        "boxes": f32(boxes),
        "n": int(len(boxes)),
        "config": {"height": int(cfg["height"]), "max_tokens": int(cfg["max_tokens"]),
                   "layers": int(cfg["dec_layers"]), "heads": int(cfg["dec_heads"]),
                   "channels": int(cfg["channels"]),
                   "head_dim": int(cfg["channels"]) // int(cfg["dec_heads"]),
                   "tokens": len(math_meta["tokens"])},
        "probabilities": f32(probabilities),
        "flags": [bool(f) for f in flags],
        "readings": [{"index": int(i), "text": t, "confidence": float(c)}
                     for i, (t, c) in zip(idx, readings)],
    }


# --------------------------------------------------------------------------- the stand-ins


class StandinCls:
    """``p = hi`` for the first line of a batch and ``lo`` for the rest, so the batching shows."""

    def __init__(self, hi=0.9, lo=0.1):
        self.hi = hi
        self.lo = lo
        self.calls: list[list[str]] = []

    def run(self, _outputs, feed):
        self.calls.append(sorted(feed))
        b_n = feed["x"].shape[0]
        return [np.array([self.hi if i == 0 else self.lo for i in range(b_n)], np.float32)]


class StandinEnc:
    """``memory`` [B, T, C] and ``mask`` [B, T] of zeros: the scripted decoder ignores both."""

    def __init__(self, t=3, channels=192):
        self.t = t
        self.channels = channels
        self.calls: list[list[str]] = []

    def run(self, _outputs, feed):
        self.calls.append(sorted(feed))
        b_n = feed["x"].shape[0]
        return [np.zeros((b_n, self.t, self.channels), np.float32),
                np.zeros((b_n, self.t), np.float32)]


class StandinInit:
    """``cross_k`` / ``cross_v`` [L, B, H, T, D] from the memory's shape; it must not see a mask."""

    def __init__(self, layers=3, heads=8, head_dim=24):
        self.layers = layers
        self.heads = heads
        self.head_dim = head_dim
        self.calls: list[list[str]] = []

    def run(self, _outputs, feed):
        self.calls.append(sorted(feed))
        b_n, t_n, _ = feed["memory"].shape
        k = np.zeros((self.layers, b_n, self.heads, t_n, self.head_dim), np.float32)
        return [k, k + 1.0]


class StandinStep:
    """The scripted decoder.

    ``script[r]`` is the token ids row ``r`` of a batch emits, one per step, or ``None`` for a row
    that emits the low-probability filler instead: every logit equal, so the reader's argmax peak
    probability is ``1 / vocab`` and the early stop fires after 8 tokens.  A row whose script runs
    out emits EOS.

    The stand-in also checks the loop's invariants, so the C# test fails loudly when the port gets
    them wrong: the self-attention cache it is handed must be ``[L, B, H, pos, D]`` (empty at the
    first step, the previous step's output afterwards), ``pos`` is the step number, and ``token``
    is what the previous step's argmax selected.
    """

    def __init__(self, script, vocab, eos=2, hi=20.0, layers=3, heads=8, head_dim=24):
        self.script = script
        self.vocab = vocab
        self.eos = eos
        self.hi = hi
        self.layers = layers
        self.heads = heads
        self.head_dim = head_dim
        self.calls: list[list[str]] = []

    def run(self, _outputs, feed):
        self.calls.append(sorted(feed))
        token, pos = feed["token"], int(feed["pos"])
        b_n = len(token)
        k = feed["self_k"]
        assert k.shape == (self.layers, b_n, self.heads, pos, self.head_dim), k.shape

        logits = np.zeros((b_n, self.vocab), np.float32)
        for r in range(b_n):
            want = self.script[r % len(self.script)]
            if want is None:
                continue                                     # all equal: top = 1 / vocab
            if pos < len(want):
                logits[r, want[pos]] = self.hi
                if pos > 0:
                    assert int(token[r]) == want[pos - 1], (int(token[r]), want[pos - 1])
            else:
                logits[r, self.eos] = self.hi
        nk = np.zeros((self.layers, b_n, self.heads, pos + 1, self.head_dim), np.float32)
        return [logits, nk, nk + 1.0]


def standin_section() -> dict:
    """The scripted graphs over hand-made crops and a small page."""
    tokens = ["$", "x", "_", "{", "i", "}", "\\leq", "1", "+", "2"]
    itos = ["", "", "", ""] + tokens
    config = {"height": 64, "max_tokens": 10, "layers": 3, "heads": 8, "channels": 192}

    def reader_of(script):
        """A ``MathReader`` over stand-in graphs: ``__init__`` wants the four sessions, so the
        reader is built empty and given the same attributes ``__init__`` would set."""
        cls, enc, init, step = StandinCls(), StandinEnc(), StandinInit(), StandinStep(script,
                                                                                     len(itos))
        mr = MathReader.__new__(MathReader)
        mr.cls, mr.enc, mr.init, mr.step = cls, enc, init, step
        mr.itos = itos
        mr.height = config["height"]
        mr.max_tokens = config["max_tokens"]
        mr.layers = config["layers"]
        mr.heads = config["heads"]
        mr.head_dim = config["channels"] // config["heads"]
        # What `__init__` reads off the sessions: the init graph does not read the mask.
        mr.inputs = {"math_dec_init.onnx": {"memory"},
                     "math_dec_step.onnx": {"token", "pos", "self_k", "self_v", "cross_k",
                                            "cross_v", "mask"}}
        return mr, (cls, enc, init, step)

    # --- flags: three crops of the math page's own size, plus a very wide one, so width_batches
    # has to split (max_ratio 4.0) and pad.
    page = cv2.imread(str(INPUTS / "math.png"), cv2.IMREAD_GRAYSCALE)
    assert page is not None
    boxes = np.array([[60.0, 100.0, 1000.0, 150.0, 0.9],
                      [60.0, 200.0, 400.0, 250.0, 0.9],
                      [60.0, 300.0, 900.0, 350.0, 0.9],
                      [60.0, 400.0, 1340.0, 450.0, 0.9]], np.float32)
    crops = crop_lines(page, boxes[:, :4], 32, None)
    wide = np.concatenate([crops[1], crops[1]], axis=1)
    crops = crops + [wide]
    mr, (cls, enc, init, step) = reader_of([[4, 5, 6]])
    probabilities = np.zeros(len(crops), np.float32)
    batches = width_batches([c.shape[1] for c in crops], 64)
    for part in batches:
        arr, w = pad_batch([crops[j] for j in part])
        probabilities[part] = mr.cls.run(None, {"x": arr, "widths": w})[0]
    flags = mr.flags(crops)
    print(f"  stand-in flags: {len(crops)} crops in {len(batches)} batches, "
          f"{sum(flags)} flagged")

    flags_section = {
        "config": config,
        "tokens": tokens,
        "crops": [{"h": 32, "w": int(c.shape[1]), "f": f32(c)} for c in crops],
        "batches": [[int(j) for j in part] for part in batches],
        "probabilities": f32(probabilities),
        "flags": [bool(f) for f in flags],
    }

    # --- read: four lines, one per scripted behaviour (tokens with `$`, the low-probability
    # filler, tokens without `$`, and a script longer than max_tokens - 1).
    script = [[4, 5, 6], None, [7, 8], [4, 5, 6, 7, 8, 9, 10, 11, 12, 4, 5, 6]]
    mr, (cls, enc, init, step) = reader_of(script)
    read_boxes = np.array([[60.0, 100.0, 400.0, 150.0, 0.9],
                           [60.0, 200.0, 410.0, 250.0, 0.9],
                           [60.0, 300.0, 420.0, 350.0, 0.9],
                           [60.0, 400.0, 430.0, 450.0, 0.9]], np.float32)
    readings = mr.read(page, read_boxes)
    print(f"  stand-in read: {len(readings)} readings, "
          f"{sum(1 for t, _ in readings if '$' in t)} with $")

    read_section = {
        "config": config,
        "tokens": tokens,
        "script": script,
        "page": {"h": int(page.shape[0]), "w": int(page.shape[1]), "gray": z(page)},
        "boxes": f32(read_boxes),
        "readings": [{"text": t, "confidence": float(c)} for t, c in readings],
        "calls": {"enc": sorted({tuple(c) for c in enc.calls}),
                  "init": sorted({tuple(c) for c in init.calls}),
                  "step": sorted({tuple(c) for c in step.calls})},
    }
    return {"flags": flags_section, "read": read_section}


# --------------------------------------------------------------------------- write it out


real = real_section()
standin = standin_section()
doc = {
    "note": "fixtures for math.MathReader (step 200); arrays are zlib+base64 of raw "
            "little-endian bytes (see tools/scripts/test/gen_math_fixtures.py)",
    "min": float(MATH_MIN),
    "min_conf": float(MATH_MIN_CONF),
    "early_stop_margin": float(EARLY_STOP_MARGIN),
    "detokenize": [{"tokens": tokens, "text": detokenize(tokens)}
                   for tokens, _ in DETOKENIZE],
    "standin": standin,
}
if real is not None:
    doc["real"] = real

path = fixtures_dir() / "math.json"
path.write_text(json.dumps(doc, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"wrote {path.name}: {path.stat().st_size} bytes "
      f"({len(doc['detokenize'])} detokenize cases, real={'yes' if real else 'no'})")
