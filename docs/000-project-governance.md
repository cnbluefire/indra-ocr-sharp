# 000 — Project governance & layout

Status: done

## Goal

Establish the repository rules, plan and documentation structure before any code.

## Deliverables

- `AGENTS.md` — authoritative record of the user's requirements and working rules
  (`resources/` read-only, NuGet ONNX runtime, four projects, tool/temp/doc layout,
  commit-per-step, the two-phase plan, and the locked parity decisions).
- `PLAN.md` — living execution plan (phases, step IDs, status, current state).
- `.gitignore` — excludes `resources/`, build output, `tools/pyenv/`, `temp/`, and native
  binaries / model weights.
- `docs/README.md` — index of step files.
- `docs/000-project-governance.md` — this file.

## Notes

- The repo had no commits yet; branch is `master`.
- `resources/indra-src` is itself a git checkout of the upstream project; it is treated as
  read-only and is not part of this repository's history.
- `resources/models` holds `det/rec/rec_cjk/order/orient/script/meta.json`. `mask.onnx` and
  the math models (`mathcls`, `math_enc`, `math_dec_init`, `math_dec_step`, `math_meta`)
  are absent and must be handled as optional the way the Python code does.
