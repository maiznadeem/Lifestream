"""Builds the app's Onest fonts: static, TrueType-hinted 400/500/600/700 instances from the official variable font.

Why: WPF cannot select a variable font's weight axis (it takes one default instance), and the Onest files Google Fonts serves are
unhinted, which WPF draws soft at 12 to 14 px. This script pins the upstream variable font by SHA-256, cuts the four static weights
with fontTools' instancer, and hints each one with ttfautohint (the same hinter Google Fonts uses for its hinted families).

Usage (Python 3.11+, from the repo root):
    python -m pip install --user fonttools ttfautohint-py
    python tools/fonts/build-onest.py <path to Onest[wght].ttf> src/Lifestream.Desktop/Fonts

The variable font comes from https://github.com/google/fonts/blob/4c1db3aec83c67dd223dd82a68c039cab30917a9/ofl/onest/Onest%5Bwght%5D.ttf
(SHA-256 below). The output hashes are recorded in src/Lifestream.Desktop/Fonts/README.md and checked by FontTests.
"""

import hashlib
import io
import sys
from pathlib import Path

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
import ttfautohint

SOURCE_SHA256 = "966c5c29b4755da84b6854d5c21dd4eaa2420225d0e9874de602de176d4a9f31"
WEIGHTS = {400: "Regular", 500: "Medium", 600: "SemiBold", 700: "Bold"}

# ttfautohint options. Defaults are tuned for DirectWrite/WPF ClearType: "q" (quantized) stems for grayscale and DirectWrite,
# "s" (strong) for GDI ClearType. Hinting is applied up to 50 px (larger text is smooth anyway), x-height may round up by at most
# 10 % so small sizes keep their proportions, and no "ttfautohint" info string is appended to the version (the build is reproducible).
AUTOHINT = dict(
    hinting_range_min=8,
    hinting_range_max=50,
    hinting_limit=200,
    increase_x_height=14,
    x_height_snapping_exceptions="",
    windows_compatibility=True,
    default_script="latn",
    fallback_script="none",
    symbol=False,
    no_info=True,
    gray_stem_width_mode=ttfautohint.StemWidthMode.QUANTIZED,
    gdi_cleartype_stem_width_mode=ttfautohint.StemWidthMode.STRONG,
    dw_cleartype_stem_width_mode=ttfautohint.StemWidthMode.QUANTIZED,
)


def set_names(font: TTFont, weight: int, style: str) -> None:
    """Static naming WPF groups into one family: name ID 1/2 for GDI (RIBBI), ID 16/17 for the typographic family."""
    name = font["name"]
    ribbi = style in ("Regular", "Bold")
    legacy_family = "Onest" if ribbi else f"Onest {style}"
    legacy_style = style if ribbi else "Regular"
    for record in list(name.names):
        if record.nameID in (1, 2, 3, 4, 6, 16, 17, 25):
            name.removeNames(nameID=record.nameID)
    name.setName(legacy_family, 1, 3, 1, 0x409)
    name.setName(legacy_style, 2, 3, 1, 0x409)
    name.setName(f"Onest-{style};2.001;static-hinted", 3, 3, 1, 0x409)
    name.setName(f"Onest {style}", 4, 3, 1, 0x409)
    name.setName(f"Onest-{style}", 6, 3, 1, 0x409)
    if not ribbi:
        name.setName("Onest", 16, 3, 1, 0x409)
        name.setName(style, 17, 3, 1, 0x409)
    os2 = font["OS/2"]
    os2.usWeightClass = weight
    # fsSelection: bit 0 italic, bit 5 bold, bit 6 regular; head.macStyle bit 0 bold.
    os2.fsSelection = (os2.fsSelection & ~0b1100001) | (0b100000 if style == "Bold" else 0) | (0b1000000 if style == "Regular" else 0)
    font["head"].macStyle = 1 if style == "Bold" else 0
    if "STAT" in font:
        del font["STAT"]


def build(source: Path, out_dir: Path) -> None:
    data = source.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != SOURCE_SHA256:
        sys.exit(f"Unexpected source font {source} (SHA-256 {digest}); update SOURCE_SHA256 on purpose if upstream changed.")
    out_dir.mkdir(parents=True, exist_ok=True)
    for weight, style in WEIGHTS.items():
        variable = TTFont(io.BytesIO(data))
        static = instancer.instantiateVariableFont(variable, {"wght": weight}, updateFontNames=False)
        set_names(static, weight, style)
        buffer = io.BytesIO()
        static.save(buffer)
        hinted = ttfautohint.ttfautohint(in_buffer=buffer.getvalue(), **AUTOHINT)
        target = out_dir / f"Onest-{style}.ttf"
        target.write_bytes(hinted)
        check = TTFont(target)
        assert "fpgm" in check and "prep" in check and "fvar" not in check, target
        print(f"{target.name}  {hashlib.sha256(hinted).hexdigest().upper()}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    build(Path(sys.argv[1]), Path(sys.argv[2]))
