"""Generate src/Indra.Ocr.Tests/fixtures/layout_post.json (and the extra predicate ranges in
unicode_predicates.json).

Parity fixtures for ``indra_ocr.layout_post`` (step 120) - see docs/120-layout-post.md:

* ``round``          - every Python ``round(x, digits)`` case the port's exact implementation has
                       to reproduce (inputs and outputs as raw ieee754 bit patterns, so ``-0.0``
                       and ``nan`` survive JSON);
* ``join_lines``     - hyphen and CJK joins, Python ``str.strip()`` and the code-point indexing;
* ``balance_math``   - the ``$`` windows of the recognizer's math spans;
* ``split_blocks``   - the same-block runs;
* ``rows``           - the geometric row grouping (the one place with numpy's ``argsort`` tie
                       order, which the port documents as a stable sort - the generator replays
                       each case with a stable argsort and only keeps the ones that agree);
* ``merge_wrapped``  - the wrapped-cell row merge;
* ``table_html``     - the geometric tables (title rows, bands, colspan, markup escaping);
* ``heads_plausible`` / ``table_html_heads`` - the OrderNet table heads;
* ``page_blocks`` / ``page_markdown`` - the page-level output, over synthetic pages that hit
                       every block class (including ``cells`` and the display-equation path) and
                       over the real pages of ``recognize.json`` driven by the real ``order.onnx``
                       (through the real ``OrtOCR.reading_order``).

Run:  tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_layout_fixtures.py
"""

from __future__ import annotations

import base64
import json
import math
import struct
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
from indra_ocr.layout_post import (  # noqa: E402
    balance_math,
    heads_plausible,
    join_lines,
    page_blocks,
    page_markdown,
    split_blocks,
    table_html,
    table_html_heads,
)
from indra_ocr.layout_post import _merge_wrapped, _rows  # noqa: E402

MODELS = ROOT / "resources" / "models"
FIX = fixtures_dir()
f32_ = np.float32


def z(a) -> str:
    return base64.b64encode(zlib.compress(np.ascontiguousarray(a).tobytes(), 9)).decode("ascii")


def f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32))


def f64bits(x: float) -> str:
    return struct.pack("<d", float(x)).hex()


def session(name: str) -> ort.InferenceSession:
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1
    so.inter_op_num_threads = 1
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.enable_cpu_mem_arena = False
    return ort.InferenceSession(str(MODELS / name), so, providers=["CPUExecutionProvider"])


# --------------------------------------------------------------------------- portable replay

def stable_argsort(a) -> list:
    """The port's ``Numerics.ArgSort``: a stable argsort (the documented deviation)."""
    return sorted(range(len(a)), key=lambda i: (float(a[i]), i))


def portable_rows(boxes) -> list:
    """``_rows`` with a stable argsort, i.e. what the C# port computes."""
    b = np.asarray(boxes, np.float32)
    cy = (b[:, 1] + b[:, 3]) / 2
    h = float(np.median(b[:, 3] - b[:, 1]))
    rows: list = []
    for i in stable_argsort(cy):
        if rows and abs(cy[i] - np.mean(cy[rows[-1]])) < 0.5 * h:
            rows[-1].append(int(i))
        else:
            rows.append([int(i)])
    return [sorted(r, key=lambda k: b[k, 0]) for r in rows]


stats = {"rows": 0, "rows_differ": 0, "skipped": []}


def rows_case(name, boxes):
    """``_rows`` fixture; returns None when the reference's argsort tie order is not stable."""
    b = np.ascontiguousarray(boxes, np.float32)
    expected = [[int(v) for v in r] for r in _rows(b)]
    stats["rows"] += 1
    if portable_rows(b) != expected:
        stats["rows_differ"] += 1
        stats["skipped"].append(name)
        print(f"    ! {name}: _rows relies on numpy's argsort tie order, skipping")
        return None
    rows_cases.append({"name": name, "n": int(len(b)), "boxes": f32(b), "expected": expected})
    return expected


# --------------------------------------------------------------------------- cases

