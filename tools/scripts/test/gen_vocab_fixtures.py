"""Generate the vocabulary / Unicode fixtures used by the text tests.

The reference is ``indra_ocr.vocab`` (Python 3.14, ``unicodedata`` 16.0.0). The C# port
rebuilds the vocabularies from .NET's own Unicode tables, so the fixtures pin down both the
resulting token lists and the behaviour that depends on them -- see
``docs/050-vocab.md``.
"""

from __future__ import annotations

import json
import sys
import unicodedata
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import fixtures_dir  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "resources" / "indra-src"))
from indra_ocr.vocab import (  # noqa: E402
    LATEX_COMMANDS,
    Vocab,
    build_latin_chars,
    build_multi_tokens,
    canonical,
    indic_page_ok,
    indic_valid,
)

latin = build_latin_chars()
multi = build_multi_tokens()
vocab = Vocab.multi()


def write(name: str, doc: dict) -> None:
    path = fixtures_dir() / name
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {name}: {path.stat().st_size} bytes")


# --------------------------------------------------------------------------- canonical

canonical_cases = []
for text in [
    "abc",
    "\u0218\u0219\u021a\u021b",                       # comma-below -> cedilla
    "\u015e\u015f\u0162\u0163",
    "\u25aa\u25cf\u2219\u2023\u2043",                 # bullet look-alikes
    "\u2022\u2022",
    "........", "....", "...", "..",                   # runs
    "---------", "=====", "*****", "~~~~~~",
    "\u00b7\u00b7\u00b7\u00b7",
    "._-=\u00b7*~",
    ". . . .",
    "''", "``", "''''", "````", "a''b", "a``b", "'''", "``'",
    "e\u0301", "A\u030a", "o\u0308", "n\u0303",        # NFC composition
    "\u1e0b\u0323",                                    # NFC reordering
    "caf\u00e9",
    "", " ",
]:
    canonical_cases.append({"in": text, "out": canonical(text)})

# --------------------------------------------------------------------------- tokenize / decode

tokenize_cases = []
for text in [
    "plain text",
    "we have $x _ { i } \\leq 1$ here",
    "price $5 or $10",
    "$\\alpha$",
    "$x$",
    "$ a $",
    "\\alpha x",
    "\\alpha  x",
    "a\\alphab",
    "\\ x",
    "x\\",
    "\\",
    "",
    "$\\frac{1}{2}$ and $y^2$",
    "$ a $ $b^2$",
]:
    tokenize_cases.append({"text": text, "tokens": vocab.tokenize(text)})

decode_cases = []
for text in ["", "a", "abc", "\\alpha", "\\alpha x", "\\alpha\\beta", "$ stuff"]:
    ids = vocab.encode(text)
    if ids is None:
        ids = [vocab.index[c] for c in vocab.tokenize(text) if c in vocab.index]
    decode_cases.append({"ids": ids, "text": vocab.decode(ids)})

# The recognizer's post-processing hands decode a plain id list, with blanks still in it.
decode_cases.append({"ids": [0, vocab.index["a"], 0, 0, vocab.index["b"]], "text": vocab.decode(
    [0, vocab.index["a"], 0, 0, vocab.index["b"]])})
decode_cases.append({"ids": [0, 0], "text": vocab.decode([0, 0])})
decode_cases.append({"ids": [-1, 99999, vocab.index["a"]],
                     "text": vocab.decode([-1, 99999, vocab.index["a"]])})
for first, second in [("\\alpha", "x"), ("\\alpha", "1"), ("\\alpha", "\\beta"),
                      ("\\frac", "y"), ("\\le", "z"), ("\\,", "x"), ("\\alpha", "")]:
    ids = [vocab.index[first]] + ([vocab.index[second]] if second else [])
    decode_cases.append({"ids": ids, "text": vocab.decode(ids)})

encode_cases = []
for text in ["abc", "hello world", "\\alpha x", "caf\u00e9", "Ș", "\u2603", "x\u2603y", "",
             "   ", "a\\alphab"]:
    ids = vocab.encode(text)
    encode_cases.append({"text": text, "ids": ids})
