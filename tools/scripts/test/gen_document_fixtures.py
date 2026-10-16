"""Generate src/Indra.Ocr.Tests/fixtures/document.json — the parity fixture for step 160
(``document.Converter``: the trusted text layer, the OCR fallback, and the file plumbing).

Three groups:

* the pure policy functions — ``page_scale``, ``right_to_left``, ``assign_runs`` (and
  ``readable``, which needs the real vocabulary);
* ``convert`` over the sample PDFs of :mod:`pdf_samples` (every one of them, through the
  modes, the hybrid path and the content order), recording each yielded page exactly as
  step 155 does, plus the two ``InputError`` messages;
* ``convert`` over one example image, i.e. the non-PDF path.

The sample PDFs are recorded in the fixture (base64), so the C# test needs no Python and no
file: it writes the bytes to a temp file and drives the port's ``Converter`` with the host
decoder and ``Indra.Ocr.Pdf``.  The example image is external material, so its path and
sha256 are recorded instead (the test skips when it is absent).

Run with the reference environment:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_document_fixtures.py
"""

from __future__ import annotations

import base64
import hashlib
import json
import sys
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "resources" / "indra-src"))

import numpy as np  # noqa: E402
from indra_ocr.document import Converter, assign_runs, readable, right_to_left  # noqa: E402
from indra_ocr.pipeline import OrtOCR  # noqa: E402
from indra_ocr.textlayer import TextLine  # noqa: E402

from pdf_samples import build_all  # noqa: E402

MODELS = ROOT / "resources" / "models"
EXAMPLES = ROOT / "resources" / "indra-src" / "assets" / "examples"
OUT = ROOT / "src" / "Indra.Ocr.Tests" / "fixtures" / "document.json"
WORK = ROOT / "temp" / "document_samples"


def z(raw: bytes) -> str:
    return base64.b64encode(zlib.compress(raw, 9)).decode("ascii")


def z_f32(a) -> str:
    return z(np.ascontiguousarray(a, np.float32).tobytes())


def sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


# ---------------------------------------------------------------------------- pure: page_scale


class FakePage:
    """Only ``get_size`` — what ``page_scale`` reads."""

    def __init__(self, w: float, h: float):
        self._size = (w, h)

    def get_size(self):
        return self._size


page_scale_cases = []
for w, h, dpi in [
    (200.0, 120.0, 200.0),
    (612.0, 792.0, 200.0),  # letter
    (200.5, 120.25, 200.0),  # fractional, as the ``text`` sample's page 2
    (200.0, 120.0, 300.0),
    (200.0, 120.0, 72.0),
    (0.0, 0.0, 200.0),  # a degenerate page: max(w*h, 1.0)
    (4000.0, 4000.0, 200.0),  # the cap starts to bite here
    (5000.0, 5000.0, 200.0),
    (10000.0, 4000.0, 200.0),
    (20000.0, 20000.0, 200.0),
    (100000.0, 100000.0, 200.0),
]:
    conv = Converter(None, dpi=dpi)
    page_scale_cases.append(
        {"w": w, "h": h, "dpi": dpi, "scale": float(conv.page_scale(FakePage(w, h)))}
    )

# ---------------------------------------------------------------------------- pure: bidi


def cps(s: str) -> list[int]:
    return [ord(c) for c in s]


rtl_cases = []
for name, chars in [
    ("empty", ""),
    ("latin", "Hello world"),
    ("arabic", "مرحبا"),
    ("hebrew", "שלום"),
    ("hebrew_beats_latin", "שלום ab"),
    ("latin_beats_hebrew", "שלום abc"),
    ("tie_is_ltr", "aב"),
    ("digits_only", "12345"),
    ("arabic_indic_digits", "١٢٣"),
    ("punctuation_only", "(),.-"),
    ("astral_is_not_one_char", chr(0x1D400) + "a"),
    ("lone_surrogate", "\ud83d" + "a"),
    ("arabic_letters_astral_ignored", "مر" + chr(0x1D400)),
]:
    code_points = cps(chars)
    rtl_cases.append(
        {
            "name": name,
            "cps": code_points,
            "rtl": bool(right_to_left([(chr(cp), 0.0, 0) for cp in code_points])),
        }
    )

