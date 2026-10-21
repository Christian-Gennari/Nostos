# PDF fixtures

`shared-cff-font.cff` is a CFF font program of 1,200 generated polygon glyphs,
made for `PdfSharedFontMemoryTests` by `tools/fixtures/make_shared_cff_font.py`
(fontTools). Nothing in it comes from an existing typeface, so it carries no
third-party licence. Regenerate with:

    pip install --target <dir> fonttools
    PYTHONPATH=<dir> python3 tools/fixtures/make_shared_cff_font.py \
      Nostos.Backend.Tests/Fixtures/pdf/shared-cff-font.cff 1200
