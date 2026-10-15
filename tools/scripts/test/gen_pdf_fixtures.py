"""Generate ``src/Indra.Ocr.Tests/fixtures/pdf.json`` — the parity fixture for the PDFium
surface (``Indra.Ocr.Pdf``, step 140) and for the text-layer policy (step 150).

The sample PDFs come from :mod:`pdf_samples`; everything else is read back with pypdfium2
and the reference ``indra_ocr.textlayer`` module, i.e. the same library the port must match.

What is recorded per document:

* ``pdf``  — the file's bytes, base64, so the C# tests need no Python and no file;
* per page — ``size`` (points, as rendered), ``rotation``, the renders at the sample's
  scales (raw little-endian bytes, zlib+base64: the C# test compares them bit-exactly),
  every text-layer character (code, angle, whether pdfium returned a box, and the box in
  pdfium's ``left right bottom top`` order), the ``FPDF_PageToDevice`` affine, the share of
  invisibly drawn text objects, and the reference ``read_text_layer`` result (runs, stats,
  verdict) for the step-150 tests.

Run with the reference environment:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_pdf_fixtures.py
"""

from __future__ import annotations

import base64
import ctypes
import json
import sys
import zlib
from pathlib import Path

import pypdfium2 as pdfium
import pypdfium2.raw as R

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str((HERE / ".." / ".." / ".." / "resources" / "indra-src").resolve()))

from indra_ocr import textlayer as TL  # noqa: E402

from pdf_samples import SAMPLES, build_all  # noqa: E402

OUT = Path("src/Indra.Ocr.Tests/fixtures/pdf.json")
CHECK_SCALE = 1.0  # document.CHECK_DPI / 72: the resolution of the ink check

# bad_char_count cases, as code points (a JSON string cannot carry a lone surrogate).  The
# reference's answer for each is recorded, so the C# port is checked on the exact characters:
# replacement, private use (BMP and astral), lone surrogates, unassigned points, controls,
# formats and a tab.
BAD_CHAR_CASES = [
    [],
    [0x41],
    [0xE9],
    [0xFFFD],
    [0xE000],
    [0xD800],
    [0x0378],
    [0x01],
    [0x09],
    [0x0A],
    [0x85],
    [0x00AD],
    [0x200B],
    [0x200C],
    [0x2028],
    [0x1F600],
    [0x10FFFF],
    [0x0F0000],
    [0x41, 0xFFFD, 0x42],
]


def zbytes(data) -> str:
    return base64.b64encode(zlib.compress(bytes(data), 9)).decode()


def chars_json(page) -> list:
    """Every character as the raw pdfium calls see it (textlayer.extract_runs' inputs)."""
    tp = page.get_textpage()
    try:
        n = tp.count_chars()
        items = []
        for i in range(n):
            l, r, b, t = (ctypes.c_double() for _ in range(4))
            has_box = bool(R.FPDFText_GetCharBox(tp.raw, i, *(ctypes.byref(v) for v in (l, r, b, t))))
            items.append(
                {
                    "code": int(R.FPDFText_GetUnicode(tp.raw, i)),
                    "angle": float(R.FPDFText_GetCharAngle(tp.raw, i)),
                    "has_box": has_box,
                    "box": [l.value, r.value, b.value, t.value],
                }
            )
        return items
    finally:
        tp.close()


def stats_json(stats: dict) -> dict:
    return {k: (int(v) if isinstance(v, int) else float(v)) for k, v in stats.items()}


def runs_json(runs) -> list:
    return [
        {
            "bbox": [float(v) for v in ln.bbox],
            "text": ln.text,
            "chars": [[c, [float(v) for v in bx]] for c, bx in ln.chars],
        }
        for ln in runs
    ]


def page_json(page, scales) -> dict:
    w, h = page.get_size()
    renders = []
    for scale in scales:
        arr = page.render(scale=scale, grayscale=True).to_numpy()
        renders.append(
            {
                "scale": scale,
                "width": int(arr.shape[1]),
                "height": int(arr.shape[0]),
                "stride": int(arr.strides[0]),
                "gray": zbytes(arr.tobytes()),
            }
        )
    check = page.render(scale=CHECK_SCALE, grayscale=True).to_numpy()
    layer = TL.read_text_layer(page, check, CHECK_SCALE)
    runs, stats = TL.extract_runs(page)
    return {
        "size": [w, h],
        "rotation": page.get_rotation(),
        "renders": renders,
        "chars": chars_json(page),
        "affine": [[float(v) for v in row] for row in TL._device_affine(page)],
        "invisible_frac": float(TL.invisible_text_frac(page)),
        # The step-150 expectations: the runs extract_runs returned, its stats, and the
        # verdict read_text_layer reached on the CHECK_SCALE render above.
        "textlayer": {
            "runs": runs_json(runs),
            "stats": stats_json(stats),
            "layer_stats": stats_json(layer.stats),
            "layer_lines": len(layer.lines),
            "trusted": bool(layer.trusted),
            "reason": layer.reason,
        },
    }


def error_json(name: str, data: bytes, password: str | None) -> dict:
    """How pypdfium2 reports a document that cannot be opened."""
    message = None
    try:
        with pdfium.PdfDocument(data, password=password):
            pass
    except pdfium.PdfiumError as e:
        message = str(e)
    return {
        "name": name,
        "pdf": base64.b64encode(data).decode(),
        "password": password,
        "message": message,
    }


def main() -> None:
    samples = build_all()
    docs = []
    for name, (builder, scales, password) in SAMPLES.items():
        data = builder()
        with pdfium.PdfDocument(data, password=password) as doc:
            pages = [page_json(page, scales) for page in doc]
        docs.append(
            {
                "name": name,
                "pdf": base64.b64encode(data).decode(),
                "password": password,
                "pages": pages,
            }
        )
        print(f"  {name:10s} {len(data):6d} bytes  pages={len(pages)}")

    errors = [
        error_json("empty", b"", None),
        error_json("garbage", b"not a pdf at all\n", None),
        error_json("truncated", samples["text"][:200], None),
        error_json("locked_no_password", samples["locked"], None),
        error_json("locked_wrong_password", samples["locked"], "wrong"),
    ]
    for e in errors:
        print(f"  error {e['name']:22s} -> {e['message']!r}")

    bad_chars = [
        {"codes": codes, "count": TL.bad_char_count("".join(chr(c) for c in codes))}
        for codes in BAD_CHAR_CASES
    ]

    fixture = {
        "note": __doc__.strip().splitlines()[0],
        "check_scale": CHECK_SCALE,
        "documents": docs,
        "errors": errors,
        "bad_chars": bad_chars,
    }
    text = json.dumps(fixture, ensure_ascii=True, indent=1, sort_keys=False)
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(
        f"wrote {OUT}: {len(text)} bytes "
        f"({len(docs)} documents, {len(errors)} error cases, {len(bad_chars)} bad-char cases)"
    )


if __name__ == "__main__":
    main()
