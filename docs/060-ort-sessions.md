# 060 — ONNX Runtime sessions and the model set

**Status:** done.

## Goal

Load a model folder the way `OrtOCR.__init__` does: one `SessionOptions` with the reference's
settings, the sessions the folder implies, and the metadata/vocabulary that goes with them.

## Files

- `src/Indra.Ocr.Core/Onnx/OnnxSessions.cs` — `CreateOptions`, `Open`, provider mapping.
- `src/Indra.Ocr.Core/Onnx/OcrModels.cs` — `OcrModels.Load`, the session/metadata accessors,
  `IsComplete`.
- `tools/scripts/test/gen_onnx_fixtures.py` → `fixtures/onnx_models.json`.
- `src/Indra.Ocr.Tests/OnnxModelTests.cs`, `src/Indra.Ocr.Tests/TestModels.cs`.

## Session options

Python builds **one** `ort.SessionOptions` and hands it to every `InferenceSession`;
`OcrModels.Load(dir, SessionOptions)` reproduces that (the math reader will share it in step
`200`). `Load(dir, threads, recInt8, providers)` is the convenience form, which owns the
options and disposes them with the model set.

| Python | .NET |
|---|---|
| `intra_op_num_threads = threads` | `IntraOpNumThreads = threads` |
| `inter_op_num_threads = 1` | `InterOpNumThreads = 1` |
| `graph_optimization_level = ORT_ENABLE_ALL` | `GraphOptimizationLevel.ORT_ENABLE_ALL` |
| `enable_cpu_mem_arena = False` | `EnableCpuMemArena = false` |

The arena is off on purpose: with line batches of changing shape it kept growing (onnxruntime
1.31: `rec.onnx` +1.4 GB over 10 identical runs; a 40-page scan peaked at 4.9 GB) and it
bought no measurable speed.

`providers` maps names onto `AppendExecutionProvider_*`. `CPUExecutionProvider` is a no-op (it
is ONNX Runtime's built-in default) and the default list is `["CPUExecutionProvider"]`. CUDA,
DML/DirectML, TensorRT, MIGraphX, OpenVINO and CoreML are wired up; anything else — including
the ROCm provider, which the CPU-targeting `Microsoft.ML.OnnxRuntime` package does not expose —
throws `NotSupportedException` rather than silently falling back. The CLI never sets
`--providers`, so this path exists for API parity only.

## Which models load

| file | required | session |
|---|---|---|
| `det.onnx` | yes | `Det` |
| `rec.onnx` / `rec.int8.onnx` | yes (one of) | `Rec` |
| `meta.json` | yes | `Vocab`, `VocabCjk`, `DetLongSide`, `RecHeight` |
| `mask.onnx` | no | `Mask` |
| `order.onnx` | no | `Order` |
| `orient.onnx` | no | `Orient` |
| `script.onnx` + `rec_cjk.onnx` | no (as a pair) | `Script`, `RecCjk`, `VocabCjk` |

`recInt8` defaults to "is there a `rec.int8.onnx`?" exactly like Python. Missing required
files raise `FileNotFoundException(path)` whose message is the path, matching Python's
`raise FileNotFoundError(p)`. The shipped model set has no `mask.onnx` and no math models, so
`Mask` is `null` and `HasCjk` is `true`.

`VocabCjk` is read only inside the `script`+`rec_cjk` branch, so a folder without the CJK
models does not need `vocab_cjk` in `meta.json` — the same condition Python uses.

## Tensor signatures

The port does not assume anything about the models' tensors; the fixture records what the
reference saw (`gen_onnx_fixtures.py`, onnxruntime 1.31.0):

| model | inputs | outputs |
|---|---|---|
| `det` | `x` f32 `[1,1,H,W]` | `out` f32 `[1,3,h,w]` |
| `rec` / `rec_cjk` | `x` f32 `[B,1,32,W]`, `widths` i64 `[B]` | `ids` i64 `[B,T]`, `conf` f32 `[B,T]`, `lengths` i64 `[B]` |
| `order` | `feats` f32 `[1,N,16]`, `mask` **bool** `[1,N]` | `a`, `cls` `[1,N,10]`, `same`, `tab` `[1,3,N,N2]` |
| `orient` | `x` f32 `[B,1,768,768]` | `p` f32 `[B,4]` |
| `script` | `x` f32 `[B,1,32,W]`, `widths` i64 `[B]` | `p` f32 `[B]` |

Symbolic dimensions arrive as `-1` from both bindings, so the fixture stores them that way and
the test compares `NodeMetadata.Dimensions` directly. **Output order is part of the contract**:
Python calls `session.run(None, ...)` and indexes the result positionally, so
`Every_session_runs` asserts the observed output-name order equals the model's declared order.

## Verification

- `dotnet build src/Indra.Ocr.slnx` → 0 warnings, 0 errors.
- `dotnet test src/Indra.Ocr.slnx` → 38 passed.
- `OnnxModelTests` (against `resources/models`, which is externally downloaded):
  - the session set matches the folder (`Mask` null, `Order`/`Orient`/`Script`/`RecCjk` present),
    `DetLongSide = 1024`, `RecHeight = 32`, `Vocab` 1677/1678, `VocabCjk` 9967/9968;
  - every input and output of every model matches the fixture's name, element type and shape;
  - every session actually runs (det on 64×64, rec/script/rec_cjk on 32×W, order on N=4,
    orient on 768×768) and returns its outputs in the declared order.
- The model-dependent tests are skipped (with a note to the test output) when
  `resources/models` is absent, since that folder is not in git. The signature fixture is
  committed, so the expectations stay reviewable.
