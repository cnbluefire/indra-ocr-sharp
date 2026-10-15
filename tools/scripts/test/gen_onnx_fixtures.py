"""Generate the ONNX model-signature fixture.

Records what the Python reference (onnxruntime 1.31.0) sees for each model file, so the C#
port's ``OcrModels`` must expose the same tensors through ``Microsoft.ML.OnnxRuntime``. The
model files themselves are externally downloaded (``resources/models``), so the test that
uses this fixture skips when they are absent.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from refutil import fixtures_dir  # noqa: E402

import onnxruntime as ort  # noqa: E402

REPO = Path(__file__).resolve().parents[3]
MODELS = REPO / "resources" / "models"

OPTIONAL = ("mask", "order", "orient", "script", "rec_cjk")


def shape_of(dims) -> list[int]:
    return [d if isinstance(d, int) else -1 for d in dims]


so = ort.SessionOptions()
so.intra_op_num_threads = 1
so.inter_op_num_threads = 1
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
so.enable_cpu_mem_arena = False

models = {}
for name in ("det", "rec", *OPTIONAL):
    path = MODELS / f"{name}.onnx"
    if not path.exists():
        continue
    session = ort.InferenceSession(str(path), so, providers=["CPUExecutionProvider"])
    models[name] = {
        "inputs": [
            {"name": i.name, "type": i.type, "shape": shape_of(i.shape)}
            for i in session.get_inputs()
        ],
        "outputs": [
            {"name": o.name, "type": o.type, "shape": shape_of(o.shape)}
            for o in session.get_outputs()
        ],
    }

meta = json.loads((MODELS / "meta.json").read_text(encoding="utf-8"))
doc = {
    "reference": {"onnxruntime": ort.__version__},
    "meta": {"det_long_side": meta["det_long_side"], "rec_height": meta["rec_height"]},
    "vocab_json_len": len(meta["vocab"]),
    "vocab_cjk_json_len": len(meta["vocab_cjk"]),
    "present": {name: (MODELS / f"{name}.onnx").exists() for name in ("rec", "rec.int8", *OPTIONAL)},
    "models": models,
}

path = fixtures_dir() / "onnx_models.json"
path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
print(f"wrote {path}: {path.stat().st_size} bytes, models = {sorted(models)}")
