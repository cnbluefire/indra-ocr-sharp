"""Generate deterministic test inputs under temp/inputs/ for CLI + pipeline parity.

    python tools/scripts/gen_inputs.py [outdir]

Creates small images (PNG), a multi-page TIFF, and a born-digital PDF with a text
layer, all from fixed content so reference and port see identical bytes.
"""

from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "temp" / "inputs"

WIN_FONTS = Path("C:/Windows/Fonts")
LATIN_TTF = WIN_FONTS / "arial.ttf"
CJK_TTF = WIN_FONTS / "msyh.ttc"


def font(path: Path, size: int):
    try:
        return ImageFont.truetype(str(path), size)
    except OSError:
        return ImageFont.load_default()


def render_lines(lines, size=(900, 700), px=30, ttf=LATIN_TTF, step=None) -> Image.Image:
    img = Image.new("L", size, 255)
    d = ImageDraw.Draw(img)
    f = font(ttf, px)
    y = 40
    for ln in lines:
        d.text((50, y), ln, fill=0, font=f)
        y += step if step is not None else px + 18
    return img


def first_font(paths) -> Path:
    """The first font of ``paths`` that is installed (the page's content does not depend on
    which one it is; both sides read the same bytes either way)."""
    for path in paths:
        if Path(path).exists():
            return Path(path)
    return LATIN_TTF


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)

    # A short "document" of Latin text.
    latin = render_lines(
        [
            "The quick brown fox jumps over the lazy dog.",
            "Invoice number: 12345",
            "Total: $1,234.56",
            "Contact: hello@example.com",
        ]
    )
    latin.save(OUT / "latin.png")

    # Same content, turned a quarter turn (exercises orient.onnx).
    latin.rotate(-90, expand=True, fillcolor=255).save(OUT / "latin_rot90.png")
    # Slightly tilted (exercises the deskew path).
    latin.rotate(3.0, expand=True, fillcolor=255).save(OUT / "latin_tilt.png")

    # A two-column layout.
    empty = Image.new("L", (900, 700), 255)
    empty.paste(render_lines(["Left column alpha beta", "second line here"], size=(400, 700)), (0, 0))
    empty.paste(
        render_lines(["Right column gamma", "another line", "and a third"], size=(400, 700)), (480, 0)
    )
    empty.save(OUT / "two_columns.png")

    # Chinese content (routes to the CJK recognizer).
    if CJK_TTF.exists():
        render_lines(
            ["这是一段中文测试文本。", "第二行内容用于识别。"], px=34, ttf=CJK_TTF
        ).save(OUT / "cjk.png")

    # A blank page.
    Image.new("L", (600, 800), 255).save(OUT / "blank.png")

    # A page of math: the formula lines are what the classifier fires on, so `--math` reads
    # them with MathRec while the prose between them stays as the recognizer read it.  Cambria
    # has the symbols the classifier knows best; Times and Arial are the fallbacks.
    math_lines = [
        "We compute the integral as follows.",
        "f(x) = \u222b\u2080^\u221e e^\u2212x\u00b2 dx = \u221a\u03c0 / 2",
        "E = mc\u00b2",
        "x = \u2212b \u00b1 \u221a(b\u00b2 \u2212 4ac) / 2a",
        "\u2211\u1d62\u208c\u2081\u207f i = n(n + 1)/2",
        "lim\u2093\u208a\u2080 sin x / x = 1",
        "The result follows from the definition.",
    ]
    render_lines(
        math_lines,
        size=(1400, 120 + 90 * len(math_lines)),
        px=40,
        ttf=first_font([WIN_FONTS / "cambria.ttc", WIN_FONTS / "times.ttf", LATIN_TTF]),
        step=90,
    ).save(OUT / "math.png")

    # The same math as a born-digital PDF: its text layer has the symbols but not the LaTeX,
    # so `--math` reads those lines with MathRec and keeps their place in the content order.
    from fpdf import FPDF

    math_pdf = FPDF(unit="pt", format=(320, 240))
    math_pdf.set_creation_date(datetime(2026, 1, 1, tzinfo=timezone.utc))
    math_pdf.set_auto_page_break(False)
    math_pdf.add_page()
    math_pdf.add_font("math", "", str(first_font([WIN_FONTS / "times.ttf",
                                                  WIN_FONTS / "DejaVuSans.ttf"])))
    math_pdf.set_font("math", size=16)
    for i, line in enumerate(math_lines):
        math_pdf.text(20, 40 + i * 32, line)
    math_pdf.output(str(OUT / "math.pdf"))

    # Multi-page TIFF: three frames with distinct content.
    frames = [
        render_lines(["Frame one of the TIFF."]),
        render_lines(["Frame two of the TIFF."]),
        render_lines(["Frame three of the TIFF."]),
    ]
    frames[0].save(OUT / "multi.tiff", save_all=True, append_images=frames[1:])

    # A born-digital PDF with a real text layer (fpdf2, standard Helvetica).
    pdf = FPDF(unit="pt", format="letter")
    pdf.add_page()
    pdf.set_font("Helvetica", size=14)
    for ln in [
        "Chapter 1",
        "This is a born-digital PDF with a text layer.",
        "The detector should still find the visual lines.",
        "Numbers 0123456789 and punctuation, too.",
    ]:
        pdf.cell(0, 22, ln, new_x="LMARGIN", new_y="NEXT")
    pdf.add_page()
    pdf.set_font("Helvetica", size=12)
    for ln in ["Page two heading", "Some more text on the second page.", "End."]:
        pdf.cell(0, 18, ln, new_x="LMARGIN", new_y="NEXT")
    pdf.output(str(OUT / "digital.pdf"))

    print(f"inputs written to {OUT}")
    for p in sorted(OUT.iterdir()):
        print(f"  {p.name} ({p.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
