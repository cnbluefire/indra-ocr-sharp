# The sample PDFs' font

`LiberationSans-Regular.ttf` is the font `../pdf_samples.py` embeds in every sample PDF. It is
committed on purpose — the other binary asset this repository carries is `build/smoke/latin.*`, and
this one is here for the same kind of reason (`docs/380-test-hosts.md` has the whole story).

## Why the samples embed a font at all

The samples used fpdf2's built-in `Helvetica` — one of the PDF "standard 14" fonts, which are never
embedded. pdfium therefore *substitutes a host font*, and `FPDFText_GetCharBox` answers with the
substitute's glyph boxes. Those differ between the fonts two hosts pick: the left ink edge of an `H`
at 12 pt is `20.96` on Windows and `20.924` on the Linux CI runner. The committed text-layer fixture
was therefore a statement about the machine that captured it, and `TextLayerTests` could only pass
there. With the font embedded, pdfium reads the metrics and the outlines out of the PDF itself, the
same on every host.

## Provenance

* **Font**: Liberation Sans Regular, © 2010 Google Corporation (Arimo/Tinos/Cousine) and © 2012
  Red Hat, Inc.
* **License**: SIL Open Font License 1.1 — the full text is `LICENSE` next to the font. Its
  reserved names ("Arimo", "Tinos", "Cousine", "Liberation") are why the file keeps its name;
  fpdf2's family string is only a PDF resource key, the embedded font program is unmodified.
* **Source**: `https://github.com/liberationfonts/liberation-fonts/files/7261482/liberation-fonts-ttf-2.1.5.tar.gz`
  (the `liberation-fonts-ttf-2.1.5` release of `liberationfonts/liberation-fonts`), fetched
  2026-10-17. The tarball's `LiberationSans-Regular.ttf` and `LICENSE` are committed as they came.
* **Checksums of what is committed**:
  * `LiberationSans-Regular.ttf` — `76D04C18EA243F426B7DE1F3AD208E927008F961DC5945E5AAD352D0DFDE8EE8`
  * `LICENSE` — `93FED46019C38BBE566B479D22148E2E8A1E85ADA614ACCB0211C37B2C61C19B`
* **Why this one**: it is *metric-compatible with Helvetica/Arial*, so the samples keep the layout
  they were built with (the fixes that patch the PDFs — the crop box, the rotation, the raw content
  stream — do not have to be reconsidered); it is 401 KB; it is TrueType, so fpdf2 embeds it
  directly; it is the font a Linux host substitutes for Helvetica anyway, so the captured
  text-layer values stay close to what they were; and its license allows redistribution.

Regenerating a fixture needs this file and nothing else: `pdf_samples.py` finds it next to itself,
and the tests read the font out of the PDFs' embedded subsets, so the test project does not ship it.
