"""Generate src/Indra.Ocr.Tests/fixtures/viewer.json — the parity fixture for step 210
(``viewer.Engine``: an upload, one page per request, the preview, and the response).

For each input — the sample PDFs of :mod:`pdf_samples` (a text page, a page that PDFium
already turns, a scan) and three synthetic colour PNGs (one dense page, and that page turned
90 and 180 degrees, which is what makes the orientation model sure enough to turn it back) —
the fixture records the reference ``Engine.upload``'s page count and the reference
``Engine.page``'s response for a handful of option sets.  The PDFs and the PNGs are recorded
as bytes (base64): ``tools/scripts/test/viewer_parity.py`` uploads exactly those bytes to both
servers, so the two sides see the same documents.  The recorded responses stay as the
reference's own record — the harness compares against the *live* reference instead of replaying
them (a recording cannot catch a change in the reference, and the C# engine test that asserted
it is gone; see ``docs/210-viewer-engine.md``).

The error texts are recorded too: a page asked for a mode outside ``MODES``, a page of an
unknown upload, and an upload that is neither a PDF nor a readable image.

Two things cannot be compared and are left out of the page record: the JPEG preview (Python
encodes with OpenCV's libjpeg, the port with ImageSharp, so the bytes differ — the test only
checks the prefix and that it decodes) and the *values* of the timings (wall clock; only the
keys, and their order, are compared).

Run with the reference environment:

    tools\\pyenv\\Scripts\\python.exe tools\\scripts\\test\\gen_viewer_fixtures.py
"""

from __future__ import annotations

import base64
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "resources" / "indra-src"))

import cv2  # noqa: E402
import numpy as np  # noqa: E402
from indra_ocr.viewer import Engine  # noqa: E402

from pdf_samples import build_all  # noqa: E402

MODELS = ROOT / "resources" / "models"
OUT = ROOT / "src" / "Indra.Ocr.Tests" / "fixtures" / "viewer.json"
WORK = ROOT / "temp" / "viewer_samples"

DEFAULTS = {
    "mode": "auto",
    "pdf_order": True,
    "image_ocr": True,
    "math": False,
    "furniture": False,
    "dpi": 200.0,
    "max_pages": 20,
}


WORDS = ["lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit",
         "sed", "do", "eiusmod", "tempor"]


