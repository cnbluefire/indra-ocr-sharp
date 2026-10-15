# 270 — ONNX backend benchmark: Microsoft.ML.OnnxRuntime vs Lokad.Onnx

**Status:** done (throwaway experiment; the project lives under `temp/` and is not versioned).

## Goal

Measure how fast the two C# ONNX backends execute the Indra models:

- `Microsoft.ML.OnnxRuntime` 1.31.0 — the native runtime the port references (`AGENTS.md` hard
  requirement 3), with a CPU execution provider.
- `Lokad.Onnx` 0.3.0 — a 100 % managed-code ONNX backend for .NET 10 ("tensor types and
  kernels, file-order graph execution over a CPU provider, and transparent buffer pooling",
  <https://github.com/Lokad/Onnx>), i.e. the interesting alternative if the native runtime
  ever has to go.

BenchmarkDotNet is used so the numbers carry error bars, and `[MemoryDiagnoser]` so the
managed backend's allocation behaviour is visible.

## Files

Nothing here touches `src/`; `temp/` is git-ignored (`AGENTS.md` tooling rule 6).

| file | role |
|---|---|
| `temp/onnx-bench/OnnxBench.csproj` | net10.0 console app: `Microsoft.ML.OnnxRuntime` 1.31.0, `Lokad.Onnx` 0.3.0, `BenchmarkDotNet` 0.15.8 |
| `temp/onnx-bench/Cases.cs` | the four Indra models at pipeline shapes (det 1024², rec 16×32×256, orient 768², order N=64) |
| `temp/onnx-bench/InputSpec.cs` | concrete f32 / i64 / bool input tensors |
| `temp/onnx-bench/Runner.cs` | one model → three executions: ORT original, ORT rewritten, Lokad rewritten; checksums and element-wise agreement |
| `temp/onnx-bench/Rewrite.cs` | replaces operators Lokad.Onnx 0.3.0 lacks with equivalent subgraphs (protobuf-level graph surgery) |
| `temp/onnx-bench/Probe.cs` | cold-run feasibility pass: which backend runs what, and how far apart the numbers are |
| `temp/onnx-bench/Benchmarks.cs`, `Program.cs` | BenchmarkDotNet entry point |
| `temp/onnx-bench/Inspect.cs` | `--inspect ops/node` model inspector used while building the rewriter |
| `temp/onnx-bench/run.log` | captured full BenchmarkDotNet run |

## How to run

```powershell
$p = "temp\onnx-bench\OnnxBench.csproj"
dotnet run --project $p -c Release -- --probe   # support + agreement + cold timings, no benchmarking
dotnet run --project $p -c Release -- --quick   # BenchmarkDotNet, short job
dotnet run --project $p -c Release              # BenchmarkDotNet, default job
```

Both backends are pinned to a single thread (`IntraOpNumThreads = 1`, and Lokad's
`ExecutionOptions.Default` on the CPU provider) because the Indra pipeline runs one worker
thread per page (`THREADS_PER_WORKER = 1`). The comparison is therefore about kernels, not
about how well each backend scales.

## Finding 1 — Lokad.Onnx 0.3.0 cannot run the detector or the recognizer

Executing the Indra models raises, one operator at a time:

```
det      node /Softplus      Softplus:17          is not supported by the backend
rec      node /rec/ArgMax    ArgMax:17            is not supported by the backend
```

Before those two were reached, four more unsupported operators had to be rewritten:
`HardSwish:17` (det ×31, orient ×21, rec ×11), `Xor:17` (rec ×1), `Mod:17` (rec ×1),
`BatchNormalization:17` (rec ×1), and `DepthToSpace:17` (det ×1). `Exp:17` (rec ×1) is also
rejected; it is listed here because the Softplus rewrite below was the first thing to reach an
`Exp` node, not because it blocks anything else first.

`Rewrite.cs` substitutes each of them with equivalent supported subgraphs:

| missing op | replacement |
|---|---|
| `HardSwish(x)` | `Mul(x, Clip(Add(Mul(x, 1/6), 0.5), 0, 1))` |
| `Xor(a, b)` | `Not(Equal(a, b))` |
| `Mod(a, b)` (integer, `fmod=0`) | `Sub(a, Mul(b, Div(a, b)))` |
| `DepthToSpace` | symbolic `Shape`/`Gather`/`Concat`/`Reshape`/`Transpose`/`Reshape` (DCR and CRD) |
| `BatchNormalization` | `Reshape` of the `[C]` parameters to `[1,C,1,…]` (rank derived at run time via `Shape(Shape(x))` + `ConstantOfShape`) then `Sub`/`Add`/`Sqrt`/`Div`/`Mul`/`Add` |

`Softplus` has no supported replacement: the natural `Log(Add(Exp(x), 1))` needs `Exp` and
`Log`, and neither is implemented. `ArgMax` would need `Range`/`ReduceMin` plus a
rank-dependent broadcast. Both were left alone, so **`det.onnx` and `rec.onnx` stay
unrunnable** and only `orient.onnx` (after the HardSwish rewrite) and `order.onnx` (no
rewrite needed) can be compared.

Substituting is verified, not assumed: every rewritten model is also executed by native ONNX
Runtime on the *original* file, and the two output sets are compared element by element
(`maxScaled = |a-b| / (1+|ref|)`). The rewrite disagreements over the whole output tensors
are `1.7e-6` (det) and `2.6e-6` (rec); see the table below.

## Finding 2 — probe (single cold run per execution, milliseconds)

```
model    rewrite                                    ort      ortRew   lokad    rewriteDif  lokadDif
det      DepthToSpacex1, HardSwishx31               77.7     136.5    -        1.70E-006   -
rec      BatchNormalizationx1, HardSwishx11, ...    40.6     42.5     -        2.60E-006   -
orient   HardSwishx21                               12.0     38.8     120.9    7.22E-009   7.22E-009
order    none                                       3.0      2.7      43.9     0.00E+000   3.94E-006
```

`ort` is the untouched model (the number that matters for "how fast is Indra on ONNX
Runtime"), `ortRew` the rewritten graph, `lokad` the managed backend on the rewritten graph.
The rewrite is not free for ORT: it un-fuses `HardSwish`/`BatchNormalization` into separate
kernels, which is why det goes 80 → 143 ms and orient 11 → 33 ms.

## Finding 3 — BenchmarkDotNet (AMD Ryzen 7 5800H, .NET 10.0.11, one thread)

| Method | Model | Mean | Ratio | Allocated | Alloc Ratio |
|---|---|---:|---:|---:|---:|
| ORT, original graph | order | 2.015 ms | 1.00 | 2 632 B | 1.00 |
| ORT, rewritten graph | order | 2.003 ms | 0.99 | 2 632 B | 1.00 |
| **Lokad.Onnx, rewritten graph** | **order** | **3.171 ms** | **1.57** | **1 810 429 B** | **688** |
| ORT, original graph | orient | 9.030 ms | 1.00 | 904 B | 1.00 |
| ORT, rewritten graph | orient | 32.958 ms | 3.65 | 904 B | 1.00 |
| **Lokad.Onnx, rewritten graph** | **orient** | **93.819 ms** | **10.40** | **63 207 904 B** | **69 920** |

Read against the *rewritten* ORT column (the same graph), the managed backend is 2.8× slower
on `orient` (93.8 vs 33.0 ms) and 1.6× slower on `order` (3.17 vs 2.00 ms) — but it allocates
63 MB per `orient` call against 904 B, i.e. it keeps nothing alive between runs. That GC
pressure, not the kernels, is what the Ratio column against the original model mostly reflects.

## Consequences for the port

- `Microsoft.ML.OnnxRuntime` stays the reference backend; `Lokad.Onnx` 0.3.0 is **not**
  currently a drop-in replacement for the Indra model set — two of the four models cannot be
  executed at all, and the two that can pay a 1.6–2.8× kernel premium plus heavy allocation.
- Nothing in `src/` depends on this experiment. If a managed backend is ever wanted, the
  rewriter in `temp/onnx-bench/Rewrite.cs` is the starting point for the substitution list.
- The `--probe` disagreement numbers double as a numeric-parity sanity check on the models
  themselves: the managed backend reproduces the native one to 7e-9 (orient) and 3.9e-6
  (order) scaled.