round_cases: list = []
join_cases: list = []
math_cases: list = []
split_cases: list = []
rows_cases: list = []
merge_cases: list = []
table_cases: list = []
head_ok_cases: list = []
head_table_cases: list = []
blocks_cases: list = []
markdown_cases: list = []


def add_join(name, texts, note=""):
    join_cases.append({"name": name, "texts": list(texts),
                       "expected": join_lines(list(texts)), "note": note})


def add_math(name, text, note=""):
    math_cases.append({"name": name, "text": text, "expected": balance_math(text), "note": note})


def add_split(name, order, same, note=""):
    s = np.ascontiguousarray(same, np.float32)
    split_cases.append({"name": name, "n": int(s.shape[0]), "order": [int(v) for v in order],
                        "same": f32(s),
                        "expected": [[int(v) for v in b] for b in split_blocks(list(order), s)],
                        "note": note})


def add_merge(name, rows, boxes, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    merge_cases.append({"name": name, "n": int(len(b)), "boxes": f32(b),
                        "rows": [[int(v) for v in r] for r in rows],
                        "expected": [[int(v) for v in r] for r in _merge_wrapped(rows, b)],
                        "note": note})


def add_table(name, boxes, texts, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    expected = table_html(b, list(texts))
    table_cases.append({"name": name, "n": int(len(b)), "boxes": f32(b), "texts": list(texts),
                        "expected": expected, "note": note})
    return expected


def heads_tab(n, same_cell, same_row, same_col):
    """A ``[3, n, n]`` table-head slice: 0 same row, 1 same column, 2 same cell."""
    tab = np.zeros((3, n, n), np.float32)
    for k, mat in enumerate((same_row, same_col, same_cell)):
        for i in range(n):
            for j in range(n):
                if i != j:
                    tab[k, i, j] = mat(i, j)
    return tab


def add_heads(name, boxes, texts, tab, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    t = np.ascontiguousarray(tab, np.float32)
    head_ok_cases.append({"name": name, "n": int(len(b)), "boxes": f32(b), "tab": f32(t),
                          "expected": bool(heads_plausible(b, t)), "note": note})
    head_table_cases.append({"name": name, "n": int(len(b)), "boxes": f32(b), "texts": list(texts),
                             "tab": f32(t), "expected": table_html_heads(b, list(texts), t),
                             "note": note})


def cells_json(cells):
    if cells is None:
        return None
    out = []
    for line in sorted(cells):
        items = cells[line]
        out.append({"line": int(line),
                    "boxes": f32(np.array([it[0] for it in items], np.float32).reshape(-1, 4)),
                    "texts": [it[1] for it in items]})
    return out


def add_page(name, boxes, texts, order, cls, same, keep=False, cells=None, tab=None, note=""):
    b = np.ascontiguousarray(boxes, np.float32)
    c = np.ascontiguousarray(cls, np.float32)
    s = np.ascontiguousarray(same, np.float32)
    t = None if tab is None else np.ascontiguousarray(tab, np.float32)
    fields = {"name": name, "n": int(len(b)), "boxes": f32(b), "texts": list(texts),
              "order": [int(v) for v in order], "cls": f32(c), "same": f32(s),
              "keep_furniture": bool(keep), "cells": cells_json(cells),
              "tab": None if t is None else f32(t),
              "expected": page_markdown(b, list(texts), list(order), c, s, keep_furniture=keep,
                                        cells=cells, tab=t),
              "note": note}
    blocks = page_blocks(b, list(texts), list(order), c, s)
    fields["expected_blocks"] = [{"label": bl["label"], "bbox": bl["bbox"], "text": bl["text"]}
                                 for bl in blocks]
    markdown_cases.append(fields)


# --------------------------------------------------------------------------- round

rng = np.random.default_rng(120)
round_vals = [float(v) for v in rng.uniform(-2000, 2000, 300)]
round_vals += [float(v) for v in rng.uniform(-1, 1, 200)]
round_vals += [float(np.float32(v)) for v in rng.uniform(-500, 500, 200)]
round_vals += [
    0.0, -0.0, 0.05, -0.05, 0.15, -0.15, 0.25, 0.35, -0.01, 0.5, 1.5, 2.5, -2.5, 0.125, 0.375,
    123.45, 1234.5678, 2.675, 1.005, 1e-5, -1e-5, 1e6, -1e6, 1e15, -1e15, 1e308, -1e308,
    5e-324, -5e-324, 1.7976931348623157e308, float("inf"), float("-inf"), float("nan"),
]
for digits in (0, 1, 2, 3, 4):
    for x in round_vals:
        round_cases.append({"x": f64bits(x), "digits": digits,
                            "expected": f64bits(round(x, digits))})
print(f"  round: {len(round_cases)} cases")

# --------------------------------------------------------------------------- join_lines

add_join("empty", [])
add_join("blank", ["", "   ", "\t\n"])
add_join("hyphen", ["tumori-", "genesis"])
add_join("hyphen_upper", ["tumori-", "Genesis"])          # t[0] not lower: a space is kept
add_join("hyphen_dash_only", ["-", "text"])
add_join("hyphen_two", ["a-b", "cd"])
add_join("hyphen_two_upper", ["Ab-", "cd"])               # out[-2] not alpha
add_join("hyphen_space", ["word- ", " next"])
add_join("cjk", ["\u4e00\u4e8c", "\u4e09\u56db"])
add_join("cjk_kana", ["\u3042\u3044", "\u3046\u3048"])
add_join("cjk_fullwidth", ["\uff21", "\uff22"])
add_join("cjk_astral", ["\U00020000", "\U00020001"])
add_join("cjk_latin", ["\u4e00", "abc"])                   # latin does not join without a space
add_join("korean", ["\ud55c\uad6d", "\uc5b4"])             # Hangul is not in the CJK ranges
add_join("strip_python_space", ["\x1cinside\x1c", "\x1f more \x1f"])
add_join("strip_nbsp", ["\xa0text\xa0"])
add_join("strip_nel", ["\x85text\x85"])
add_join("astral_first", ["\U0001f600 text", "second"])
add_join("padded", ["  padded  ", "  lines  "])
add_join("many", ["one", "two", "three", "four"])
add_join("one_hyphen_then_cjk", ["a-", "\u4e00\u4e8c"])

# --------------------------------------------------------------------------- balance_math

add_math("plain", "no dollar here")
add_math("price", "The price is $5 per item")
add_math("price_followed", "costs $5 and $7 total")
add_math("latex", "a $x^2$ b")
add_math("open_latex", "open $x_1")
add_math("open_end", "trailing $x")
add_math("plain_word_close", "$a plus some word here")
add_math("plain_word_short", "$a xb cdef")
add_math("underscore_close", "$a plus_word here")
add_math("brace_close", "$a plus{word here")
add_math("paren_close", "$a plus(word here")
add_math("caret_close", "$a plus^word here")
add_math("word_too_short", "$a bc def")
add_math("word_then_dollar", "$x abc $y$")
add_math("two_spans", "$a_1$ and $b^2$")
add_math("adjacent", "$$x$$")
add_math("dollar_dollar", "$a$$b$")
add_math("astral_math", "$a \U0001f600^2$ tail")
add_math("astral_word", "$a \U0001f600 plus word")
add_math("only_dollar", "$")
add_math("far_dollar", "$a" + "b" * 20 + "$ tail")
add_math("near_dollar", "$a" + "b" * 10 + "$ tail")
add_math("boundary_dollar", "$a" + "b" * 14 + "$ tail")
add_math("boundary_dollar_over", "$a" + "b" * 15 + "$ tail")
add_math("combining", "$a \u0301word here")

# --------------------------------------------------------------------------- split_blocks / rows


def same_block(n, groups):
    """A ``same`` matrix whose consecutive pairs are positive inside each group."""
    s = np.full((n, n), -1.0, np.float32)
    np.fill_diagonal(s, 0.0)
    for g in groups:
        for a, b in zip(g, g[1:]):
            s[a, b] = s[b, a] = 1.0
    return s


add_split("empty", [], np.zeros((0, 0), np.float32))
add_split("single", [0], same_block(1, [[0]]))
add_split("one_block", [0, 1, 2, 3], same_block(4, [[0, 1, 2, 3]]))
add_split("all_separate", [0, 1, 2, 3], same_block(4, []))
add_split("two_blocks", [0, 1, 2, 3], same_block(4, [[0, 1], [2, 3]]))
add_split("order_shuffled", [3, 1, 0, 2], same_block(4, [[3, 1], [0, 2]]))
add_split("zero_logit", [0, 1], np.array([[0.0, 0.0], [0.0, 0.0]], np.float32))
add_split("reversed_order", [2, 1, 0], same_block(3, [[2, 1, 0]]))

# --------------------------------------------------------------------------- rows

two_columns = [[60.0, 100.0 + i * 50, 280.0, 140.0 + i * 50] for i in range(4)]
two_columns += [[360.0, 100.0 + i * 50, 620.0, 140.0 + i * 50] for i in range(4)]

rows_case("empty", np.zeros((0, 4), np.float32))
rows_case("single", np.array([[10.0, 10.0, 100.0, 40.0]], np.float32))
rows_case("two_columns", np.array(two_columns, np.float32))
rows_case("grid", np.array(
    [[60.0 + c * 200, 80.0 + r * 120, 220.0 + c * 200, 140.0 + r * 120]
     for r in range(3) for c in range(3)], np.float32))
rows_case("stacked_lines", np.array(
    [[50.0, 100.0, 200.0, 130.0], [50.0, 132.0, 200.0, 162.0], [50.0, 164.0, 200.0, 194.0],
     [300.0, 100.0, 450.0, 130.0]], np.float32))
rows_case("overlapping", np.array(
    [[0.0, 0.0, 100.0, 100.0], [20.0, 20.0, 120.0, 120.0], [40.0, 40.0, 140.0, 140.0]],
    np.float32))
rows_case("unsorted_x", np.array(
    [[300.0, 100.0, 400.0, 130.0], [100.0, 100.0, 200.0, 130.0],
     [200.0, 100.0, 300.0, 130.0]], np.float32))
rows_case("jitter", np.array(
    [[50.0 + c * 180, 100.0 + r * 60 + j, 200.0 + c * 180, 134.0 + r * 60 + j]
     for r, j in enumerate((-8.0, -7.0, 1.0, 3.0)) for c in range(3)], np.float32))
rows_case("tall_cell", np.array(
    [[50.0, 100.0, 200.0, 200.0], [250.0, 100.0, 400.0, 130.0]], np.float32))
rows_case("flat", np.array(
    [[0.0, 0.0, 100.0, 1.0], [0.0, 2.0, 100.0, 3.0]], np.float32))

# --------------------------------------------------------------------------- merge_wrapped

wrapped = np.array(
    [[50.0, 100.0, 200.0, 130.0],
     [50.0, 132.0, 200.0, 162.0],
     [250.0, 100.0, 400.0, 130.0],
     [250.0, 132.0, 400.0, 162.0],
     [50.0, 200.0, 200.0, 230.0],
     [250.0, 200.0, 400.0, 230.0],
     [50.0, 300.0, 200.0, 330.0],
     [250.0, 300.0, 400.0, 330.0]], np.float32)
add_merge("few_rows", [[0], [1]], wrapped)
add_merge("three_rows", [[0], [1], [2]], wrapped)
add_merge("wrapped_pair", _rows(wrapped), wrapped)
add_merge("no_merge", [[0, 1], [2], [3]], wrapped)
add_merge("even_gap", [[0], [1], [2]], np.array(
    [[0.0, 0.0, 100.0, 30.0], [0.0, 30.0, 100.0, 60.0], [0.0, 60.0, 100.0, 90.0]],
    np.float32))

# --------------------------------------------------------------------------- table_html

# a two-column table with a title row
title_table = np.array(
    [[40.0, 40.0, 400.0, 70.0],
     [40.0, 100.0, 180.0, 130.0],
     [240.0, 100.0, 400.0, 130.0],
     [40.0, 140.0, 180.0, 170.0],
     [240.0, 140.0, 400.0, 170.0]], np.float32)
add_table("title_row", title_table, ["Table 1 & <chart>", "Head \"A\"", "Head 'B'",
                                     "cell & co", "x > y"],
          "first row spans the table: title text, then <th>")
add_table("no_title", title_table[1:], ["A", "B", "C", "D"], "no single-cell first row")
add_table("single_row", title_table[1:3], ["A", "B"], "one row only: <td>")
add_table("ragged", np.array(
    [[40.0, 40.0, 180.0, 70.0], [240.0, 40.0, 400.0, 70.0],
     [40.0, 90.0, 400.0, 120.0],
     [40.0, 140.0, 180.0, 170.0], [240.0, 140.0, 400.0, 170.0]], np.float32),
    ["A", "B", "spans", "C", "D"], "a row with one wide cell")
add_table("wrapped", wrapped, [f"c{i}" for i in range(len(wrapped))],
          "stacked cells merge into one row")
add_table("escaping", np.array(
    [[0.0, 0.0, 100.0, 30.0], [200.0, 0.0, 300.0, 30.0]], np.float32),
    ["a & b <i> 'q' \"d\"", "5 > 3"], "html.escape(quote=False)")
add_table("empty_texts", np.array(
    [[0.0, 0.0, 100.0, 30.0], [200.0, 0.0, 300.0, 30.0]], np.float32), ["", "b"],
    "an empty cell text")
add_table("wide_cell", np.array(
    [[0.0, 0.0, 300.0, 30.0], [0.0, 40.0, 100.0, 70.0], [200.0, 40.0, 300.0, 70.0]],
    np.float32), ["wide", "a", "b"], "first row spans both bands")

# --------------------------------------------------------------------------- table heads

grid2x2 = np.array(
    [[50.0, 100.0, 250.0, 130.0],
     [300.0, 100.0, 500.0, 130.0],
     [50.0, 200.0, 250.0, 230.0],
     [300.0, 200.0, 500.0, 230.0]], np.float32)


def heads(same_cell, same_row, same_col, n=4):
    return heads_tab(n, same_cell, same_row, same_col)


add_heads("grid2x2",
          grid2x2, ["a", "b", "c", "d"],
          heads(lambda i, j: -5.0,
                 lambda i, j: 5.0 if i // 2 == j // 2 else -5.0,
                 lambda i, j: 5.0 if i % 2 == j % 2 else -5.0),
          "one line per cell: 2x2 with a header row")
add_heads("grid2x2_noheads",
          grid2x2, ["a", "b", "c", "d"],
          heads(lambda i, j: -5.0,
                 lambda i, j: -5.0,
                 lambda i, j: -5.0),
          "no positive logits at all: every cell its own row/col")

# a wrapped cell: lines 0 and 1 are stacked in the wide first cell, 2 and 3 below
wrapped_table = np.array(
    [[50.0, 100.0, 500.0, 130.0],
     [50.0, 132.0, 500.0, 162.0],
     [50.0, 200.0, 250.0, 230.0],
     [300.0, 200.0, 500.0, 230.0]], np.float32)
add_heads("wrapped_cell",
          wrapped_table, ["wide one", "wide two", "left", "right"],
          heads(lambda i, j: 5.0 if (i, j) in ((0, 1), (2, 3), (1, 2), (1, 3)) else -5.0,
                 lambda i, j: 5.0 if i // 2 == j // 2 else -5.0,
                 lambda i, j: 5.0 if i % 2 == j % 2 else -5.0),
          "the first cell holds two stacked lines and spans two columns")
add_heads("side_by_side_bad",
          grid2x2, ["a", "b", "c", "d"],
          heads(lambda i, j: 5.0 if (i, j) in ((0, 1), (1, 0), (2, 3), (3, 2)) else -5.0,
                 lambda i, j: 5.0 if i // 2 == j // 2 else -5.0,
                 lambda i, j: 5.0 if i % 2 == j % 2 else -5.0),
          "the cell head joins side-by-side lines: not plausible")

# --------------------------------------------------------------------------- synthetic pages

HEADER, SECTION, LIST, TABLE = 2, 1, 4, 5
FOOTER = 3


def page_case(name, note=""):
    """A page with one of every block class, then the same page through the table heads."""
    boxes = np.array(
        [[60.0, 40.0, 640.0, 70.0],        # 0  page header
         [60.0, 100.0, 400.0, 130.0],      # 1  section header
         [60.0, 160.0, 640.0, 190.0],      # 2  paragraph, hyphen wrap
         [60.0, 192.0, 300.0, 222.0],      # 3
         [60.0, 250.0, 640.0, 280.0],      # 4  list item 1
         [60.0, 282.0, 640.0, 312.0],      # 5  list item 1 continued
         [60.0, 320.0, 640.0, 350.0],      # 6  list item 2
         [60.0, 400.0, 300.0, 430.0],      # 7  table: cells A B
         [360.0, 400.0, 640.0, 430.0],
         [60.0, 440.0, 300.0, 470.0],      # 9  table: cells C D
         [360.0, 440.0, 640.0, 470.0],
         [60.0, 520.0, 400.0, 550.0],      # 11 display equation
         [60.0, 600.0, 640.0, 630.0]],     # 12 footer
        np.float32)
    texts = ["Quarterly report", "1 Introduction",
             "This paragraph ends with a hyphen-", "ated word in the middle.",
             "\u2022 first item", "carried on", "2) second item",
             "A", "B", "C", "D", "$E = mc^2$", "Draft - page 1"]
    n = len(boxes)
    groups = [[0], [1], [2, 3], [4, 5, 6], [7, 8, 9, 10], [11], [12]]
    classes = [[HEADER], [SECTION], [0], [LIST], [TABLE], [0], [FOOTER]]
    labels = [g[0] for g in classes]
    same = np.full((n, n), -1.0, np.float32)
    np.fill_diagonal(same, 0.0)
    for g in groups:
        for a, b in zip(g, g[1:]):
            same[a, b] = same[b, a] = 1.0
    cls = np.zeros((n, 10), np.float32)
    for g, lab in zip(groups, labels):
        for i in g:
            cls[i, lab] = 1.0
    order = list(range(n))
    cells = {
        7: [(boxes[7], "A")], 8: [(boxes[8], "B")],
        9: [(boxes[9], "C")], 10: [(boxes[10], "D")],
    }
    add_page(name, boxes, texts, order, cls, same, note=note)
    add_page(name + "_keep", boxes, texts, order, cls, same, keep=True,
             note=note + " (keep_furniture)")
    add_page(name + "_cells", boxes, texts, order, cls, same, cells=cells,
             note=note + " (geometric table from the cells map)")
    tab = heads_tab(n, lambda i, j: -5.0,
                    lambda i, j: 5.0 if (i, j) in ((7, 8), (9, 10)) else -5.0,
                    lambda i, j: 5.0 if (i, j) in ((7, 9), (8, 10)) else -5.0)
    add_page(name + "_tab", boxes, texts, order, cls, same, cells=cells, tab=tab,
             note=note + " (table heads)")


page_case("classes", "one block per layout class")

# list markers and the display-equation variants
marker_boxes = np.array([[60.0, 100.0 + i * 34, 640.0, 130.0 + i * 34] for i in range(9)],
                        np.float32)
marker_texts = ["\u2022 bullet", "continued", "1. one", "\u2192 arrow", "a) lettered",
                "\u0661\u0662. arabic-indic", "\u4e00\u4e8c. hanzi", "\x0cf form feed", "plain"]
n = len(marker_boxes)
cls = np.zeros((n, 10), np.float32)
cls[:, LIST] = 1.0
same = np.full((n, n), -1.0, np.float32)
np.fill_diagonal(same, 0.0)
add_page("list_markers", marker_boxes, marker_texts, list(range(n)), cls, same,
         note="every _ITEM marker form, plus one continuation line")

math_boxes = np.array([[60.0, 100.0, 400.0, 130.0], [60.0, 140.0, 400.0, 170.0],
                       [60.0, 200.0, 400.0, 230.0]], np.float32)
math_texts = ["$E = mc^2$", "$  \\frac{1}{2} $", "$a + b$"]
n = len(math_boxes)
cls = np.zeros((n, 10), np.float32)
cls[:, 0] = 1.0
same = np.full((n, n), -1.0, np.float32)
np.fill_diagonal(same, 0.0)
add_page("whole_math", math_boxes, math_texts, list(range(n)), cls, same,
         note="every line a whole formula: $$..$$")

bad_math = ["$E = mc^2$ and more", "$5 per item", "$a", "$b$"]
bad_boxes = np.array([[60.0, 100.0 + i * 34, 640.0, 130.0 + i * 34] for i in range(4)], np.float32)
n = len(bad_boxes)
cls = np.zeros((n, 10), np.float32)
same = np.full((n, n), -1.0, np.float32)
np.fill_diagonal(same, 0.0)
for i in range(n - 1):
    same[i, i + 1] = same[i + 1, i] = 1.0
add_page("mixed_math", bad_boxes, bad_math, list(range(n)), cls, same,
         note="one formula plus text: joined as a paragraph")

add_page("empty", np.zeros((0, 4), np.float32), [], [], np.zeros((0, 10), np.float32),
         np.zeros((0, 0), np.float32), note="no lines at all")

# the same-block split puts two far-apart lines in one block: reading order is taken as given
far_boxes = np.array([[60.0, 100.0, 640.0, 130.0], [60.0, 800.0, 640.0, 830.0]], np.float32)
n = len(far_boxes)
cls = np.zeros((n, 10), np.float32)
same = np.full((n, n), 1.0, np.float32)
np.fill_diagonal(same, 0.0)
add_page("far_lines", far_boxes, ["first part", "second part"], list(range(n)), cls, same,
         note="one block over two distant lines")

# --------------------------------------------------------------------------- real pages

order_sess = None
if (MODELS / "order.onnx").exists() and (FIX / "recognize.json").exists():
    from indra_ocr.pipeline import OrtOCR

    class OrderStub:
        """Only the attributes ``OrtOCR.reading_order`` touches (step 115)."""

        def __init__(self, order, table_heads=False):
            self.order = order
            self.table_heads = table_heads

    order_sess = session("order.onnx")
    rec_doc = json.loads((FIX / "recognize.json").read_text(encoding="utf-8"))
    page_wh = {n: (float(p["w"]), float(p["h"])) for n, p in rec_doc["pages"].items()}
    for case in rec_doc["cases"]:
        name = case["name"]
        raw = np.frombuffer(zlib.decompress(base64.b64decode(case["boxes"])),
                            np.float32).reshape(-1, 5)
        boxes = np.ascontiguousarray(raw[:, :4], np.float32)
        texts = [ln["text"] for ln in case["lines"]]
        wh = page_wh[case["page"]]
        print(f"  {name:14s} n={len(boxes):3d}")
        order, layout, _ = OrtOCR.reading_order(OrderStub(order_sess), boxes, texts,
                                                (int(wh[0]), int(wh[1])))
        if layout is None:
            print(f"    {name}: short page, no layout: skipped")
            continue
        cls, same, _ = layout
        add_page("real_" + name, boxes, texts, order, cls, same,
                 note="real detector + recognizer + order.onnx")
        add_page("real_" + name + "_keep", boxes, texts, order, cls, same, keep=True,
                 note="real page, keep_furniture")
        order_t, layout_t, _ = OrtOCR.reading_order(OrderStub(order_sess, True), boxes, texts,
                                                    (int(wh[0]), int(wh[1])))
        cls_t, same_t, tab_t = layout_t
        add_page("real_" + name + "_tab", boxes, texts, order_t, cls_t, same_t, tab=tab_t,
                 note="real page with the model's table heads")
        if len(boxes) > 1 and tab_t is not None:
            add_heads("real_" + name, boxes, texts, tab_t,
                      "the real table heads over the whole page")
        else:
            add_table("real_" + name, boxes, texts, "one line / no table heads")
else:
    print("  order.onnx / recognize.json missing: real pages skipped")

# --------------------------------------------------------------------------- write

doc = {
    "note": "fixtures for indra_ocr.layout_post; arrays are zlib+base64 of raw little-endian "
            "bytes, floats in the round section are ieee754 bit patterns "
            "(see tools/scripts/test/gen_layout_fixtures.py)",
    "block_classes": ["Text"],   # placeholder, replaced below
    "round": round_cases,
    "join_lines": join_cases,
    "balance_math": math_cases,
    "split_blocks": split_cases,
    "rows": rows_cases,
    "merge_wrapped": merge_cases,
    "table_html": table_cases,
    "heads_plausible": head_ok_cases,
    "table_html_heads": head_table_cases,
    "page_markdown": markdown_cases,
    "stats": stats,
}
from indra_ocr.order_post import BLOCK_CLASSES  # noqa: E402

doc["block_classes"] = list(BLOCK_CLASSES)


def decode(blob: str) -> np.ndarray:
    return np.frombuffer(zlib.decompress(base64.b64decode(blob)), np.float32)


def verify_rows_deviation() -> list:
    """Recompute every table/page output with a *stable* ``_rows`` argsort.

    ``_rows`` is the one place where the reference inherits numpy's ``argsort`` tie order, which
    the port replaces with a stable sort; anything listed here depends on that tie order and would
    not be reproducible (the fixtures must stay clean).
    """
    import indra_ocr.layout_post as lp

    original = lp._rows
    lp._rows = portable_rows
    differ = []
    try:
        for case in table_cases:
            n = case["n"]
            b = np.ascontiguousarray(decode(case["boxes"]).reshape(n, 4), np.float32)
            if lp.table_html(b, case["texts"]) != case["expected"]:
                differ.append("table_html:" + case["name"])
        for case in markdown_cases:
            n = case["n"]
            b = np.ascontiguousarray(decode(case["boxes"]).reshape(n, 4), np.float32)
            cls = np.ascontiguousarray(decode(case["cls"]).reshape(n, 10), np.float32)
            same = np.ascontiguousarray(decode(case["same"]).reshape(n, n), np.float32)
            tab = None if case["tab"] is None else np.ascontiguousarray(
                decode(case["tab"]).reshape(3, n, n), np.float32)
            cells = None
            if case["cells"] is not None:
                cells = {}
                for item in case["cells"]:
                    boxes = decode(item["boxes"]).reshape(-1, 4)
                    cells[item["line"]] = list(zip(boxes, item["texts"]))
            got = lp.page_markdown(b, case["texts"], case["order"], cls, same,
                                   keep_furniture=case["keep_furniture"], cells=cells, tab=tab)
            if got != case["expected"]:
                differ.append("page_markdown:" + case["name"])
    finally:
        lp._rows = original
    return differ


differ = verify_rows_deviation()
stats["rows_dependent"] = differ
for name in differ:
    print(f"    ! {name}: depends on numpy's argsort tie order")
print(f"  stable-argsort replay of _rows changes {len(differ)}/{len(table_cases) + len(markdown_cases)} "
      f"table/page outputs")

out = FIX / "layout_post.json"
out.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"  rows: portable (stable argsort) replay differs in {stats['rows_differ']}/{stats['rows']} "
      f"cases")
print(f"wrote {out.name}: {out.stat().st_size} bytes ({len(round_cases)} round, "
      f"{len(join_cases)} join_lines, {len(math_cases)} balance_math, "
      f"{len(split_cases)} split_blocks, {len(rows_cases)} rows, {len(merge_cases)} merge_wrapped, "
      f"{len(table_cases)} table_html, {len(head_ok_cases)} heads_plausible, "
      f"{len(head_table_cases)} table_html_heads, {len(markdown_cases)} page_markdown cases)")

# --------------------------------------------------------------------------- predicate ranges

if "--predicates" in sys.argv:
    alpha, lower, digit, upper, space = [], [], [], [], []
    for cp in range(sys.maxunicode + 1):
        ch = chr(cp)
        if ch.isalpha():
            alpha.append(cp)
        if ch.islower():
            lower.append(cp)
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
           "digit": ranges(digit), "upper": ranges(upper), "space": ranges(space),
           "lower": ranges(lower), "alpha": ranges(alpha)}
    uni_path = FIX / "unicode_predicates.json"
    uni_path.write_text(json.dumps(uni, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {uni_path.name}: digit {len(digit)} cps, upper {len(upper)}, space {len(space)}, "
          f"lower {len(lower)}, alpha {len(alpha)}")
