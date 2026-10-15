"""Helpers for writing JSON fixtures that the C# tests read.

A fixture is a small JSON document under src/Indra.Ocr.Tests/fixtures/. It records
the inputs and the **expected outputs from the Python reference implementation**, so a
C# test can assert bit-parity (arrays are compared element-wise with a tolerance only
where floats are unavoidable).
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np


def fixtures_dir() -> Path:
    root = Path(__file__).resolve().parents[3]
    d = root / "src" / "Indra.Ocr.Tests" / "fixtures"
    d.mkdir(parents=True, exist_ok=True)
    return d


def to_list(a) -> list:
    """numpy array -> nested JSON lists (float32 kept as float, ints as int)."""
    arr = np.asarray(a)
    if arr.dtype == np.float32 or arr.dtype == np.float64:
        return arr.astype(float).tolist()
    if np.issubdtype(arr.dtype, np.integer):
        return arr.astype(int).tolist()
    if arr.dtype == np.bool_:
        return arr.astype(bool).tolist()
    return arr.tolist()


def write(name: str, obj: dict) -> Path:
    """Write a fixture; numpy arrays in ``obj`` are converted automatically."""
    path = fixtures_dir() / name
    clean = {k: (to_list(v) if isinstance(v, np.ndarray) else v) for k, v in obj.items()}
    path.write_text(json.dumps(clean, ensure_ascii=False, indent=1))
    print(f"wrote {path} ({path.stat().st_size} bytes)")
    return path
