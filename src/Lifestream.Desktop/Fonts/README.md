# Bundled font: Google Sans Flex

The app uses one font family, **Google Sans Flex**, for all text, at four weights: Regular 400, Medium 500, SemiBold 600 and Bold 700 (direction A,
docs/design-system.md). They ship inside `Lifestream.exe` as WPF resources, so nothing has to be installed on the user's PC. Icons are vector
geometry (`Controls/Icon.cs`), not a font.

## Source and licence

| | |
|---|---|
| Font | `GoogleSansFlex[ROND,opsz,slnt,wdth,wght].ttf`, version 5.000, 3,910,784 bytes, SHA-256 `A9AC770798857D5E8AC593CED42B22E40A7C08CEC98319F92B0ECCC2783A77BA` |
| Source | https://github.com/google/fonts/tree/fa826bb2a3e26df8c1bee99351fc0d039aeed7d2/ofl/googlesansflex (Google Fonts) |
| Upstream | https://github.com/googlefonts/googlesans-flex, release `v5.000` (commit `cedf1e9cef08ddf33d711dd259601ea3017b7cd4`); the font in `GoogleSansFlex-v5.000.zip` is the same file, byte for byte |
| Licence | SIL Open Font License 1.1, in `OFL.txt` next to the fonts (copied from `ofl/googlesansflex/OFL.txt` at the commit above, SHA-256 `FC13D69F63E36D284B6E383D4D1463D8AD404F0AA065E57CF961252D51063137`). Copyright 2015 The Google Sans Flex Authors. |

The licence allows this use: "Original or Modified Versions of the Font Software may be bundled, redistributed and/or sold with any software, provided
that each copy contains the above copyright notice and this license" (condition 2). The files here are a Modified Version (static instances, hinted);
the licence names no Reserved Font Name, so they keep the family name. The licence travels with the app as `OFL.txt`.

"Google", "Google Sans" and "Google Sans Flex" are trademarks of Google LLC (`TRADEMARKS.md` beside the font in google/fonts). The app names the
typeface only to say which font it uses; it must never suggest that Google made or endorses Lifestream, and the name must not appear in the product's name.

## Why these files are built, not downloaded

Google publishes one variable font with five axes and **no hinting** (no `fpgm`, no glyph instructions). WPF cannot select an instance of a variable
font: it takes the default one. And in the whole-pixel text layout the app uses, WPF draws unhinted outlines ragged. So
`tools/fonts/build-google-sans-flex.py`:

1. reads the variable font above (it refuses any other file: the SHA-256 is pinned);
2. cuts static instances at wght 400, 500, 600 and 700 with fontTools' instancer (fontTools 4.68.0), with every other axis fixed (below), named so WPF
   groups them as one family "Google Sans Flex";
3. hints each one with ttfautohint (ttfautohint-py 0.6.1; the auto-hinter Google Fonts uses for its hinted families): quantized stems for grayscale
   and DirectWrite, strong stems for GDI ClearType, hinting from 8 to 50 px, x-height rounding limited at 14 px.

