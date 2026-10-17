"""Generate the release smoke samples under build/smoke/.

    python tools/scripts/gen_smoke_samples.py

The two files are committed: they are what build/smoke-win.ps1, build/smoke-linux.sh
and tools/nuget-smoke feed to the published binaries and to the NuGet packages, so
they have to be there on a machine that has nothing but the repository -- no
resources/ (not in git) and no Python.  This script is how they were made, and the
content is fixed (no clock, no random) so regenerating them is a no-op.

    latin.png  a 700x220 image with two lines of black text on white
    latin.pdf  one A4 page whose two lines carry a real text layer

Both say "The quick brown fox jumps over the lazy dog." and "Invoice number: 12345";
the smoke runners look for "12345" in the image OCR and for "Invoice number: 12345"
in the PDF's extracted text layer.
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from fpdf import FPDF

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "build" / "smoke"

LINES = ["The quick brown fox jumps over the lazy dog.", "Invoice number: 12345"]

# The same font search order the parity inputs use: the bytes are fixed once written,
# so it does not matter which of these the machine has.
FONTS = [
    Path("C:/Windows/Fonts/arial.ttf"),
    Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
    Path("/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"),
]


def load_font(size: int):
    for path in FONTS:
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default()


def make_png() -> None:
    image = Image.new("L", (700, 220), 255)
    draw = ImageDraw.Draw(image)
    font = load_font(28)
    for i, line in enumerate(LINES):
        draw.text((30, 40 + i * 50), line, fill=0, font=font)
    image.save(OUT / "latin.png")


def make_pdf() -> None:
    pdf = FPDF(unit="pt", format="A4")
    # A fixed stamp: the reference harness does the same, so the bytes do not move.
    pdf.set_creation_date(datetime(2026, 1, 1, tzinfo=timezone.utc))
    pdf.set_title("indra-ocr smoke")
    pdf.add_page()
    pdf.set_font("Helvetica", size=14)
    for line in LINES:
        pdf.cell(0, 24, line, new_x="LMARGIN", new_y="NEXT")
    pdf.output(str(OUT / "latin.pdf"))


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    make_png()
    make_pdf()
    for name in ("latin.png", "latin.pdf"):
        print(f"{name}: {(OUT / name).stat().st_size} bytes")


if __name__ == "__main__":
    main()
