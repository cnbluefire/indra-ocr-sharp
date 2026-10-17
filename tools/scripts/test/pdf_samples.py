"""Sample PDFs for the Indra.Ocr.Pdf and textlayer fixtures.

The PDFs are small enough to travel base64-encoded inside the JSON fixtures, so the C#
tests need neither Python nor any binary checked into git.

They are built with fpdf2 (text, images, a fixed creation date so the bytes are stable)
and patched with pikepdf for what fpdf2 cannot express: page rotation, a crop box, a
second content stream drawn with the invisible text render mode, a broken ``/ToUnicode``
map (a private-use and a control character, i.e. the "broken font map" case) and
encryption with a user password.

Every sample embeds ``fonts/LiberationSans-Regular.ttf`` rather than fpdf2's built-in
``Helvetica``: a standard font is never embedded, so pdfium substitutes a *host* font and
answers ``FPDFText_GetCharBox`` with whatever that host's font says, which made the
text-layer fixture a statement about the machine that captured it
(``fonts/README.md``, ``docs/380-test-hosts.md``).

Run the module directly to drop the PDFs into ``temp/pdf_samples`` for inspection:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\pdf_samples.py
"""

from __future__ import annotations

import io
import re
from datetime import datetime, timezone
from pathlib import Path

import pikepdf
from fpdf import FPDF
from PIL import Image

FIXED_DATE = datetime(2026, 1, 1, tzinfo=timezone.utc)

#: The font every sample embeds, so that pdfium's text-layer geometry is the font's own and not
#: whatever the *host* substitutes for a non-embedded one (`fonts/README.md`,
#: `docs/380-test-hosts.md`).
FONT = "LiberationSans"
FONT_FILE = Path(__file__).with_name("fonts") / "LiberationSans-Regular.ttf"


def _new(width: float, height: float) -> FPDF:
    pdf = FPDF(unit="pt", format=(width, height))
    pdf.set_auto_page_break(False)
    pdf.set_creation_date(FIXED_DATE)
    pdf.add_font(FONT, fname=FONT_FILE)
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


def _font_of(page) -> pikepdf.Object:
    """The page's only font: every sample draws with one."""
    return next(iter(page.obj["/Resources"]["/Font"].values()))


def _tounicode(font) -> dict[int, str]:
    """The embedded subset's own ``/ToUnicode`` map as fpdf2 wrote it: code -> character.

    A TrueType font goes into the PDF as a ``/Type0`` font with ``/Identity-H``, so the
    content stream's two-byte codes are the subset's own and only this map says what they
    mean.  (Writing those codes by hand is not an option: fpdf2 numbers them by first use.)
    """
    text = font["/ToUnicode"].read_bytes().decode("latin-1")
    mapping: dict[int, str] = {}
    for block in re.findall(r"beginbfchar(.*?)endbfchar", text, re.S):
        for code, dest in re.findall(r"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>", block):
            mapping[int(code, 16)] = "".join(
                chr(int(dest[i : i + 4], 16)) for i in range(0, len(dest), 4)
            )
    for block in re.findall(r"beginbfrange(.*?)endbfrange", text, re.S):
        for first, last, dest in re.findall(
            r"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>", block
        ):
            base = int(dest, 16)
            for offset, code in enumerate(range(int(first, 16), int(last, 16) + 1)):
                mapping[code] = chr(base + offset)
    return mapping


def _broken_cmap(original: dict[int, str]) -> str:
    """A ``/ToUnicode`` map that turns every code a page uses into a private-use or a control
    character: the shape a broken font map has when the layer's codes are not text
    (textlayer.bad_char_count)."""
    targets: dict[str, int] = {}
    entries = []
    for code in sorted(original):
        char = original[code]
        if char == " ":
            target = 0x01  # a control character; the space has no glyph to point at
        else:
            targets.setdefault(char, 0xE000 + len(targets))
            target = targets[char]
        entries.append(f"<{code:04X}> <{target:04X}>")
    body = "\n".join(entries)
    return f"""\
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CMapName /Custom def
/CMapType 2 def
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
{len(entries)} beginbfchar
{body}
endbfchar
endcmap
CMapName currentdict /CMap defineresource pop
end
end
"""


