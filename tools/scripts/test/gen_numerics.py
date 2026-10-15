"""Generate tests/Indra.Ocr.Tests/fixtures/numerics.json from numpy for step 020."""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import write  # noqa: E402

rng = np.random.default_rng(20240520)

median_cases = []
for n in (1, 2, 3, 8, 9, 15):
    a = np.round(rng.normal(0, 5, n).astype(np.float32), 3)
    median_cases.append({"input": a.tolist(), "expected": float(np.median(a))})

percentile_cases = []
for q in (0, 10, 25, 50, 90, 100):
    a = rng.normal(20, 7, 41)
    percentile_cases.append({"input": a.tolist(), "q": q, "expected": float(np.percentile(a, q))})

argsort_cases = []
for n in (1, 2, 5, 16, 40):
    a = np.round(rng.normal(0, 1, n).astype(np.float32), 4)
    argsort_cases.append({"input": a.tolist(), "expected": np.argsort(a).tolist()})
# a case with explicit ties (stable for small n; documents the tie policy)
ties = np.array([3, 1, 3, 2, 1, 3, 0], np.float32)
argsort_ties = {"input": ties.tolist(), "expected": np.argsort(ties).tolist()}

argmax_cases = []
for n in (1, 4, 17):
    a = np.round(rng.normal(0, 1, n).astype(np.float32), 4)
    argmax_cases.append({"input": a.tolist(), "argmax": int(np.argmax(a)), "argmin": int(np.argmin(a))})

bincount_cases = [
    {"x": [0, 1, 1, 3, 3, 3], "minlength": 0, "expected": np.bincount([0, 1, 1, 3, 3, 3]).tolist()},
    {"x": [2, 0], "minlength": 5, "expected": np.bincount([2, 0], minlength=5).tolist()},
]
bincount_weighted = []
for _ in range(3):
    x = rng.integers(0, 6, 20).tolist()
    w = np.round(rng.uniform(0, 1, 20).astype(np.float32), 4)
    bincount_weighted.append(
        {"x": x, "w": w.tolist(), "minlength": 6, "expected": np.bincount(x, weights=w, minlength=6).tolist()}
    )

logaddexp_cases = []
for x, y in [(0.0, 0.0), (0.0, -2.5), (-3.0, -0.5), (5.0, -5.0), (-1e9, 0.0)]:
    logaddexp_cases.append({"x": x, "y": y, "expected": float(np.logaddexp(x, y))})

cumsum_case = {"input": rng.normal(0, 3, 12).tolist(), "expected": np.cumsum(rng.normal(0, 3, 12)).tolist()}
# build cumsum consistently
c_in = rng.normal(0, 3, 12)
cumsum_case = {"input": c_in.tolist(), "expected": np.cumsum(c_in).tolist()}

sorted_arr = np.sort(rng.normal(0, 1, 25))
search_cases = []
for v in (-2.0, 0.0, 0.0001, 1.0, 3.0):
    search_cases.append(
        {"sorted": sorted_arr.tolist(), "v": float(v), "left": int(np.searchsorted(sorted_arr, v, "left")),
         "right": int(np.searchsorted(sorted_arr, v, "right"))}
    )

A = np.round(rng.normal(0, 1, (4, 3)), 4)
B = np.round(rng.normal(0, 1, (3, 5)), 4)
matmul_case = {"a": A.tolist(), "b": B.tolist(), "expected": (A @ B).tolist()}
A32, B32 = A.astype(np.float32), B.astype(np.float32)
matmulf_case = {"a": A32.tolist(), "b": B32.tolist(), "expected": (A32 @ B32).astype(float).tolist()}

write(
    "numerics.json",
    {
        "median": median_cases,
        "percentile": percentile_cases,
        "argsort": argsort_cases,
        "argsort_ties": argsort_ties,
        "argmax": argmax_cases,
        "bincount": bincount_cases,
        "bincount_weighted": bincount_weighted,
        "logaddexp": logaddexp_cases,
        "cumsum": cumsum_case,
        "searchsorted": search_cases,
        "matmul": matmul_case,
        "matmulf": matmulf_case,
    },
)
