"""Generate deterministic test inputs under temp/inputs/ for CLI + pipeline parity.

    python tools/scripts/gen_inputs.py [outdir]

Creates small images (PNG), a multi-page TIFF, and a born-digital PDF with a text
layer, all from fixed content so reference and port see identical bytes.
"""

from __future__ import annotations

import sys
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


def render_lines(lines, size=(900, 700), px=30, ttf=LATIN_TTF) -> Image.Image:
    img = Image.new("L", size, 255)
    d = ImageDraw.Draw(img)
    f = font(ttf, px)
    y = 40
    for ln in lines:
        d.text((50, y), ln, fill=0, font=f)
        y += px + 18
    return img


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

    # Multi-page TIFF: three frames with distinct content.
    frames = [
        render_lines(["Frame one of the TIFF."]),
        render_lines(["Frame two of the TIFF."]),
        render_lines(["Frame three of the TIFF."]),
    ]
    frames[0].save(OUT / "multi.tiff", save_all=True, append_images=frames[1:])

    # A born-digital PDF with a real text layer (fpdf2, standard Helvetica).
    from fpdf import FPDF

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