def text() -> bytes:
    """Two pages of the embedded font's text; page 2 has a fractional page size."""
    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font(FONT, size=12)
    pdf.text(20, 40, "Hello world")
    pdf.set_fill_color(0, 0, 0)
    pdf.rect(20, 60, 40, 10, style="F")  # a path object between the two text objects
    pdf.text(20, 95, "Second line")
    pdf.add_page(format=(200.5, 120.25))
    pdf.set_font(FONT, size=9)
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
    pdf.set_font(FONT, size=10)
    pdf.text(20, 90, "Under image")
    return _save(pdf)


def rotated() -> bytes:
    """A page with /Rotate 90: size, render and character boxes come out rotated."""

    def patch(p):
        p.pages[0].obj["/Rotate"] = 90

    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font(FONT, size=12)
    pdf.text(20, 40, "Hello world")
    return _patch(_save(pdf), patch)


def cropped() -> bytes:
    """A page whose crop box is smaller than its media box (the rendered frame)."""

    def patch(p):
        p.pages[0].obj["/CropBox"] = [20, 10, 180, 110]

    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font(FONT, size=12)
    pdf.text(20, 40, "Hello world")
    return _patch(_save(pdf), patch)


def invisible() -> bytes:
    """A visible line plus a second content stream drawn with render mode 3."""

    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font(FONT, size=12)
    pdf.text(20, 40, "Hello world")
    pdf.text(20, 80, "hidden layer")

    def patch(p):
        page = p.pages[0]
        stream = page.obj["/Contents"].read_bytes()
        # The last text object is the hidden line: give it a stream of its own and turn on the
        # invisible render mode, the way an OCR layer sits on top of a scanned page.  The codes
        # in it are this document's own (`/Identity-H`), so the operators move as they are.
        start = stream.rindex(b"BT")
        end = stream.rindex(b"ET") + 2
        hidden = b"BT 3 Tr " + stream[start + 2 : end]
        page.obj["/Contents"] = pikepdf.Array(
            [p.make_stream(stream[:start] + stream[end:]), p.make_stream(hidden)]
        )

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
    pdf.set_font(FONT, size=12)
    pdf.text(20, 40, "Hello world")

    def patch(p):
        font = _font_of(p.pages[0])
        # Every code the page uses (from the subset's own map) points at a private-use or a
        # control character, so the layer reads as "not text" on any machine.
        font["/ToUnicode"] = p.make_stream(_broken_cmap(_tounicode(font)).encode("latin-1"))

    return _patch(_save(pdf), patch)


def locked() -> bytes:
    """A page encrypted with the user password ``secret``."""
    pdf = _new(200, 120)
    pdf.add_page()
    pdf.set_font(FONT, size=12)
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


def _font_is_embedded(data: bytes, password: str | None) -> bool:
    """Whether every font the pages use carries its own program (``/FontFile*``).

    A font that does not is a *standard* one, and pdfium answers its character boxes out of
    whatever the host substitutes for it — a fixture captured that way is about the machine
    that ran the generator, not about the port (``fonts/README.md``).
    """
    with pikepdf.open(io.BytesIO(data), password=password or "") as p:
        for page in p.pages:
            for font in page.obj["/Resources"].get("/Font", {}).values():
                descriptor = font.get("/FontDescriptor")
                if descriptor is None:  # a /Type0 font keeps it on its descendant
                    descriptor = font["/DescendantFonts"][0]["/FontDescriptor"]
                if not any(
                    key in descriptor for key in ("/FontFile", "/FontFile2", "/FontFile3")
                ):
                    return False
    return True


def build_all() -> dict[str, bytes]:
    """Every sample PDF, by name (the bytes are deterministic).

    Raises when a sample's font is not embedded: that sample would record pdfium's answer
    about the *host's* substitute font rather than about the port
    (``fonts/README.md``, ``docs/380-test-hosts.md``).
    """
    samples = {}
    for name, (builder, _, password) in SAMPLES.items():
        data = builder()
        if not _font_is_embedded(data, password):
            raise ValueError(f"sample {name!r} does not embed its font")
        samples[name] = data
    return samples


if __name__ == "__main__":
    out = Path("temp/pdf_samples")
    out.mkdir(parents=True, exist_ok=True)
    for name, data in build_all().items():
        path = out / f"{name}.pdf"
        path.write_bytes(data)
        print(f"{path} {len(data)} bytes")
