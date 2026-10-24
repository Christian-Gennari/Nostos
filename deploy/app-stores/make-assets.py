#!/usr/bin/env python3
"""Build the icons and screenshots the app-store packages need.

Every image comes from files already in this repository:

- the brand kit in _brand-assets/ (rendered PNGs and the canonical SVGs);
- the README screenshots in docs/screenshots/.

Run it again after the brand kit or the screenshots change:

    python3 deploy/app-stores/make-assets.py

It needs Pillow with WebP support (python3 -c "import PIL"). It does not
render SVG: rasters are scaled down from the 1024 px brand PNGs, so every
output matches the brand kit exactly.
"""

from __future__ import annotations

import re
import shutil
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
BRAND = ROOT / "_brand-assets"
SHOTS = ROOT / "docs" / "screenshots"

FOREST = (0x29, 0x3E, 0x32)

PRIMARY_SVG = BRAND / "01_Primary_Mark" / "nostos-mark-primary.svg"
PRIMARY_PNG = BRAND / "01_Primary_Mark" / "nostos-mark-primary-1024.png"
LIGHT_PNG = BRAND / "02_Color_Variants" / "nostos-mark-monochrome-light-1024.png"
DARK_PNG = BRAND / "02_Color_Variants" / "nostos-mark-monochrome-dark-1024.png"

# The doorway mark as one even-odd path: the rounded tile, the outer arch and
# the inner doorway. Filled even-odd, the arch is cut out of the tile and the
# doorway stays filled, which is the brand kit's monochrome knockout without a
# <mask>. selfh.st recolours icons by their fill, so a single path is safest.
TILE = (
    "M205 0H819A205 205 0 0 1 1024 205V819A205 205 0 0 1 819 1024"
    "H205A205 205 0 0 1 0 819V205A205 205 0 0 1 205 0Z"
)
ARCH = (
    "M282 764L282 509A229 234 0 0 1 740 509L740 764Z"
    "M358 764L358 509A110 112 0 0 1 578 509L578 764Z"
)

# Screenshots in the order the stores show them.
SCREENSHOTS = [
    "library.png",
    "reader.png",
    "book-details.png",
    "brain-list.png",
    "writing-studio.png",
]


def resized(source: Path, size: int) -> Image.Image:
    image = Image.open(source).convert("RGBA")
    return image.resize((size, size), Image.Resampling.LANCZOS)


def on_forest(image: Image.Image) -> Image.Image:
    """Fill the transparent rounded corners with the tile colour (for JPEG)."""
    background = Image.new("RGBA", image.size, FOREST + (255,))
    background.alpha_composite(image)
    return background.convert("RGB")


def screenshot(source: Path, width: int) -> Image.Image:
    image = Image.open(source).convert("RGB")
    height = round(image.height * width / image.width)
    return image.resize((width, height), Image.Resampling.LANCZOS)


def clean_svg(path: Path) -> str:
    """The primary SVG without the XML prologue, for stores that inline it."""
    text = path.read_text(encoding="utf-8")
    return re.sub(r"^<\?xml[^>]*\?>\s*", "", text)


def knockout_svg(fill: str, title: str) -> str:
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">'
        f"<title>{title}</title>"
        f'<path fill="{fill}" fill-rule="evenodd" d="{TILE}{ARCH}"/>'
        "</svg>\n"
    )


def write(path: Path, data: str | bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(data, str):
        path.write_text(data, encoding="utf-8")
    else:
        path.write_bytes(data)
    print(f"wrote {path.relative_to(ROOT)}")


def save(image: Image.Image, path: Path, **options) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, **options)
    print(f"wrote {path.relative_to(ROOT)} {image.size[0]}x{image.size[1]}")


def unraid() -> None:
    out = HERE / "unraid"
    save(resized(PRIMARY_PNG, 512), out / "nostos-icon.png", optimize=True)


def casaos() -> None:
    out = HERE / "casaos" / "Nostos"
    write(out / "icon.svg", clean_svg(PRIMARY_SVG))
    save(resized(PRIMARY_PNG, 256), out / "icon.png", optimize=True)
    # Same size and aspect as the official store's thumbnails (1568x882, 16:9).
    poster = Image.open(SHOTS / "promo-poster.png").convert("RGB")
    save(poster.resize((1568, 882), Image.Resampling.LANCZOS), out / "thumbnail.png", optimize=True)
    for number, name in enumerate(SCREENSHOTS, start=1):
        save(screenshot(SHOTS / name, 1440), out / f"screenshot-{number}.jpg", quality=85, optimize=True)


def runtipi() -> None:
    out = HERE / "runtipi" / "apps" / "nostos" / "metadata"
    save(on_forest(resized(PRIMARY_PNG, 512)), out / "logo.jpg", quality=92, optimize=True)


def selfhst() -> None:
    out = HERE / "selfhst"
    variants = {
        "nostos": PRIMARY_PNG,
        "nostos-light": LIGHT_PNG,
        "nostos-dark": DARK_PNG,
    }
    write(out / "svg" / "nostos.svg", clean_svg(PRIMARY_SVG))
    write(out / "svg" / "nostos-light.svg", knockout_svg("#FFFFFF", "Nostos (light)"))
    write(out / "svg" / "nostos-dark.svg", knockout_svg("#1C1B1A", "Nostos (dark)"))
    for name, source in variants.items():
        image = resized(source, 512)
        save(image, out / "png" / f"{name}.png", optimize=True)
        save(image, out / "webp" / f"{name}.webp", lossless=True, method=6)


def umbrel() -> None:
    # Umbrel hosts its own icon and gallery, so the package has no images.
    # Their reviewers want a logo link in the PR body; the README points at
    # the brand kit's 1024 px PNG. Nothing to generate.
    pass


if __name__ == "__main__":
    for directory in ("casaos/Nostos", "selfhst"):
        target = HERE / directory
        if target.exists():
            for child in target.iterdir():
                if child.suffix in {".png", ".jpg", ".webp"} or child.name in {"png", "webp", "svg"}:
                    shutil.rmtree(child) if child.is_dir() else child.unlink()
    unraid()
    casaos()
    runtipi()
    selfhst()
    umbrel()