def make_png(quarter_turns: int = 0) -> bytes:
    """A synthetic colour page: lines of text cv2's Hershey font draws deterministically, plus
    a saturated bar so the RGB->grey step is exercised (PNG is a codec the port matches
    bit-exactly).

    The page is dense enough for the orientation model to be sure of it, so the same page
    turned by 90 and 180 degrees exercises the engine's own turn — the display is rotated back
    while the boxes are already upright.
    """
    img = np.full((1100, 850, 3), 255, np.uint8)
    rng = np.random.default_rng(0)
    y = 60
    for _ in range(28):
        text = " ".join(rng.choice(WORDS, int(rng.integers(4, 9))))
        cv2.putText(img, text, (50, y), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 0, 0), 2,
                    cv2.LINE_AA)
        y += 36
    cv2.rectangle(img, (50, y + 10), (800, y + 60), (30, 30, 200), -1)  # a red bar (BGR)
    if quarter_turns:
        img = np.ascontiguousarray(np.rot90(img, quarter_turns))
    ok, buf = cv2.imencode(".png", img, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    assert ok
    return buf.tobytes()


def page_json(rec: dict) -> dict:
    """The response without what cannot match: the JPEG bytes and the timing values."""
    js = dict(rec["json"])
    js["timings"] = list(js["timings"].keys())
    return {
        "page": rec["page"],
        "w": rec["w"],
        "h": rec["h"],
        "lines": rec["lines"],
        "blocks": rec["blocks"],
        "markdown": rec["markdown"],
        "source": rec["source"],
        "note": rec["note"],
        "rotation": rec["rotation"],
        "skew": rec["skew"],
        "json": js,
        # Wall clock: only the keys, and their order, are part of the output.
        "timings": list(rec["timings"].keys()),
    }


pdfs = build_all()
WORK.mkdir(parents=True, exist_ok=True)
for name, data in pdfs.items():
    (WORK / f"{name}.pdf").write_bytes(data)

inputs: dict[str, dict] = {}
for name in ["text", "rotated", "image"]:
    inputs[name] = {"kind": "pdf", "b64": base64.b64encode(pdfs[name]).decode("ascii")}
for name, turns in [("synthetic", 0), ("synthetic-90", 1), ("synthetic-180", 2)]:
    png = make_png(turns)
    (WORK / f"{name}.png").write_bytes(png)
    inputs[name] = {"kind": "image", "b64": base64.b64encode(png).decode("ascii")}

engine = Engine(MODELS, 4, False, "eng", DEFAULTS)

uploads: dict[str, int] = {}
for name, spec in inputs.items():
    got = engine.upload(base64.b64decode(spec["b64"]))
    uploads[name] = int(got["pages"])
    print(f"  {name:14s} pages={got['pages']}")

# (case name, input, page index, options)
CASES = [
    ("text-default", "text", 0, {}),
    ("text-dpi100", "text", 0, {"dpi": 100}),
    ("text-mode-text", "text", 0, {"mode": "text"}),
    ("text-mode-ocr", "text", 0, {"mode": "ocr"}),
    ("text-model-order", "text", 0, {"model_order": True}),
    ("text-no-image-ocr", "text", 0, {"image_ocr": False}),
    ("text-page-2", "text", 1, {}),
    ("rotated-default", "rotated", 0, {}),
    ("scan-default", "image", 0, {}),
    ("png-default", "synthetic", 0, {}),
    ("png-turn-90", "synthetic-90", 0, {}),
    ("png-turn-180", "synthetic-180", 0, {}),
]

cases = []
for name, source, n, opts in CASES:
    # A fresh upload per case, so a case never depends on another's id still being kept.
    uploaded = engine.upload(base64.b64decode(inputs[source]["b64"]))
    assert uploaded["pages"] == uploads[source], name
    rec = engine.page(uploaded["id"], n, opts)
    cases.append(
        {"name": name, "input": source, "n": n, "opts": opts, "page": page_json(rec)}
    )
    print(f"  {name:18s} {rec['w']:5d}x{rec['h']:<5d} {rec['source']:6s} "
          f"{len(rec['lines']):3d}L {len(rec['blocks']):3d}B rotation={rec['rotation']}")

# The two errors the page can raise: a mode outside MODES and an unknown upload.
errors = []
try:
    engine.page(engine.upload(base64.b64decode(inputs["text"]["b64"]))["id"], 0,
                {"mode": "xml"})
except Exception as e:  # noqa: BLE001 - the message is what is recorded
    errors.append({"name": "mode", "input": "text", "n": 0, "opts": {"mode": "xml"},
                   "error": str(e)})
    print(f"  mode               error {str(e)!r}")
try:
    engine.page("nope", 0, {})
except Exception as e:  # noqa: BLE001
    errors.append({"name": "unknown-upload", "input": None, "n": 0, "opts": {},
                   "error": str(e)})
    print(f"  unknown-upload     error {str(e)!r}")

# An upload that is neither a PDF nor a readable image (the reference's ValueError).
#
# Only bytes cv2 *decodes to None* are recorded. An empty upload is not one of them: there
# `cv2.imdecode` fails an assertion and the reference answers with OpenCV's own message,
# complete with the build path and version ("...loadsave.cpp:1291: error: (-215:Assertion
# failed) !buf.empty()..."). That is an OpenCV implementation detail, not behaviour worth
# reproducing, so the port answers with the readable message instead (see docs/210).
upload_errors = []
for name, raw in [("garbage", b"this is not an image at all")]:
    try:
        engine.upload(raw)
        print(f"  upload-{name:11s} NO ERROR")
    except Exception as e:  # noqa: BLE001
        upload_errors.append({"name": name, "b64": base64.b64encode(raw).decode("ascii"),
                              "error": str(e)})
        print(f"  upload-{name:11s} error {str(e)!r}")

doc = {
    "note": __doc__.strip().splitlines()[0],
    "defaults": DEFAULTS,
    "inputs": inputs,
    "uploads": uploads,
    "cases": cases,
    "errors": errors,
    "upload_errors": upload_errors,
}
text = json.dumps(doc, ensure_ascii=False, indent=1)
OUT.write_text(text, encoding="utf-8", newline="\n")
print(f"wrote {OUT.relative_to(ROOT)}: {len(text)} bytes ({len(cases)} page cases)")
