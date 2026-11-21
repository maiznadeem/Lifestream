"""Builds the app's Google Sans Flex fonts: static, TrueType-hinted 400/500/600/700 instances cut from the official variable font.

Why: WPF cannot select an instance of a variable font (it takes the default one), and the file Google publishes is unhinted (no fpgm, no glyph
instructions), which WPF draws ragged in the whole-pixel text layout the app uses. This script pins the upstream variable font by SHA-256, fixes
every axis (AXES below, reasons in src/Lifestream.Desktop/Fonts/README.md), names the four files so WPF groups them as one family,
"Google Sans Flex", and hints each one with ttfautohint (the auto-hinter Google Fonts uses for its hinted families).

Usage (Python 3.11+, from the repo root; -I keeps the downloaded font away from the interpreter's search path):
    python -m pip install fonttools==4.68.0 ttfautohint-py==0.6.1
    python -I tools/fonts/build-google-sans-flex.py "<path to GoogleSansFlex[ROND,opsz,slnt,wdth,wght].ttf>" src/Lifestream.Desktop/Fonts

The variable font comes from
https://github.com/google/fonts/blob/fa826bb2a3e26df8c1bee99351fc0d039aeed7d2/ofl/googlesansflex/GoogleSansFlex%5BROND%2Copsz%2Cslnt%2Cwdth%2Cwght%5D.ttf
(font version 5.000, the same bytes as GoogleSansFlex-v5.000.zip of https://github.com/googlefonts/googlesans-flex/releases/tag/v5.000; SHA-256 below).
The output is the same bytes on every run; the hashes are recorded in src/Lifestream.Desktop/Fonts/README.md and checked by FontTests.
"""

import hashlib
import io
import sys
from pathlib import Path

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
import ttfautohint

SOURCE_SHA256 = "a9ac770798857d5e8ac593ced42b22e40a7c08cec98319f92b0eccc2783a77ba"
FAMILY = "Google Sans Flex"
FILE_STEM = "GoogleSansFlex"
VERSION = "5.000"
WEIGHTS = {400: "Regular", 500: "Medium", 600: "SemiBold", 700: "Bold"}

# Every axis but weight, fixed for the whole app (one optical size: headings and body are the same design, see the README).
AXES = {"opsz": 14, "wdth": 100, "ROND": 0, "slnt": 0}

# ttfautohint options: quantized stems for grayscale and DirectWrite, strong stems for GDI ClearType. Hinting is applied up to 50 px (larger text is
# smooth anyway), the x-height may round up at 14 px and below so small sizes stay open, and no "ttfautohint" info string goes into the version
# (the build is reproducible; see also the epoch in build()).
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
    legacy_family = FAMILY if ribbi else f"{FAMILY} {style}"
    legacy_style = style if ribbi else "Regular"
    for record in list(name.names):
        if record.nameID in (1, 2, 3, 4, 6, 16, 17, 21, 22, 25) or record.nameID >= 256:
            name.removeNames(nameID=record.nameID)
    axes = ",".join(f"{tag}={value}" for tag, value in AXES.items())
    name.setName(legacy_family, 1, 3, 1, 0x409)
    name.setName(legacy_style, 2, 3, 1, 0x409)
    name.setName(f"{VERSION};GOOG;{FILE_STEM}-{style};static-hinted;{axes}", 3, 3, 1, 0x409)
    name.setName(f"{FAMILY} {style}", 4, 3, 1, 0x409)
    name.setName(f"{FILE_STEM}-{style}", 6, 3, 1, 0x409)
    if not ribbi:
        name.setName(FAMILY, 16, 3, 1, 0x409)
        name.setName(style, 17, 3, 1, 0x409)
    os2 = font["OS/2"]
    os2.usWeightClass = weight
    # fsSelection: bit 0 italic, bit 5 bold, bit 6 regular; head.macStyle bit 0 bold.
    os2.fsSelection = (os2.fsSelection & ~0b1100001) | (0b100000 if style == "Bold" else 0) | (0b1000000 if style == "Regular" else 0)
    font["head"].macStyle = 1 if style == "Bold" else 0
    if "STAT" in font:
        del font["STAT"]


def instance(data: bytes, weight: int, style: str) -> bytes:
    """One static weight of the variable font, unhinted, as bytes. No timestamp of the build goes into the file."""
    variable = TTFont(io.BytesIO(data), recalcTimestamp=False)
    static = instancer.instantiateVariableFont(variable, {**AXES, "wght": weight}, updateFontNames=False)
    set_names(static, weight, style)
    buffer = io.BytesIO()
    static.save(buffer)
    return buffer.getvalue()


def build(source: Path, out_dir: Path) -> None:
    data = source.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != SOURCE_SHA256:
        sys.exit(f"Unexpected source font {source} (SHA-256 {digest}); update SOURCE_SHA256 on purpose if upstream changed.")
    out_dir.mkdir(parents=True, exist_ok=True)
    # ttfautohint stamps the time of the run into head.modified unless it is given one: the upstream font's own modification time (head times count
    # from 1904, the epoch option from 1970), so the same source gives the same bytes on every run.
    epoch = TTFont(io.BytesIO(data))["head"].modified - 2082844800
    for weight, style in WEIGHTS.items():
        built = ttfautohint.ttfautohint(in_buffer=instance(data, weight, style), epoch=epoch, **AUTOHINT)
        target = out_dir / f"{FILE_STEM}-{style}.ttf"
        target.write_bytes(built)
        check = TTFont(target)
        assert "fpgm" in check and "prep" in check and "fvar" not in check and "gvar" not in check and check["OS/2"].usWeightClass == weight, target
        print(f"{target.name}  {hashlib.sha256(built).hexdigest().upper()}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    build(Path(sys.argv[1]), Path(sys.argv[2]))
