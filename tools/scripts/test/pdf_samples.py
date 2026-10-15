"""Sample PDFs for the Indra.Ocr.Pdf and textlayer fixtures.

The PDFs are small enough to travel base64-encoded inside the JSON fixtures, so the C#
tests need neither Python nor any binary checked into git.

They are built with fpdf2 (text, images, a fixed creation date so the bytes are stable)
and patched with pikepdf for what fpdf2 cannot express: page rotation, a crop box, a
second content stream drawn with the invisible text render mode, a broken ``/ToUnicode``
map (a private-use and a control character, i.e. the "broken font map" case) and
encryption with a user password.

Run the module directly to drop the PDFs into ``temp/pdf_samples`` for inspection:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\pdf_samples.py
"""

from __future__ import annotations

import io
from datetime import datetime, timezone
from pathlib import Path

import pikepdf
from fpdf import FPDF
from PIL import Image

#: A CMap that maps the glyphs of "Hello world" to private-use and control characters,
#: the shape a broken font map has when the codes are not text (textlayer.bad_char_count).
BROKEN_CMAP = """\
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CMapName /Custom def
/CMapType 2 def
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
1 begincodespacerange
<00> <FF>
endcodespacerange
5 beginbfchar
<48> <E000>
<65> <E001>
<6C> <E002>
<6F> <E003>
<20> <0001>
endbfchar
endcmap
CMapName currentdict /CMap defineresource pop
end
end
"""

FIXED_DATE = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _new(width: float, height: float) -> FPDF:
    pdf = FPDF(unit="pt", format=(width, height))
    pdf.set_auto_page_break(False)
    pdf.set_creation_date(FIXED_DATE)
    return pdf


def _save(pdf: FPDF) -> bytes:
    return bytes(pdf.output())


def _patch(data: bytes, patch) -> bytes:
    with pikepdf.open(io.BytesIO(data)) as p:
        patch(p)
        buf = io.BytesIO()
        # deterministic_id: the /ID comes from the contents, not the clock, so regenerating
        # the fixture produces identical bytes.
        p.save(buf, deterministic_id=True)
        return buf.getvalue()


def text() -> bytes:
    """Two pages of Helvetica text; page 2 has a fractional page size."""
    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font("Helvetica", size=12)
    pdf.text(20, 40, "Hello world")
    pdf.set_fill_color(0, 0, 0)
    pdf.rect(20, 60, 40, 10, style="F")  # a path object between the two text objects
    pdf.text(20, 95, "Second line")
    pdf.add_page(format=(200.5, 120.25))
    pdf.set_font("Helvetica", size=9)
    pdf.text(10, 30, "Fractional")
    return _save(pdf)


def image() -> bytes:
    """A page with an embedded 8-bit grayscale image and a line of text below it."""
    img = Image.new("L", (60, 40))
    px = img.load()
    for y in range(40):
        for x in range(60):
            px[x, y] = (x * 4 + y * 3) % 256
    buf = io.BytesIO()
    img.save(buf, format="PNG")

    pdf = _new(200, 120)
    pdf.add_page()
    pdf.image(buf, x=20, y=20, w=60, h=40)
    pdf.set_font("Helvetica", size=10)
    pdf.text(20, 90, "Under image")
    return _save(pdf)


def rotated() -> bytes:
    """A page with /Rotate 90: size, render and character boxes come out rotated."""

    def patch(p):
        p.pages[0].obj["/Rotate"] = 90

    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font("Helvetica", size=12)
    pdf.text(20, 40, "Hello world")
    return _patch(_save(pdf), patch)


def cropped() -> bytes:
    """A page whose crop box is smaller than its media box (the rendered frame)."""

    def patch(p):
        p.pages[0].obj["/CropBox"] = [20, 10, 180, 110]

    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font("Helvetica", size=12)
    pdf.text(20, 40, "Hello world")
    return _patch(_save(pdf), patch)


def invisible() -> bytes:
    """A visible line plus a second content stream drawn with render mode 3."""

    def patch(p):
        page = p.pages[0]
        page.obj["/Contents"] = pikepdf.Array(
            [
                page.obj["/Contents"],
                p.make_stream(b"BT /F1 12 Tf 3 Tr 20 80 Td (hidden layer) Tj ET\n"),
            ]
        )

    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font("Helvetica", size=12)
    pdf.text(20, 40, "Hello world")
    return _patch(_save(pdf), patch)


def empty() -> bytes:
    """A page with no text at all (an image-only scan)."""
    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_fill_color(200, 200, 200)
    pdf.rect(20, 20, 60, 40, style="F")
    return _save(pdf)


def broken() -> bytes:
    """A page whose font has a /ToUnicode mapping to private-use/control characters."""
    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font("Helvetica", size=12)
    pdf.text(20, 40, "Hello world")

    def patch(p):
        font = p.pages[0].obj["/Resources"]["/Font"]["/F1"]
        font["/ToUnicode"] = p.make_stream(BROKEN_CMAP.encode())

    return _patch(_save(pdf), patch)


def locked() -> bytes:
    """A page encrypted with the user password ``secret``."""
    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font("Helvetica", size=12)
    pdf.text(20, 40, "Hello world")
    with pikepdf.open(io.BytesIO(_save(pdf))) as p:
        buf = io.BytesIO()
        # AES-256 with the user password ``secret``.  pikepdf randomises the encryption
        # parameters, so this one sample's bytes differ on every regeneration (its page
        # data does not): the fixture as committed is stable, regenerating it is not.
        p.save(buf, encryption=pikepdf.Encryption(owner="owner", user="secret", R=6))
        return buf.getvalue()


#: name -> (builder, render scales, user password)
#:
#: The scales are the ones the fixture records renders for: 0.25 and 1.0 cover the ink
#: check's resolution, 0.255 gives an odd pixel width (pdfium still packs rows tightly),
#: and 2.5 checks upscaling.
SAMPLES = {
    "text": (text, (0.25, 1.0, 0.255), None),
    "image": (image, (0.25, 1.0), None),
    "rotated": (rotated, (0.25, 1.0), None),
    "cropped": (cropped, (0.25, 1.0), None),
    "invisible": (invisible, (0.25, 1.0), None),
    "empty": (empty, (0.25, 1.0), None),
    "broken": (broken, (0.25, 1.0), None),
    "locked": (locked, (0.25, 1.0), "secret"),
}


def build_all() -> dict[str, bytes]:
    """Every sample PDF, by name (the bytes are deterministic)."""
    return {name: builder() for name, (builder, _, _) in SAMPLES.items()}


if __name__ == "__main__":
    out = Path("temp/pdf_samples")
    out.mkdir(parents=True, exist_ok=True)
    for name, data in build_all().items():
        path = out / f"{name}.pdf"
        path.write_bytes(data)
        print(f"{path} {len(data)} bytes")