Rebuild (`-I` keeps the downloaded font away from Python's search path):
`python -m pip install fonttools==4.68.0 ttfautohint-py==0.6.1`, then
`python -I tools/fonts/build-google-sans-flex.py "<GoogleSansFlex[ROND,opsz,slnt,wdth,wght].ttf>" src/Lifestream.Desktop/Fonts`.
The output is the same bytes on every run (the modification time written into the files is the upstream font's, not the time of the build).

| File | SHA-256 |
|---|---|
| `GoogleSansFlex-Regular.ttf` | `F888CF0B68BE683523C2479F4D30C8239CEEBF385264CDA03FCB9831DE527926` |
| `GoogleSansFlex-Medium.ttf` | `74F63E93E88115243C8B84EE2CB6EBE3099578373B36EE36B133B6FA57A83565` |
| `GoogleSansFlex-SemiBold.ttf` | `1A3437DBCC9A2B9015F8469FF5AAC56DB584AF8CD79E10A985EEE37DCFC33B18` |
| `GoogleSansFlex-Bold.ttf` | `09B61042AA53A1F5EED025A43E13909D8A1C154A308F1E9BD666CAC689C1AE67` |

## The axes

| Axis | Range | Value | Why |
|---|---|---|---|
| Weight `wght` | 1 to 1000 | 400, 500, 600, 700 | the four weights the type ramp uses |
| Optical size `opsz` | 1 to 144 | 14 | Most text is 12 to 15 px, and at 14 the letters are open and evenly spaced at those sizes. **One** value for every size: a heading cut at 28 beside body text cut at 14 is a visibly tighter, finer design, and the app must read as one typeface. At 18 (the font's default) 12 px text runs together ("31Oct"); at 12, titles look loose. |
| Width `wdth` | 25 to 151 | 100 | the normal width; a condensed cut would save room in tables at the cost of legibility at 12 px |
| Roundness `ROND` | 0 to 100 | 0 | plain stroke ends: rounded ends soften small text and suit a playful product, not clinical records |
| Slant `slnt` | -10 to 0 | 0 | upright; the app sets no running text in italics (the receipt's one italic line is slanted by WPF) |

Version 5.000 has no grade axis (Google Fonts removed it).

What the app relies on, checked in the built files: tabular figures (`tnum`, for right-aligned numeric columns; figures are proportional otherwise),
a real minus sign U+2212 as wide as the plus (blood groups such as "O−"), and every character of the app's own text and of the demo data
(ö ü Ö á í ó ć ş, the en dash, curly quotes, the middle dot).

## Rendering

Every text of the app is drawn the same way, set once in `Theme/TextDefaults.cs`: **Display** formatting, **grayscale** anti-aliasing,
**kerning off**. It was chosen from captures of real windows at 100 % scale (96 DPI), where a 12 px letter is 12 device pixels:

- *Display, not Ideal.* Ideal formatting ignores hinting altogether (hinted and unhinted files give the same pixels) and draws the horizontal
  strokes of 12 to 14 px text across two rows of pixels: soft. Display formatting puts glyphs on whole pixels and uses the hinting: crisp.
- *Hinted, not as published.* In Display formatting the unhinted outlines come out ragged, with some letters heavier than their neighbours.
- *Kerning off.* Display formatting rounds each kerning pair to a whole pixel. With kerning on, "2.1.0" read "21.0" and "10:30" and "made-up"
  closed up at 12 and 13 px. Without it the spacing is even, at every size.
- *Grayscale, not ClearType.* WPF cannot draw ClearType on a transparent surface, so text on Mica (the title bar, the sidebar, the right side of
  the sign-in window) and in the dialogs is grayscale whatever is asked for. Only grayscale is the same on every surface.
- *One mode for every size.* 2.1.0 set its headings in Ideal formatting over Display text, and part of each window in grayscale beside ClearType;
  with a font whose hinting reshaped the small sizes, a window read as two typefaces.

The comparison is made with `tests/Lifestream.Desktop.Tests/EndToEnd/RealWindowTextTests.cs`, which shows the sign-in window, a list screen and a
size ladder in real windows outside every monitor and captures what the compositor draws (set `LIFESTREAM_REALWINDOW_DIR`; never part of a normal
test run). `tools/fonts/render-ladder.cs` draws a size ladder off-screen with WPF alone, which is always grayscale.

The theme refers to the font in `Theme/Typography.xaml` (`Font.Family`); the charts load `GoogleSansFlex-Regular.ttf` into Skia
(`Theme/FontSetup.cs`) and the printed receipt uses `Font.Family`. `Lifestream.Desktop.Tests/FontTests.cs` checks the hashes above, the hinting, the
licence file and that every text style resolves to these files; `RedesignA/TextChecks.cs` checks on every rendered screen that every piece of text is
drawn from these files, in one family, with one set of rendering settings.
