# 250 — the reference's own unit tests, ported

**Status:** done.

## Goal

The reference ships two test modules — `tests/test_inputs.py` (10 cases) and
`tests/test_textlayer.py` (17) — and this step makes the port answer to all 27. They are the
reference's *internal* checks (its own fixtures and stand-ins, not a command line), so they belong
in `Indra.Ocr.Tests`: 26 of them are ported case for case, and `test_version_flag` stays with the
tests that already cover the command-line surface it is about.

## Where each case went

| Reference (`resources/indra-src/tests/`) | Port |
|---|---|
| `test_inputs.py::test_file_type_comes_from_the_bytes` | `ReferenceInputsTests.File_type_comes_from_the_bytes` |
| `test_inputs.py::test_every_tiff_frame_is_a_page` | `ReferenceInputsTests.Every_tiff_frame_is_a_page` |
| `test_inputs.py::test_unreadable_files_raise_input_error` | `ReferenceInputsTests.Unreadable_files_raise_input_error` |
| `test_inputs.py::test_password_protected_pdf` | `ReferenceInputsTests.Password_protected_pdf` |
| `test_inputs.py::test_a_failing_page_does_not_end_the_document` | `ReferenceInputsTests.A_failing_page_does_not_end_the_document` |
| `test_inputs.py::test_huge_pages_render_below_the_pixel_cap` | `ReferenceInputsTests.Huge_pages_render_below_the_pixel_cap` |
| `test_inputs.py::test_parallel_plan_has_one_unit_per_page` | `ReferenceInputsTests.Parallel_plan_has_one_unit_per_page` |
| `test_inputs.py::test_model_errors_are_one_clear_message` | `ReferenceInputsTests.Model_errors_are_one_clear_message` |
| `test_inputs.py::test_cached_models_are_checked_once_a_day` | `ReferenceInputsTests.Cached_models_are_checked_once_a_day` |
| `test_inputs.py::test_version_flag` | *not ported*: `ArgparseTests.Version_prints_the_product_line`, `ProductSmokeTests` and the `version` case of `cli_parity.py` (see below) |
| `test_textlayer.py::test_bad_chars_count_replacement_and_private_use` | `ReferenceTextLayerTests.Bad_chars_count_replacement_and_private_use` |
| `test_textlayer.py::test_two_columns_in_reading_order_are_coherent` | `ReferenceTextLayerTests.Two_columns_in_reading_order_are_coherent` |
| `test_textlayer.py::test_interleaved_columns_are_not_coherent` | `ReferenceTextLayerTests.Interleaved_columns_are_not_coherent` |
| `test_textlayer.py::test_lines_written_bottom_up_are_not_coherent` | `ReferenceTextLayerTests.Lines_written_bottom_up_are_not_coherent` |
| `test_textlayer.py::test_cli_source_flags` | `ReferenceTextLayerTests.Cli_source_flags` |
| `test_textlayer.py::test_runs_go_to_the_smallest_containing_line_in_x_order` | `ReferenceTextLayerTests.Runs_go_to_the_smallest_containing_line_in_x_order` |
| `test_textlayer.py::test_a_run_across_two_cells_splits_between_them` | `ReferenceTextLayerTests.A_run_across_two_cells_splits_between_them` |
| `test_textlayer.py::test_runs_of_a_right_to_left_line_go_right_to_left` | `ReferenceTextLayerTests.Runs_of_a_right_to_left_line_go_right_to_left` |
| `test_textlayer.py::test_ocr_only_reads_scripts_in_its_vocab` | `ReferenceTextLayerTests.Ocr_only_reads_scripts_in_its_vocab` |
| `test_textlayer.py::test_lines_with_hanzi_go_to_the_cjk_recognizer` | `ReferenceTextLayerTests.Lines_with_hanzi_go_to_the_cjk_recognizer` |
| `test_textlayer.py::test_a_turned_page_is_turned_upright_before_detection` | `ReferenceTextLayerTests.A_turned_page_is_turned_upright_before_detection` |
| `test_textlayer.py::test_fast_average_linkage_matches_the_brute_force` | `ReferenceTextLayerTests.Fast_average_linkage_matches_the_brute_force` |
| `test_textlayer.py::test_skew_estimate_recovers_a_tilt` | `ReferenceTextLayerTests.Skew_estimate_recovers_a_tilt` |
| `test_textlayer.py::test_cjk_lines_join_without_a_space_and_bare_bullets_start_items` | `ReferenceTextLayerTests.Cjk_lines_join_without_a_space_and_bare_bullets_start_items` |
| `test_textlayer.py::test_unrotate_box_maps_a_deskewed_box_back` | `ReferenceTextLayerTests.Unrotate_box_maps_a_deskewed_box_back` |
| `test_textlayer.py::test_math_tokens_back_to_text` | `ReferenceTextLayerTests.Math_tokens_back_to_text` |
| `test_textlayer.py::test_math_models_are_optional` | `ReferenceTextLayerTests.Math_models_are_optional` |

