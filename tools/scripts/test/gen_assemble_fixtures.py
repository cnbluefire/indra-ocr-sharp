"""Generate src/Indra.Ocr.Tests/fixtures/assemble.json.

Parity fixtures for the assemble half of ``indra_ocr/pipeline.py`` (step 130) - see
docs/130-assemble.md:

* ``is_space``            - the reference's ``_is_space`` over id / char tokens;
* ``decode``              - ``_decode``: the vocab path and the char path (``" ".join(s.split())``);
* ``split_line``          - the wide-gap / gutter cuts, with and without ``at``;
* ``gutter_cuts``         - the column-gutter detection (float32 band comparisons);
* ``order_backtracks``    - the upward moves of the content order;
* ``pdf_order_coherent``  - the same with the model-agreement check;
* ``assemble``            - whole pages through the real ``OrtOCR.assemble``: real pages of
                            ``recognize.json`` driven by the real ``order.onnx``, plus synthetic
                            pages that reach the paths the real pages do not (a table line that the
                            table heads split into cells and the second order pass after it, a
                            wide-gap split, gutter cuts, text-layer tokens with a coherent and an
                            incoherent content order, no order model at all, empty and single-line
                            pages).

The cases that need an order model do **not** ship a model file: every ``assemble`` case records
the logits its (real or fixed) model returned for each line count it was called with, so the C#
test drives ``Assembler`` with a small table-backed ``IOrderModel``.  ``assemble`` itself runs the
*real* reference function (bound to a stub carrying the model, the vocab and the flags).

Run:  tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_assemble_fixtures.py
"""

from __future__ import annotations

import base64
import json
import os
import sys
import zlib
from pathlib import Path

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import fixtures_dir  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
REFERENCE = Path(os.environ.get("INDRA_OCR_REFERENCE", ROOT / "resources" / "indra-src"))
sys.path.insert(0, str(REFERENCE))
from indra_ocr.order_post import BLOCK_CLASSES  # noqa: E402
from indra_ocr.pipeline import OrtOCR  # noqa: E402
try:  # removed upstream in 1.1: kept here to refresh the port's compatibility helper fixtures
    from indra_ocr.textlayer import order_backtracks, pdf_order_coherent  # type: ignore[attr-defined]  # noqa: E402
except ImportError:
    def order_backtracks(boxes):
        if len(boxes) < 3:
            return 0
        h = float(np.median(boxes[:, 3] - boxes[:, 1]))
        return sum(b[1] < a[1] - 0.5 * h and b[0] < a[2] - h
                   for a, b in zip(boxes[:-1], boxes[1:]))

    def pdf_order_coherent(boxes, pair_logits=None):
        if order_backtracks(boxes) > max(1, int(0.02 * len(boxes))):
            return False
        if pair_logits is None or len(boxes) < 3:
            return True
        i = np.arange(len(boxes) - 1)
        return float((pair_logits[i, i + 1] > 0).mean()) >= 0.85
from indra_ocr.vocab import Vocab  # noqa: E402

MODELS = Path(os.environ.get("INDRA_OCR_TEST_MODELS", ROOT / "resources" / "models"))
FIX = fixtures_dir()
f32_ = np.float32