# ---------------------------------------------------------------------------- pure: assign_runs


def run(chars: list[tuple[str, tuple[float, float, float, float]]]) -> TextLine:
    """A TextLine from (char, box) pairs; only ``chars`` is read by ``assign_runs``."""
    ink = [(c, b) for c, b in chars if not c.isspace()]
    box = (
        min(b[0] for _, b in ink),
        min(b[1] for _, b in ink),
        max(b[2] for _, b in ink),
        max(b[3] for _, b in ink),
    )
    return TextLine(box, "".join(c for c, _ in chars), chars)


def box(x0: float, y0: float, x1: float, y1: float):
    return (x0, y0, x1, y1)


def assign_case(name: str, runs: list, boxes: list, k: float, note: str = "") -> dict:
    arr = np.array(boxes, np.float32).reshape(-1, 4) if boxes else np.zeros((0, 4), np.float32)
    toks, lost = assign_runs(runs, arr, k)
    return {
        "name": name,
        "note": note,
        "k": k,
        "boxes": z_f32(arr),
        "n": int(len(arr)),
        "runs": [
            {
                "text": r.text,
                "chars": [{"cp": ord(c), "box": list(b)} for c, b in r.chars],
            }
            for r in runs
        ],
        "toks": [
            [{"cp": ord(t[0]), "x": float(t[1]), "ci": int(t[2])} for t in tk] for tk in toks
        ],
        "lost": int(lost),
    }


A = box(0, 0, 10, 10)  # centre (5, 5)
B = box(30, 0, 40, 10)  # centre (35, 5)
SP = box(20, 0, 25, 10)  # a space between A and B

assign_cases = [
    assign_case(
        "one_run_one_box",
        [run([("A", A), ("B", box(12, 0, 22, 10))])],
        [box(0, 0, 100, 20)],
        1.0,
        "the run's characters stay in PDF order, x is the centre",
    ),
    assign_case(
        "space_follows_the_character_before_it",
        [run([("A", A), (" ", SP), ("B", B)])],
        [box(0, 0, 100, 20)],
        1.0,
        "a space keeps the previous character's x",
    ),
    assign_case(
        "a_space_before_any_character_is_dropped",
        [run([(" ", SP), ("A", A)])],
        [box(0, 0, 100, 20)],
        1.0,
        "j is None until a character lands, so the leading space is not a token",
    ),
    assign_case(
        "a_run_across_two_cells_splits",
        [run([("A", A), ("B", box(110, 0, 120, 10))])],
        [box(0, 0, 100, 20), box(100, 0, 200, 20)],
        1.0,
        "each character goes to the box that contains its centre",
    ),
    assign_case(
        "characters_outside_every_box_are_lost",
        [run([("A", A), ("Z", box(500, 500, 510, 510)), ("B", B)])],
        [box(0, 0, 100, 20)],
        1.0,
        "lost counts non-space characters only",
    ),
    assign_case(
        "no_boxes_at_all",
        [run([("A", A), (" ", SP), ("B", B)]), run([("C", box(0, 0, 5, 5))])],
        [],
        1.0,
        "the early return counts every non-space character of every run",
    ),
    assign_case(
        "smallest_box_wins",
        [run([("A", A)])],
        [box(0, 0, 200, 200), box(0, 0, 50, 50)],
        1.0,
        "both contain the centre; the smaller area wins",
    ),
    assign_case(
        "area_tie_keeps_the_first",
        [run([("A", A)])],
        [box(0, 0, 50, 50), box(0, 0, 50, 50)],
        1.0,
        "numpy's argmin returns the first minimum",
    ),
    assign_case(
        "a_centre_below_a_box_edge_rounds_onto_it",
        [run([("A", box(0, 0, 9.9999998, 10))])],
        [box(5.0, 0, 20, 20)],
        1.0,
        "float32 comparison: the centre is 4.9999999, which numpy casts to 5.0 (NEP 50), "
        "so it lands inside; a float64 test would call it lost",
    ),
    assign_case(
        "two_runs_in_one_line_order_by_x",
        [
            run([("B", box(50, 0, 60, 10))]),
            run([("A", box(10, 0, 20, 10))]),
        ],
        [box(0, 0, 100, 20)],
        1.0,
        "left to right: the parts sort by their smallest x",
    ),
    assign_case(
        "a_right_to_left_line_orders_by_reverse_x",
        [
            run([("ש", box(50, 0, 60, 10)), ("ל", box(40, 0, 50, 10))]),
            run([("A", box(10, 0, 20, 10))]),
        ],
        [box(0, 0, 100, 20)],
        1.0,
        "the bidi base direction is right-to-left, so the parts sort by the largest x",
    ),
    assign_case(
        "one_run_in_two_lines_keeps_its_first_seen_order",
        [
            run([("A", box(10, 0, 20, 10)), ("B", box(10, 30, 20, 40))]),
            run([("C", box(30, 30, 40, 40))]),
        ],
        [box(0, 0, 100, 20), box(0, 20, 100, 50)],
        1.0,
        "line 2 holds parts 0 and 1 in the order the runs were first seen",
    ),
    assign_case(
        "k_scales_the_character_centres",
        [run([("A", box(0, 0, 10, 10))])],
        [box(0, 0, 100, 100)],
        2.0,
        "points to pixels",
    ),
]