for text in ["abc", "a b", "zz", "caf\u00e9"]:
    small = Vocab(["a", "b", "c"])
    ids = small.encode(text)
    encode_cases.append({"text": text, "small": True, "ids": ids})

oov_cases = []
for text in ["abc", "hello \u2603 world", "\u0218abc", "\\alpha", "\\gamma x", "$\u2603$"]:
    oov_cases.append({"text": text, "tokens": sorted(vocab.oov(text))})

# --------------------------------------------------------------------------- Indic checks

indic_cases = []
for text in [
    "\u0939\u093f\u0928\u094d\u0926\u0940",     # हिन्दी
    "\u09ac\u09be\u0982\u09b2\u09be",           # বাংলা
    "\u093f\u093e\u092e\u0925",                 # िामथ (no consonant before the vowel sign)
    "\u0915\u093e \u093e",                      # का ा  (second vowel sign has no base)
    "\u0923\u093e\u093f\u0940",                 # णािी (two vowel signs in a row)
    "\u0915\u094d\u0937",                       # क्ष (virama after a consonant)
    "\u0915\u093c",
    "\u093e",
    "\u0901",
    "\u0915\u0902",
    "\u0902\u0915",
    "plain latin",
    "",
    "\u0915a",
    "a\u093e",
    "\u0915\u094d",
    "\u094d",
    "\u093c\u093e",
]:
    indic_cases.append({"text": text, "valid": indic_valid(text)})

indic_page_cases = []
for texts in [
    [],
    ["plain latin", "ascii only"],
    ["\u0939\u093f\u0928\u094d\u0926\u0940"],
    ["\u093f\u093e\u092e\u0925"],
    ["\u093f\u093e\u092e\u0925"] * 4,
    ["\u0939\u093f\u0928\u094d\u0926\u0940"] * 20 + ["\u093f\u093e\u092e\u0925"],
    ["\u0939\u093f\u0928\u094d\u0926\u0940"] * 10 + ["\u093f\u093e\u092e\u0925"] * 2,
]:
    indic_page_cases.append({"texts": texts, "ok": indic_page_ok(texts)})

# --------------------------------------------------------------------------- to_json

to_json_cases = []
for chars in [
    ["a", "b"],
    ["a", '"', "\\", "\n", "\t", "\x01", "\x7f"],
    ["\u00e9", "\u2022", "\\alpha"],
    [],
]:
    to_json_cases.append({"json": json.dumps({"blank": 0, "chars": chars}, ensure_ascii=False)})

# --------------------------------------------------------------------------- the real meta vocab

meta_path = Path(__file__).resolve().parents[3] / "resources" / "models" / "meta.json"
meta = json.loads(meta_path.read_text(encoding="utf-8"))
meta_vocab = Vocab.from_json(meta["vocab"])
meta_cjk = Vocab.from_json(meta["vocab_cjk"])
assert meta_vocab.chars == multi, "meta vocab is not build_multi_tokens()"
assert meta_vocab.to_json() == meta["vocab"], "to_json round-trip differs"

doc = {
    "reference": {"unicodedata": unicodedata.unidata_version},
    "counts": {"latin": len(latin), "multi": len(multi),
               "meta_vocab": len(meta_vocab.chars), "meta_cjk": len(meta_cjk.chars)},
    "latin": latin,
    "multi": multi,
    "latex_commands": LATEX_COMMANDS,
    "meta_vocab_json": meta["vocab"],
    "meta_cjk_json": meta["vocab_cjk"],
    "cjk": [
        {"text": t, "tokens": meta_cjk.tokenize(t), "ids": meta_cjk.encode(t)}
        for t in ["\u65e5\u672c\u8a9e", "abc", "\u4e2d\u6587 mixed", "\u2603"]
    ],
    "canonical": canonical_cases,
    "tokenize": tokenize_cases,
    "decode": decode_cases,
    "encode": encode_cases,
    "oov": oov_cases,
    "indic": indic_cases,
    "indic_page": indic_page_cases,
    "to_json": to_json_cases,
}
write("vocab.json", doc)
