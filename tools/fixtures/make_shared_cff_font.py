"""Builds the synthetic CFF font program for PdfSharedFontMemoryTests.

Usage: make_shared_cff_font.py <out.cff> [glyph count]

It has 1,200 detailed glyphs by default.

The glyphs are generated polygons, so the font is ours to commit with no
licence question. Its size is what matters: a PDF reader that parses one copy
of it per page-level font retains that copy for every page.
"""
import math
import random
import sys

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen

GLYPHS = int(sys.argv[2]) if len(sys.argv) > 2 else 1200
POINTS = 48
random.seed(1111)

names = [".notdef"] + [f"g{i:04d}" for i in range(1, GLYPHS)]
fb = FontBuilder(1000, isTTF=False)
fb.setupGlyphOrder(names)
fb.setupCharacterMap({0x41 + (i % 26): names[1 + (i % 26)] for i in range(26)})
charstrings = {}
for name in names:
    pen = T2CharStringPen(600, None)
    if name != ".notdef":
        cx, cy = 300, 350
        pts = []
        for k in range(POINTS):
            angle = 2 * math.pi * k / POINTS
            radius = 150 + random.randint(0, 120)
            pts.append((int(cx + radius * math.cos(angle)), int(cy + radius * math.sin(angle))))
        pen.moveTo(pts[0])
        for p in pts[1:]:
            pen.lineTo(p)
        pen.closePath()
    charstrings[name] = pen.getCharString()
fb.setupCFF("NostosLeakFixture", {"FullName": "Nostos Leak Fixture"}, charstrings, {})
fb.setupHorizontalMetrics({n: (600, 0) for n in names})
fb.setupHorizontalHeader(ascent=800, descent=-200)
fb.setupNameTable({"familyName": "Nostos Leak Fixture", "styleName": "Regular"})
fb.setupOS2()
fb.setupPost()

data = fb.font.getTableData("CFF ")
open(sys.argv[1], "wb").write(data)
print(len(data), "bytes,", GLYPHS, "glyphs")