# ---------------------------------------------------------------------------- pure: readable


def text_run(text: str) -> TextLine:
    return TextLine((0.0, 0.0, 0.0, 0.0), text, [])


ocr = OrtOCR(MODELS, threads=4)

readable_cases = []
for name, texts in [
    ("latin", ["Hello world"]),
    ("cjk", ["你好世界"]),
    ("latin_and_cjk", ["Hello 你好"]),
    ("empty", [""]),
    ("digits_only", ["12345"]),
    ("punctuation_only", ["(),.-"]),
    ("emoji_only", ["😀😁"]),
    ("cyrillic_is_known", ["Привет"]),
    ("greek_is_known", ["αβγδ"]),
    ("accented_latin", ["café déjà"]),
    ("hebrew_is_unknown", ["שלום"]),
    ("arabic_is_unknown", ["مرحبا"]),
    ("thai_is_unknown", ["กขค"]),
    ("runic_is_unknown", ["ᚠᚢᚦ"]),
    ("astral_math_is_unknown", ["𝐀𝐁𝐂"]),
    ("cjk_ext_b_is_unknown", ["\U00020000\U00020001"]),
    ("exactly_at_the_threshold", ["Hello worlᚠ"]),  # 10 letters, 9 known: 9 >= 9.0
    ("just_below_the_threshold", ["Hello worᚠ"]),  # 9 letters, 8 known: 8 < 8.1
]:
    runs = [text_run(t) for t in texts]
    readable_cases.append(
        {"name": name, "texts": texts, "readable": bool(readable(ocr, runs))}
    )

# ---------------------------------------------------------------------------- convert


def page_json(page) -> dict:
    return {
        "lines": [
            {"box": [float(v) for v in ln.box], "text": ln.text, "conf": float(ln.conf)}
            for ln in page.lines
        ],
        "markdown": page.markdown,
        "blocks": [
            {"label": b["label"], "bbox": [float(v) for v in b["bbox"]], "text": b["text"]}
            for b in page.blocks
        ],
        "source": page.source,
        "note": page.note,
        "rotation": int(page.rotation),
        "skew": float(page.skew),
        # Wall clock: only the keys are part of the output.
        "timings": list(page.timings.keys()),
    }


pdfs = build_all()
WORK.mkdir(parents=True, exist_ok=True)
for name, data in pdfs.items():
    (WORK / f"{name}.pdf").write_bytes(data)