def z(a) -> str:
    return base64.b64encode(zlib.compress(np.ascontiguousarray(a).tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


def session() -> ort.InferenceSession:
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1
    so.inter_op_num_threads = 1
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.enable_cpu_mem_arena = False
    return ort.InferenceSession(str(MODELS / "order.onnx"), so, providers=["CPUExecutionProvider"])


meta = json.loads((MODELS / "meta.json").read_text(encoding="utf-8"))
vocab = Vocab.from_json(meta["vocab"]) if hasattr(Vocab, "from_json") else Vocab(meta["vocab"]["chars"])
print(f"  vocab: {len(vocab.chars)} chars, space id {vocab.index.get(' ')}")


# --------------------------------------------------------------------------- token json

def tokens_json(toks) -> list:
    out = []
    for tk in toks:
        item = {"x": float(tk[1])}
        if isinstance(tk[0], str):
            item["char"] = tk[0]
        else:
            item["id"] = int(tk[0])
        if len(tk) > 2:
            item["content"] = int(tk[2])
        out.append(item)
    return out


def rec_json(rec) -> list:
    return [{"text": r[0], "conf": float(r[1]), "tokens": tokens_json(r[2])} for r in rec]


# --------------------------------------------------------------------------- the stub model

class Stub:
    """``OrtOCR`` with only what ``assemble`` touches: the model, the vocab, the flags."""

    def __init__(self, order, table_heads=False, furniture=False):
        self.order = order
        self.table_heads = table_heads
        self.furniture = furniture
        self.vocab = vocab

    _is_space = OrtOCR._is_space
    _decode = OrtOCR._decode
    split_line = OrtOCR.split_line
    gutter_cuts = OrtOCR.gutter_cuts
    reading_order = OrtOCR.reading_order


class RecordingModel:
    """Wraps the real session (or a table of fixed outputs) and records every answer by line count."""

    def __init__(self, sess=None, fixed=None):
        self.sess = sess
        self.fixed = fixed or {}
        self.calls: dict[int, tuple] = {}

    def run(self, _, feeds):
        n = int(feeds["feats"].shape[1])
        if self.sess is not None:
            outs = list(self.sess.run(None, feeds))
        else:
            outs = list(self.fixed[n])
        a = outs[0][0]
        cls = outs[1][0] if len(outs) >= 3 else None
        same = outs[2][0] if len(outs) >= 3 else None
        tab = outs[3][0] if len(outs) >= 4 else None
        self.calls[n] = (a, cls, same, tab)
        return outs

    def outputs_json(self) -> list:
        out = []
        for n in sorted(self.calls):
            a, cls, same, tab = self.calls[n]
            out.append({
                "n": int(n),
                "a": f32(a),
                "cls": None if cls is None else f32(cls),
                "same": None if same is None else f32(same),
                "tab": None if tab is None else f32(tab),
            })
        return out


def fixed_page(n, groups=None, classes=None, rows=None, cols=None, cells=None):
    """[1, ...] outputs for ``n`` lines: ``a`` orders 0..n-1, ``same``/``cls`` from the groups,
    and the table heads from ``rows`` / ``cols`` / ``cells`` (line-index groups)."""
    a = np.zeros((1, n, n), np.float32)
    for i in range(n):
        for j in range(n):
            if i != j:
                a[0, i, j] = 2.0 if i < j else -2.0
    groups = groups or [[i] for i in range(n)]
    classes = classes or ["Text"] * len(groups)
    same = np.full((1, n, n), -1.0, np.float32)
    np.fill_diagonal(same[0], 0.0)
    cls = np.zeros((1, n, 10), np.float32)
    for g in groups:
        for x, y in zip(g, g[1:]):
            same[0, x, y] = same[0, y, x] = 1.0
    for g, cname in zip(groups, classes):
        for i in g:
            cls[0, i, BLOCK_CLASSES.index(cname)] = 1.0
    outs = [a, cls, same]
    if rows is not None or cols is not None or cells is not None:
        tab = np.full((1, 3, n, n), -1.0, np.float32)
        for k, grouping in enumerate((rows, cols, cells)):
            if grouping is None:
                continue
            for g in grouping:
                for x in g:
                    for y in g:
                        if x != y:
                            tab[0, k, x, y] = 2.0
        np.fill_diagonal(tab[0][0], 0.0)
        np.fill_diagonal(tab[0][1], 0.0)
        np.fill_diagonal(tab[0][2], 0.0)
        outs.append(tab)
    return outs


# --------------------------------------------------------------------------- flat sections
split_cases: list = []
cut_cases: list = []
backtrack_cases: list = []
coherent_cases: list = []
assemble_cases: list = []
space_cases: list = []
decode_cases: list = []


def space_tok(x: float):
    """A recognizer space token: the vocab's space id."""
    return (vocab.index.get(" "), x)


def add_space(name: str, tok):
    # _is_space(tok) is called with the token *key* (tk[0]), not with the (key, x) tuple.
    space_cases.append({"name": name, "token": tokens_json([tok])[0],
                        "expected": bool(OrtOCR._is_space(Stub(None), tok[0]))})


add_space("id_space", (vocab.index.get(" "), 10.0))
add_space("id_letter", (vocab.index.get("a"), 10.0))
add_space("char_space", (" ", 10.0))
add_space("char_letter", ("a", 10.0))
add_space("char_space_content", (" ", 10.0, 3))


def add_decode(name: str, toks, note=""):
    text = OrtOCR._decode(Stub(None), toks)
    decode_cases.append({"name": name, "tokens": tokens_json(toks), "expected": text,
                         "note": note})


add_decode("id_run", [(vocab.index.get("T"), 1.0), (vocab.index.get("h"), 2.0),
                      (vocab.index.get("e"), 3.0)])
add_decode("id_spaces", [(vocab.index.get("a"), 1.0), (vocab.index.get(" "), 2.0),
                         (vocab.index.get("b"), 3.0)])
add_decode("chars", [("a", 1.0), ("b", 2.0)])
add_decode("chars_spaces", [("a", 1.0), (" ", 2.0), (" ", 3.0), ("b", 4.0)],
           "\" \".join(\"\".join(chars).split())")
add_decode("chars_controls", [("a", 1.0), ("\u001c", 2.0), ("b", 3.0)],
           "Python's split() drops 0x1c")
add_decode("chars_nbsp", [("\u00a0", 1.0), ("a", 2.0), ("\u00a0", 3.0)])
add_decode("chars_content", [("a", 1.0, 0), ("b", 2.0, 1)])
add_decode("empty", [])
add_decode("id_empty", [])


def add_split(name, box, toks, gap, at=None, note=""):
    b = np.ascontiguousarray(box, np.float32)
    parts = OrtOCR.split_line(Stub(None), b, list(toks), gap, at)
    split_cases.append({
        "name": name, "box": f32(b), "tokens": tokens_json(toks), "gap": float(gap),
        "at": None if at is None else [float(v) for v in at],
        "expected": [{"box": [float(v) for v in bx], "text": tx, "tokens": tokens_json(tk)}
                     for bx, tx, tk in parts],
        "note": note,
    })


def line_tokens(text: str, x0: float, dx: float, space_id=None):
    """(id, x) tokens for ``text``: the vocab's ids where they exist, else chars."""
    space_id = vocab.index.get(" ") if space_id is None else space_id
    toks = []
    x = x0
    for ch in text:
        if ch == " ":
            toks.append((space_id, x))
        else:
            toks.append((vocab.index.get(ch, vocab.index.get("?")), x))
        x += dx
    return toks


# ``close``: neighbouring characters 5px apart, less than 3 * h = 60 for a 20px line.
close = line_tokens("abcdef ghijkl", 10.0, 5.0)
# ``wide``: two halves 90px apart, more than the SPLIT_GAP for a 20px line - the detector
# merged two cells or two columns into one line.
wide = line_tokens("abc", 10.0, 5.0) + line_tokens("def", 110.0, 5.0)

add_split("no_cut", [0.0, 0.0, 200.0, 20.0], close, 3.0, None, "no gap over 3 * h")
add_split("wide_cut", [0.0, 0.0, 200.0, 20.0], wide, 3.0, None,
          "the 90px gap is over 3 * h = 60: cut")
add_split("wide_no_cut", [0.0, 0.0, 200.0, 20.0], wide, 5.0, None,
          "the same gap under gap=5: 5 * h = 100, no cut")
add_split("at_cut", [0.0, 0.0, 200.0, 20.0], close, 3.0, [37.5],
          "a gutter cut between f and g, right after the space")
add_split("at_outside", [0.0, 0.0, 200.0, 20.0], close, 3.0, [5.0, 500.0],
          "`at` outside the line: no cut")
add_split("at_equal", [0.0, 0.0, 200.0, 20.0], close, 3.0, [20.0],
          "`at` equal to a token x: the strict inequalities keep it in one piece")
add_split("split_two", [0.0, 0.0, 400.0, 20.0],
          line_tokens("a b c d e", 10.0, 50.0), 1.0, [40.0, 210.0],
          "two cuts, one from the gap and one from `at`")
add_split("empty", [0.0, 0.0, 100.0, 20.0], [], 3.0)
add_split("spaces_only", [0.0, 0.0, 100.0, 20.0], [space_tok(5.0), space_tok(40.0)], 1.0, [20.0],
          "spaces are never cut points")
add_split("every_pair_cut", [0.0, 0.0, 200.0, 20.0], wide, 0.1, None,
          "gap 0.1 * h = 2: every neighbouring pair is cut")
add_split("blank_ids", [0.0, 0.0, 200.0, 20.0],
          [(0, 10.0), (0, 60.0), (0, 110.0)], 1.0, None,
          "vocab id 0 is the CTC blank and decodes to nothing: all segments dropped")
add_split("char_tokens", [0.0, 0.0, 200.0, 20.0],
          [("a", 10.0), ("b", 20.0), (" ", 60.0), ("c", 90.0), ("d", 100.0)], 1.0, [55.0],
          "char tokens, no content index")
add_split("tiny_box", [0.0, 10.0, 100.0, 10.0], wide, 3.0, [95.0],
          "zero height: h clamps to 1.0, so 3 * h = 3 and 90 > 3")


def add_cuts(name, boxes, rec, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    cuts = OrtOCR.gutter_cuts(Stub(None), b, rec)
    cut_cases.append({"name": name, "n": int(len(b)), "boxes": f32(b), "rec": rec_json(rec),
                      "expected": [[float(v) for v in c] for c in cuts], "note": note})


col_left = [0.0, 100.0, 100.0, 130.0], [0.0, 140.0, 100.0, 170.0], [0.0, 180.0, 100.0, 210.0]
col_right = [300.0, 100.0, 400.0, 130.0], [300.0, 140.0, 400.0, 170.0], [300.0, 180.0, 400.0, 210.0]
wide_row = [0.0, 220.0, 400.0, 250.0]

add_cuts("no_tokens", [[0.0, 0.0, 100.0, 20.0]], [("a", 1.0, [])])
add_cuts("single_token", [[0.0, 0.0, 100.0, 20.0]],
         [("ab", 1.0, line_tokens("ab", 10.0, 5.0))])
add_cuts("no_gap", [[0.0, 0.0, 100.0, 20.0]], [("ab", 1.0, line_tokens("ab", 10.0, 5.0))])
add_cuts("wide_gap_few_edges",
         [[0.0, 0.0, 100.0, 20.0], [0.0, 100.0, 100.0, 120.0]],
         [("a b", 1.0, line_tokens("a b", 10.0, 40.0)),
          ("c", 1.0, line_tokens("c", 10.0, 5.0))])
add_cuts("gutter",
         [list(col_left[0]), list(col_left[1]), list(col_left[2]),
          list(col_right[0]), list(col_right[1]), list(col_right[2]), list(wide_row)],
         [("left one", 1.0, line_tokens("leftone", 10.0, 30.0)),
          ("left two", 1.0, line_tokens("lefttwo", 10.0, 30.0)),
          ("left three", 1.0, line_tokens("leftthree", 10.0, 10.0)),
          ("right one", 1.0, line_tokens("rightone", 310.0, 30.0)),
          ("right two", 1.0, line_tokens("righttwo", 310.0, 30.0)),
          ("right three", 1.0, line_tokens("rightthree", 310.0, 10.0)),
          ("left right", 1.0, [*line_tokens("left", 10.0, 30.0), *line_tokens("right", 310.0, 30.0)])],
         "edges line up within half a line height")
add_cuts("gutter_char_tokens",
         [list(col_left[0]), list(col_left[1]), list(col_left[2]),
          list(col_right[0]), list(col_right[1]), list(col_right[2]), list(wide_row)],
         [("left one", 1.0, [("l", 10.0), ("e", 40.0), ("f", 70.0), ("t", 100.0)]),
          ("left two", 1.0, [("l", 10.0), ("e", 40.0), ("f", 70.0), ("t", 100.0)]),
          ("left three", 1.0, [("l", 10.0), ("e", 40.0), ("f", 70.0), ("t", 100.0)]),
          ("right one", 1.0, [("r", 310.0), ("i", 340.0), ("g", 370.0), ("h", 400.0)]),
          ("right two", 1.0, [("r", 310.0), ("i", 340.0), ("g", 370.0), ("h", 400.0)]),
          ("right three", 1.0, [("r", 310.0), ("i", 340.0), ("g", 370.0), ("h", 400.0)]),
          ("left right", 1.0, [("l", 10.0), ("e", 40.0), ("f", 70.0), ("t", 100.0),
                               ("r", 310.0), ("i", 340.0), ("g", 370.0), ("h", 400.0)])],
         "char tokens, same edges")


def add_backtracks(name, boxes, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    backtrack_cases.append({"name": name, "n": int(len(b)), "boxes": f32(b),
                            "expected": int(order_backtracks(b)), "note": note})


def column(n, x0, y0=0.0, w=100.0, h=30.0, step=40.0):
    return [[x0, y0 + i * step, x0 + w, y0 + i * step + h] for i in range(n)]


add_backtracks("empty", np.zeros((0, 4), np.float32))
add_backtracks("two", np.array(column(2, 0.0), np.float32))
add_backtracks("same_column", np.array(column(5, 0.0), np.float32), "down a column: none")
add_backtracks("two_columns",
               np.array(column(4, 0.0) + column(4, 400.0), np.float32),
               "up to the second column: none")
add_backtracks("backtrack",
               np.array(column(3, 0.0) + column(3, 400.0) + column(1, 0.0, y0=0.0), np.float32),
               "the last line jumps back up on the left: one")
add_backtracks("overlap_not_column",
               np.array([[0.0, 0.0, 100.0, 30.0], [50.0, 5.0, 150.0, 35.0]], np.float32),
               "up but horizontally overlapping: not a backtrack")
add_backtracks("two_backtracks",
               np.array([[0.0, 100.0, 100.0, 130.0], [0.0, 0.0, 100.0, 30.0],
                         [0.0, 50.0, 100.0, 80.0], [0.0, 10.0, 100.0, 40.0]], np.float32),
               "down, up, down, up: two backward moves")


def add_coherent(name, boxes, logits=None, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    lg = None if logits is None else np.ascontiguousarray(logits, np.float32)
    coherent_cases.append({
        "name": name, "n": int(len(b)), "boxes": f32(b),
        "logits": None if lg is None else f32(lg),
        "expected": bool(pdf_order_coherent(b, lg)), "note": note})


def pair_matrix(n, positive_frac=None, value=1.0):
    """[n, n] pair logits: positive on (i, i+1) up to ``positive_frac`` of the pairs."""
    a = np.full((n, n), -1.0, np.float32)
    np.fill_diagonal(a, 0.0)
    pairs = n - 1
    good = pairs if positive_frac is None else int(round(pairs * positive_frac))
    for i in range(good):
        a[i, i + 1] = a[i + 1, i] = value
    return a


add_coherent("no_logits_ok", np.array(column(4, 0.0), np.float32), None)
add_coherent("no_logits_backtrack",
             np.array(column(3, 0.0) + column(3, 400.0) + column(1, 0.0), np.float32), None)
add_coherent("logits_all_ok", np.array(column(4, 0.0), np.float32), pair_matrix(4))
add_coherent("logits_just_below", np.array(column(6, 0.0), np.float32), pair_matrix(6, 0.8),
             "4 of 5 pairs positive: 0.8 < 0.85")
add_coherent("logits_at_threshold", np.array(column(21, 0.0), np.float32), pair_matrix(21, 0.85),
             "17 of 20 = 0.85")
add_coherent("logits_zero", np.array(column(4, 0.0), np.float32), pair_matrix(4, value=0.0),
             "0 is not positive")
add_coherent("two_lines", np.array(column(2, 0.0), np.float32), pair_matrix(2),
             "two lines: the model-agreement check never runs")
add_coherent("two_backtracks",
             np.array([[0.0, 100.0, 100.0, 130.0], [0.0, 0.0, 100.0, 30.0],
                       [0.0, 50.0, 100.0, 80.0], [0.0, 10.0, 100.0, 40.0]], np.float32),
             None, "two backward moves: max(1, int(0.02*4)) = 1")
add_coherent("logits_disagree",
             np.array(column(6, 0.0), np.float32), pair_matrix(6, 0.5),
             "2 of 5 pairs positive: 0.4 < 0.85")


# --------------------------------------------------------------------------- assemble

def assemble_case(name, boxes, rec, model: RecordingModel | None, wh,
                  table_heads=False, furniture=False, content_order=False, note=""):
    stub = Stub(None if model is None else model, table_heads, furniture)
    b = np.ascontiguousarray(boxes, np.float32).reshape(-1, 4)
    t: dict = {}
    try:
        page = OrtOCR.assemble(stub, (float(wh[0]), float(wh[1])), b, list(rec), t,
                               content_order=content_order)
    except TypeError:  # 1.1 removed the content-order switch
        page = OrtOCR.assemble(stub, (float(wh[0]), float(wh[1])), b, list(rec), t)
        content_order = False
    assemble_cases.append({
        "name": name, "n": int(len(b)), "page": [float(wh[0]), float(wh[1])],
        "boxes": f32(b), "rec": rec_json(rec),
        "table_heads": bool(table_heads), "furniture": bool(furniture),
        "content_order": bool(content_order),
        "vocab_space": vocab.index.get(" "),
        "model": None if model is None else {"outputs": model.outputs_json()},
        "expected": {
            "lines": [{"box": [float(v) for v in ln.box], "text": ln.text, "conf": float(ln.conf)}
                      for ln in page.lines],
            "text": page.text,
            "markdown": page.markdown,
            "note": page.note,
            "source": page.source,
            "blocks": [{"label": bl["label"], "bbox": [float(v) for v in bl["bbox"]],
                        "text": bl["text"]} for bl in page.blocks],
        },
        "note_text": note,
    })
    return page


order_sess = session() if (MODELS / "order.onnx").exists() else None
rec_doc = json.loads((FIX / "recognize.json").read_text(encoding="utf-8"))
page_wh = {n: (float(p["w"]), float(p["h"])) for n, p in rec_doc["pages"].items()}
real_pages = {}
for case in rec_doc["cases"]:
    name = case["name"]
    raw = np.frombuffer(zlib.decompress(base64.b64decode(case["boxes"])),
                        np.float32).reshape(-1, 5)
    boxes = np.ascontiguousarray(raw[:, :4], np.float32)
    lines = case["lines"]
    rec = []
    for ln in lines:
        toks = []
        for tk in ln["tokens"]:
            toks.append(((tk["char"], tk["x"]) if tk["char"] is not None
                         else (int(tk["id"]), tk["x"])))
        rec.append((ln["text"], float(ln["conf"]), toks))
    real_pages[name] = (boxes, rec, page_wh[case["page"]])
    print(f"  real {name:12s} n={len(boxes):3d}")

if order_sess is not None:
    for name, (boxes, rec, wh) in real_pages.items():
        model = RecordingModel(order_sess)
        assemble_case("real_" + name, boxes, rec, model, wh,
                      note="real detector + recognizer, real order.onnx")
        model_t = RecordingModel(order_sess)
        assemble_case("real_" + name + "_table", boxes, rec, model_t, wh, table_heads=True,
                      note="real page, table heads on")
        model_f = RecordingModel(order_sess)
        assemble_case("real_" + name + "_furniture", boxes, rec, model_f, wh, furniture=True,
                      note="real page, keep_furniture")
else:
    print("  order.onnx missing: the real-page cases are skipped")

# no order model at all
zero_model = None
for name in ("latin", "two_columns", "blank"):
    if name not in real_pages:
        continue
    boxes, rec, wh = real_pages[name]
    # the recognizer's tokens are ids; the char-less path still needs the vocab
    assemble_case("nomodel_" + name, boxes, rec, zero_model, wh,
                  note="no order model: XY-cut, no markdown, no blocks")

# --- synthetic: a table line the table heads split into cells, then the order model runs again
t_boxes = np.array(
    [[0.0, 0.0, 300.0, 600.0],        # 0: not a table
     [0.0, 40.0, 300.0, 60.0],        # 1: not a table
     [0.0, 100.0, 400.0, 130.0],      # 2: a table line, two cells
     [0.0, 140.0, 400.0, 170.0]], np.float32)
t_rec = [
    ("title", 0.9, line_tokens("title", 10.0, 20.0)),
    ("sub", 0.8, line_tokens("sub", 10.0, 20.0)),
    ("A B", 0.95, [*line_tokens("A", 10.0, 20.0), *line_tokens("B", 310.0, 20.0)]),
    ("C D", 0.7, [*line_tokens("C", 10.0, 20.0), *line_tokens("D", 310.0, 20.0)]),
]
fixed = {
    4: fixed_page(4, groups=[[0], [1], [2], [3]],
                  classes=["Text", "Section-Header", "Table", "Table"],
                  rows=[[2, 3]], cols=[[2], [3]], cells=[[2], [3]]),
    # after the split: 4 + 2 = 6 lines
    6: fixed_page(6, groups=[[0], [1], [2], [3], [4], [5]],
                  classes=["Text", "Section-Header", "Table", "Table", "Table", "Table"],
                  rows=[[2, 3], [4, 5]], cols=[[2, 4], [3, 5]],
                  cells=[[2], [3], [4], [5]]),
}
model = RecordingModel(fixed=fixed)
assemble_case("table_cells", t_boxes, t_rec, model, (400.0, 600.0), table_heads=True,
              note="the table heads split cells and the order model runs again")

# --- synthetic: a gutter-cut two-column page
g_boxes = np.array([
    [0.0, 0.0, 100.0, 20.0],
    [0.0, 30.0, 100.0, 50.0],
    [0.0, 60.0, 100.0, 80.0],
    [300.0, 0.0, 400.0, 20.0],
    [300.0, 30.0, 400.0, 50.0],
    [300.0, 60.0, 400.0, 80.0],
    [0.0, 100.0, 400.0, 130.0],
], np.float32)
g_rec = [
    ("left one", 0.9, line_tokens("leftone", 10.0, 30.0)),
    ("left two", 0.9, line_tokens("lefttwo", 10.0, 30.0)),
    ("left three", 0.9, line_tokens("leftthree", 10.0, 10.0)),
    ("right one", 0.9, line_tokens("rightone", 310.0, 30.0)),
    ("right two", 0.9, line_tokens("righttwo", 310.0, 30.0)),
    ("right three", 0.9, line_tokens("rightthree", 310.0, 10.0)),
    ("left right", 0.9, [*line_tokens("left", 10.0, 30.0), *line_tokens("right", 310.0, 30.0)]),
]
model = RecordingModel(fixed={
    7: fixed_page(7, groups=[[i] for i in range(7)], classes=["Text"] * 7),
    8: fixed_page(8, groups=[[i] for i in range(8)], classes=["Text"] * 8),
})
assemble_case("gutter_split", g_boxes, g_rec, model, (400.0, 200.0),
              note="one line is cut at the gutter into two")

# --- synthetic: a wide-gap split with no model
w_boxes = np.array([[0.0, 0.0, 400.0, 30.0]], np.float32)
w_rec = [("A B", 0.95, [*line_tokens("A", 10.0, 20.0), *line_tokens("B", 310.0, 20.0)])]
assemble_case("wide_split_nomodel", w_boxes, w_rec, None, (400.0, 200.0),
              note="SPLIT_GAP cuts the line; no model, one line left")

# --- synthetic: empty and single-line pages
assemble_case("empty", np.zeros((0, 4), np.float32), [], None, (400.0, 200.0),
              note="no lines")
assemble_case("single", np.array([[0.0, 0.0, 100.0, 20.0]], np.float32),
              [("only line", 0.9, line_tokens("onlyline", 10.0, 5.0))], None, (400.0, 200.0),
              note="one line: markdown is the text itself")
assemble_case("single_model", np.array([[0.0, 0.0, 100.0, 20.0]], np.float32),
              [("only line", 0.9, line_tokens("onlyline", 10.0, 5.0))],
              RecordingModel(fixed={1: fixed_page(1, groups=[[0]], classes=["Text"])}),
              (400.0, 200.0), note="one line with a model: the model never runs")


# --- synthetic: text-layer tokens and the content order
def content_page(contents: list[int | None]):
    """Six stacked lines whose tokens carry the given PDF content indices (None: no index)."""
    boxes = np.array([[0.0, 0.0 + i * 40, 200.0, 30.0 + i * 40] for i in range(6)], np.float32)
    rec = []
    for i, content in enumerate(contents):
        toks = [(ch, 10.0 + k * 20.0) if content is None
                else (ch, 10.0 + k * 20.0, content + k)
                for k, ch in enumerate(f"line{i}")]
        rec.append((f"line {i}", 0.9, toks))
    return boxes, rec


content_boxes, content_rec = content_page([0, 10, 20, 30, 40, 50])
assemble_case("content_order_ok", content_boxes, content_rec, None, (400.0, 300.0),
              content_order=True, note="content order == the box order: kept, backtracks 0")
content_boxes2, content_rec2 = content_page([30, 0, 40, 10, 50, 20])
assemble_case("content_order_shuffled", content_boxes2, content_rec2, None, (400.0, 300.0),
              content_order=True, note="a scrambled content order with no backtrack: kept")
content_boxes3, content_rec3 = content_page([50, 40, 30, 20, 10, 0])
assemble_case("content_order_backtrack", content_boxes3, content_rec3, None, (400.0, 300.0),
              content_order=True, note="every move goes up: rejected")
content_boxes4, content_rec4 = content_page([0, None, 20, 30, 40, 50])
assemble_case("content_order_partial", content_boxes4, content_rec4, None, (400.0, 300.0),
              content_order=True,
              note="one line has no content index: the PDF order is not used")
content_boxes5, content_rec5 = content_page([0, 10, 20, 30, 40, 50])
assemble_case("content_order_with_model", content_boxes5, content_rec5,
              RecordingModel(fixed={6: [pair_matrix(6)[None]]}),
              (400.0, 300.0), content_order=True,
              note="the model has no layout heads; its logits agree with the pairs")
content_boxes6, content_rec6 = content_page([0, 10, 20, 30, 40, 50])
assemble_case("content_order_model_disagrees", content_boxes6, content_rec6,
              RecordingModel(fixed={6: [pair_matrix(6, 0.5)[None]]}),
              (400.0, 300.0), content_order=True,
              note="the content order is coherent but the model agrees with only 0.4 of the pairs")
assemble_case("content_order_single", np.array([[0.0, 0.0, 200.0, 30.0]], np.float32),
              [("only", 0.9, [("o", 10.0, 0), ("n", 30.0, 1), ("l", 50.0, 2), ("y", 70.0, 3)])],
              None, (400.0, 300.0), content_order=True,
              note="one line: the content-order block never runs")

# --------------------------------------------------------------------------- write

doc = {
    "note": "fixtures for pipeline.OrtOCR.assemble (step 130); arrays are zlib+base64 of raw "
            "little-endian bytes (see tools/scripts/test/gen_assemble_fixtures.py)",
    "block_classes": list(BLOCK_CLASSES),
    "vocab": list(vocab.chars),
    "space_id": vocab.index.get(" "),
    "is_space": space_cases,
    "decode": decode_cases,
    "split_line": split_cases,
    "gutter_cuts": cut_cases,
    "order_backtracks": backtrack_cases,
    "pdf_order_coherent": coherent_cases,
    "assemble": assemble_cases,
}
out = FIX / "assemble.json"
out.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {out.name}: {out.stat().st_size} bytes ({len(space_cases)} is_space, "
      f"{len(decode_cases)} decode, {len(split_cases)} split_line, {len(cut_cases)} gutter_cuts, "
      f"{len(backtrack_cases)} order_backtracks, {len(coherent_cases)} pdf_order_coherent, "
      f"{len(assemble_cases)} assemble cases)")
for case in assemble_cases:
    if case["model"] is not None and not case["model"]["outputs"]:
        print(f"    ! {case['name']}: the model was never called")
