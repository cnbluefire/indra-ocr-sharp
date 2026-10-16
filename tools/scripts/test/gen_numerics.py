"""Generate src/Indra.Ocr.Tests/fixtures/numerics.json from numpy for step 020."""

from __future__ import annotations

import base64
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import write  # noqa: E402

rng = np.random.default_rng(20240520)


def b64(a) -> str:
    return base64.b64encode(np.ascontiguousarray(a).tobytes()).decode("ascii")


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

# ------------------------------------------------------------------ np.exp, float32
# numpy evaluates exp with its own vector kernel rather than libm, so the port reproduces
# the kernel bit-for-bit (see docs/070-det-post.md).  The corpus below mixes the cases that
# discriminate the kernel from expf: exact quadrant ties (x*log2e a half-integer), the
# denormal scalef branch near -125*ln2, the overflow/underflow boundaries and broad samples.

LOG2E_F32 = np.float32(1.442695040888963407359924681001892137)
XMAX_F32 = np.float32(88.72283935546875)
XMIN_F32 = np.float32(-103.97208404541015625)

ties = []
for h in np.arange(-150.5, 131.5, 1.0):
    cur = np.float32(h / float(LOG2E_F32))
    for _ in range(6):
        cur = np.nextafter(cur, np.float32(-1e30), dtype=np.float32)
    for _ in range(13):
        if np.float32(cur * LOG2E_F32) == np.float32(h):
            ties.append(cur)
        cur = np.nextafter(cur, np.float32(1e30), dtype=np.float32)

specials = np.asarray(
    [
        np.float32(0.0), np.float32(-0.0), np.float32(np.inf), np.float32(-np.inf), np.float32(np.nan),
        XMAX_F32, np.nextafter(XMAX_F32, np.float32(0.0), dtype=np.float32),
        XMIN_F32, np.nextafter(XMIN_F32, np.float32(0.0), dtype=np.float32),
        np.float32(-86.6434), np.float32(-86.64337),      # around -125 * ln2
        np.float32(-86.64339), np.float32(-86.64341),
        np.float32(1.0), np.float32(-1.0), np.float32(1e-8), np.float32(-1e-8),
        np.float32(700.0), np.float32(-700.0),
    ],
    np.float32,
)

exp_x = np.concatenate(
    [
        np.asarray(ties, np.float32),
        specials,
        rng.normal(0, 2, 1000).astype(np.float32),
        rng.uniform(-104.0, 88.7, 1000).astype(np.float32),
        rng.uniform(-1.0, 1.0, 1000).astype(np.float32),
        rng.integers(0, 1 << 32, 300, dtype=np.uint64).astype(np.uint32).view(np.float32),
        np.linspace(-104.5, 89.5, 1500).astype(np.float32),
        np.linspace(-88.0, -86.0, 800).astype(np.float32),
    ]
)
with np.errstate(over="ignore", invalid="ignore"):
    exp_y = np.exp(exp_x)          # the specials below include inf/nan and both boundaries
assert exp_x.dtype == np.float32 and exp_y.dtype == np.float32

# ------------------------------------------------------------------ float32 reductions
# numpy's contiguous float32 sum/mean is the *pairwise* tree (npy_pairwise_sum_FLOAT: a
# plain sum below 8 elements, eight accumulators up to PW_BLOCKSIZE=128, then a bisection
# whose split is rounded down to a multiple of 8).  The tree is order-dependent, so the
# port reproduces it exactly; the sizes below straddle 8, 128 and the multiples of 8
# where a different split shows in the last bits.  (A fresh rng: the sections above must
# keep their values.)

sum_rng = np.random.default_rng(20260616)
sum_cases = []
for n in (1, 7, 8, 9, 15, 16, 127, 128, 129, 135, 136, 137, 200, 1000, 4096):
    x = sum_rng.uniform(-1.0, 1.0, n).astype(np.float32)
    sum_cases.append(
        {
            "n": n,
            "x": b64(x),
            "sum_bits": int(np.float32(x.sum()).view(np.uint32)),
            "mean_bits": int(np.float32(x.mean()).view(np.uint32)),
        }
    )
sum_rows = sum_rng.uniform(0.0, 1.0, (9, 257)).astype(np.float32)

# The float64 tree is the same shape (npy_pairwise_sum_DOUBLE: the eight accumulators are
# doubles).  `math.MathReader.read` computes its confidence with `np.mean` over a Python list
# of floats, which numpy turns into a float64 array first, so this is the reduction that
# matters there.
sum64_cases = []
for n in (1, 7, 8, 9, 15, 16, 127, 128, 129, 135, 136, 137, 200, 1000, 4096):
    x = sum_rng.uniform(-1.0, 1.0, n)
    sum64_cases.append(
        {
            "n": n,
            "x": b64(x),
            "sum_bits": int(np.float64(x.sum()).view(np.uint64)),
            "mean_bits": int(np.float64(x.mean()).view(np.uint64)),
        }
    )
sum64_rows = sum_rng.uniform(0.0, 1.0, (9, 257))

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
        "exp_f32": {"n": int(exp_x.size), "x": b64(exp_x), "y": b64(exp_y)},
        "sum32": sum_cases,
        "sum32_rows": {
            "shape": list(sum_rows.shape),
            "x": b64(sum_rows),
            "sum_bits": [int(v) for v in sum_rows.sum(1).view(np.uint32)],
        },
        "sum64": sum64_cases,
        "sum64_rows": {
            "shape": list(sum64_rows.shape),
            "x": b64(sum64_rows),
            "mean_bits": [int(v) for v in sum64_rows.mean(1).view(np.uint64)],
        },
    },
)
