"""Generate src/Indra.Ocr.Tests/fixtures/image_decode.json.

For each in-memory image we record the raw encoded bytes (base64) and the grayscale
frames the Python reference produces, using the *same* logic as
``indra_ocr.pipeline.read_images`` (imreadmulti, falling back to imread).
"""

from __future__ import annotations

import base64
import io
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import write  # noqa: E402

rng = np.random.default_rng(7)


def png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def encode(img: Image.Image, fmt: str) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format=fmt)
    return buf.getvalue()


def ref_frames(data: bytes) -> list[np.ndarray]:
    """Reproduce pipeline.read_images on encoded bytes."""
    tmp = Path("temp") / "_decode_probe"
    tmp.parent.mkdir(exist_ok=True)
    tmp.write_bytes(data)
    ok, frames = cv2.imreadmulti(str(tmp), flags=cv2.IMREAD_GRAYSCALE)
    if not ok or not frames:
        img = cv2.imread(str(tmp), cv2.IMREAD_GRAYSCALE)
        frames = [img] if img is not None else []
    return [np.ascontiguousarray(f) for f in frames]


cases = []


def add(name: str, data: bytes):
    frames = ref_frames(data)
    cases.append(
        {
            "name": name,
            "base64": base64.b64encode(data).decode("ascii"),
            "frames": [f.tolist() for f in frames],
        }
    )


# 1. 8-bit grayscale PNG, deterministic gradient.
w, h = 37, 23
gy, gx = np.mgrid[0:h, 0:w]
gray = ((gx * 7 + gy * 13) % 256).astype(np.uint8)
add("gray_png", png_bytes(Image.fromarray(gray, "L")))

# 2. RGB PNG: distinct channels so the OpenCV weights matter.
rgb = np.stack(
    [
        ((gx * 5 + gy) % 256),
        ((gx + gy * 11) % 256),
        ((gx * 3 + gy * 17) % 256),
    ],
    axis=-1,
).astype(np.uint8)
add("rgb_png", png_bytes(Image.fromarray(rgb, "RGB")))

# 3. RGBA PNG: alpha must be ignored.
rgba = np.concatenate([rgb, (((gx + gy) * 5) % 256).astype(np.uint8)[..., None]], axis=-1)
add("rgba_png", png_bytes(Image.fromarray(rgba, "RGBA")))

# 4. Paletted (indexed) PNG.
pal_src = Image.fromarray(rgb, "RGB").quantize(colors=64)
add("palette_png", png_bytes(pal_src))

# 5. BMP (no compression, RGB).
add("rgb_bmp", encode(Image.fromarray(rgb, "RGB"), "BMP"))

# 6. Multi-frame TIFF: three distinctive frames.
f0 = ((gx * 2 + gy) % 256).astype(np.uint8)
f1 = ((gx + gy * 3) % 256).astype(np.uint8)
f2 = ((gx * 9) % 256).astype(np.uint8)
frames = [Image.fromarray(f, "L") for f in (f0, f1, f2)]
buf = io.BytesIO()
frames[0].save(buf, format="TIFF", save_all=True, append_images=frames[1:])
add("tiff_multi", buf.getvalue())

# 7. Random-noise RGB (exercises many weight combinations at once).
noise = rng.integers(0, 256, size=(19, 29, 3), dtype=np.uint8)
add("rgb_noise_png", png_bytes(Image.fromarray(noise, "RGB")))

write("image_decode.json", {"cases": cases})