# (sample, Converter kwargs, password, max_pages)
PDF_CASES = [
    ("text", {}, None, 0),
    ("text", {"mode": "ocr"}, None, 0),
    ("text", {"mode": "text"}, None, 0),
    ("text", {"pdf_order": False}, None, 0),
    ("text", {"dpi": 100.0}, None, 0),
    ("text", {"image_ocr": False}, None, 0),
    ("text", {}, None, 1),
    ("image", {}, None, 0),
    ("image", {"image_ocr": False}, None, 0),
    ("rotated", {}, None, 0),
    ("cropped", {}, None, 0),
    ("invisible", {}, None, 0),
    ("empty", {}, None, 0),
    ("empty", {"mode": "text"}, None, 0),
    ("broken", {}, None, 0),
    ("locked", {}, "secret", 0),
]

pdf_cases = []
for name, options, password, max_pages in PDF_CASES:
    conv = Converter(ocr, **options)
    label = name + "".join(f"_{k}={v}" for k, v in sorted(options.items()))
    if max_pages:
        label += f"_max_pages={max_pages}"
    pages = [
        {"index": int(i), "page": page_json(p)}
        for i, p in conv.convert(str(WORK / f"{name}.pdf"), max_pages=max_pages, password=password)
    ]
    pdf_cases.append(
        {
            "name": label,
            "pdf": name,
            "options": options,
            "password": password,
            "max_pages": max_pages,
            "pages": pages,
        }
    )
    print(
        f"  {label:36s} pages={len(pages)} "
        + " ".join(
            f"[{p['index']} {p['page']['source']:6s} {len(p['page']['lines']):3d}L "
            f"{p['page']['note']!r}]"
            for p in pages
        )
    )

# The InputError messages, without the path prefix (the test prepends its own path).
corrupt = b"%PDF-1.4\nnot a real document\n"
pdfs["corrupt"] = corrupt
(WORK / "corrupt.pdf").write_bytes(corrupt)

error_cases = []
for name, password in [("locked", None), ("corrupt", None)]:
    path = WORK / f"{name}.pdf"
    try:
        list(Converter(ocr).convert(str(path), password=password))
    except Exception as e:  # noqa: BLE001 - the message is what is recorded
        prefix = f"{path}: "
        assert str(e).startswith(prefix), str(e)
        error_cases.append(
            {
                "name": f"{name}_no_password",
                "pdf": name,
                "password": password,
                "message": str(e)[len(prefix):],
            }
        )
        print(f"  {name:36s} error {str(e)[len(prefix):]!r}")
    else:
        raise AssertionError(f"{name} should have raised InputError")

# One example image: the non-PDF path.
image_cases = []
for name, options in [("multi_column", {})]:
    path = EXAMPLES / f"{name}.png"
    raw = path.read_bytes()
    pages = [
        {"index": int(i), "page": page_json(p)}
        for i, p in Converter(ocr, **options).convert(str(path))
    ]
    image_cases.append(
        {
            "name": name,
            "file": path.relative_to(ROOT / "resources").as_posix(),
            "sha256": sha(raw),
            "options": options,
            "pages": pages,
        }
    )
    print(f"  {name:36s} image pages={len(pages)}")

doc = {
    "note": __doc__.strip().splitlines()[0],
    "page_scale": page_scale_cases,
    "right_to_left": rtl_cases,
    "assign_runs": assign_cases,
    "readable": readable_cases,
    "pdfs": {name: base64.b64encode(data).decode("ascii") for name, data in pdfs.items()},
    "pdf_cases": pdf_cases,
    "error_cases": error_cases,
    "image_cases": image_cases,
}
text = json.dumps(doc, ensure_ascii=False, indent=1)
OUT.write_text(text, encoding="utf-8", newline="\n")
print(f"wrote {OUT.relative_to(ROOT)}: {len(text)} bytes ({len(pdf_cases)} pdf cases)")