Each ported method carries the reference's name in its `<summary>`, and the reasons for every
adaptation are in the class `<remarks>` (and repeated where the adaptation is local). The mapping
is 1:1 in *what is asserted*, not in the stand-ins: the reference reaches inside its objects
(`OrtOCR.__new__`, monkeypatched `snapshot_download`, `pypdfium2`-made PDFs, `cv2`-made images),
and the port reaches for the seams it has.

## The seams this needed

| Seam | Why |
|---|---|
| `InternalsVisibleTo` on `Indra.Ocr.Core` | `LayoutPost.IsItem` and the two test files are internal. |
| `LayoutPost.IsItem` private → internal | `test_cjk_lines_join_without_a_space_and_bare_bullets_start_items` calls the reference's `_is_item` directly. |
| `Cli.Plan` private → internal | `test_parallel_plan_has_one_unit_per_page` is about `parallel._plan`, which no public API exposes (the reference's test imports it too). |
| `HF_ENDPOINT` honoured by `ModelDownload` | The reference monkeypatches `snapshot_download`; `huggingface_hub.constants.ENDPOINT` reads this variable, so a local endpoint is the port's equivalent — and it is a parity fix (see below). |

## Adaptations, case by case

- **`StubOCR` → `SafePage.Read`** (`test_a_failing_page_does_not_end_the_document`). The
  reference's converter takes a callable, so its test makes page 2 raise on an all-black page. The
  port's `OrtOcr` owns its ONNX sessions and cannot be swapped; `SafePage.Read` is the wrapper every
  page and every image frame goes through, so it is driven directly: the failing page becomes an
  error page (`source == "error"`, the note, `markdown == ""`, `total_ms == 0.0`) and the pages
  around it still convert. The assertion sequence is the reference's.
- **`OrtOCR.__new__` → the real model set** (`test_ocr_only_reads_scripts_in_its_vocab`,
  `test_lines_with_hanzi_go_to_the_cjk_recognizer`, `test_a_turned_page_is_turned_upright_before_detection`).
  The reference builds a half-initialised `OrtOCR` and fills in its fields; the port constructs the
  real thing. `Recognizer` takes `IOrientModel`/`IRecModel`/`IScriptModel`, so the recognizer cases
  pass the same kind of stand-in the reference does (`Rec` → `FixedRec`, `Script` → `WideScript`,
  `Orient` → `FixedOrient`), while the vocabulary cases run over `resources/models` (and skip when
  it is missing).
- **`vocab_cjk` unset → the shipped set has it.** The reference's vocabulary stand-in starts without
  a CJK vocabulary and sets one for its last assertion; the port's model set carries `rec_cjk`, so
  the CJK assertion is the reference's last one. The scripts-outside-the-vocabulary assertions
  (Latin, Arabic, digits) are unchanged.
- **`numpy.random.default_rng` → `System.Random`** (`test_fast_average_linkage_matches_the_brute_force`,
  `test_skew_estimate_recovers_a_tilt`). The reference seeds numpy so the case is reproducible; the
  port seeds .NET's generator instead. What is asserted is a property (the fast average-linkage
  clusters equal the brute force's; a tilt is recovered within a tolerance), not a particular
  matrix, so the seed only has to make the run deterministic. The cluster case is symmetrised
  (`(L + L.T) / 2`) and narrowed to float32 the way the port's `OrderPost.Cluster` receives it, and
  the brute force runs on the same narrowed values.
- **`pypdfium2`-made PDFs → `fixtures/document.json`** (`test_file_type_comes_from_the_bytes`,
  `test_password_protected_pdf`, `test_huge_pages_render_below_the_pixel_cap`,
  `test_parallel_plan_has_one_unit_per_page`). The port cannot write PDFs, so the samples come from
  the fixture (a two-page `text`, a `locked` one whose password is `secret`, `broken`/`corrupt`).
  Where a *count* was asserted (five pages) the fixture has two, so the case asserts the shape and,
  separately, that `max_pages` caps it. The pixel-cap case asserts the poster arithmetic on the same
  call plus a real page's scale; the 612x792 letter case is pinned by
  `DocumentTests.Page_scale_matches_the_reference`.
- **`cv2`/`PIL`-made images → ImageSharp** (`test_every_tiff_frame_is_a_page`). The frames are
  written with the codec the port already has (three 60x80 frames of 255/200/255), and are asserted
  by count, size and value rather than as one exact blob.
- **`Converter` → the host's dispatch** (`test_unreadable_files_raise_input_error`). `document.convert`
  checks the file exists, then lets the bytes pick the reader; the test's `Read` is that dispatch
  without a recognizer, so the `InputError` and its wording are the ones the CLI produces.
- **`monkeypatched snapshot_download` → a local `HF_ENDPOINT`** (the two model cases). A
  `TcpListener` on a loopback port accepts connections and closes them at once: a request fails (as
  it would with no network) and the attempt is counted, which is what "was the Hub checked?" means.
  The reference's call-count assertions become connection-count assertions.
- **The command-line half of `test_model_errors_are_one_clear_message`** (one line on stderr, exit
  1) and **`test_version_flag`** are about a host's output, so they are covered where that output
  already is: `cli_parity.py` (`version`, `nofolder`), `ArgparseTests.Version_prints_the_product_line`
  and `ProductSmokeTests`.

## Two parity fixes the porting found

1. **`HF_ENDPOINT` was ignored.** `huggingface_hub.constants.ENDPOINT` is
   `os.getenv("HF_ENDPOINT", "https://huggingface.co").rstrip("/")`, so the reference talks to a
   mirror (or a test endpoint) when the variable is set; the port always used the public host. It
   now reads `HF_ENDPOINT` (trailing slashes trimmed) — a real parity fix, and the seam the two
   model cases above need.
2. **The daily stamp was touched on the offline path.** The reference's `model_dir` touches
   `_check_stamp` only after the *online* `snapshot_download` succeeds; when the Hub cannot be
   reached it returns the cached snapshot **without** stamping, so the next run checks again. The
   port's `ModelDownload.Download` kept the cached models but returned normally, and
   `ModelFolder.Resolve` then stamped — so after one offline run the port went quiet for a day where
   the reference keeps retrying. `Download` now returns whether the Hub was reached, and the stamp
   is touched only then. (Not observable in the CLI's output, which is why no earlier step caught
   it; `Cached_models_are_checked_once_a_day` asserts the stamp is not created by a failed check.)

   > **Superseded by step 385** (`docs/385-model-fetch.md`). Measured against the installed
   > `huggingface_hub` 2.2.0, the reading above is not what happens: when the Hub cannot be reached,
   > `snapshot_download` *returns* the cached folder through its own internal fallback, `_touch`
   > runs, and the reference **does** go quiet for a day — the port's behaviour *before* this step
   > was the right one. `ModelFolder.Resolve` stamps whenever the download returns again (the case
   > that does not stamp is a failure that travels out, i.e. a file fetch after a successful
   > listing), and `Cached_models_are_checked_once_a_day` asserts that instead. What this step's
   > `Download`-returns-whether-the-Hub-was-reached distinction is still good for is the caller's
   > information, and the smoke test's `served=` line.

## Verification

- `dotnet build src\Indra.Ocr.slnx` — 0 warnings, 0 errors.
- `dotnet test src\Indra.Ocr.slnx` — **203** tests green (177 before; the two ported modules add 26).
- `tools/scripts/test/cli_parity.py` — **83/83**; `viewer_cli_parity.py` — **20/20**;
  `viewer_parity.py` — **11/11** (the `HF_ENDPOINT` and stamp changes touch the model-folder path
  that `nofolder` and `out-bad` exercise).
