# Bundled font: Onest

The app uses one font family, **Onest**, for all text, at four weights: Regular 400, Medium 500, SemiBold 600 and Bold 700 (direction A,
docs/design-system.md). They ship inside `Lifestream.exe` as WPF resources, so nothing has to be installed on the user's PC. Icons are vector
geometry (`Controls/Icon.cs`), not a font.

## Why these files are built, not downloaded

The Onest files Google Fonts serves are **unhinted** (no `fpgm`, no glyph instructions), and the upstream release is a variable font. WPF
cannot pick weights from a variable font's axis, and draws unhinted outlines soft at 12 to 14 px. So `tools/fonts/build-onest.py`:

1. reads the official variable font `Onest[wght].ttf` from google/fonts at commit `4c1db3aec83c67dd223dd82a68c039cab30917a9`
   (`ofl/onest/Onest%5Bwght%5D.ttf`, font version 2.001, SHA-256 `966C5C29B4755DA84B6854D5C21DD4EAA2420225D0E9874DE602DE176D4A9F31`; the script refuses any other file);
2. cuts static instances at wght 400, 500, 600 and 700 with fontTools' instancer, named so WPF groups them as one family "Onest";
3. hints each one with ttfautohint (ttfautohint-py 0.6.1; the same auto-hinter Google Fonts uses for its hinted families): quantized stems for
   grayscale and DirectWrite/WPF ClearType, strong stems for GDI ClearType, hinting from 8 to 50 px, x-height rounding limited at 14 px.

Rebuild: `python -m pip install --user fonttools ttfautohint-py` then
`python tools/fonts/build-onest.py <Onest[wght].ttf> src/Lifestream.Desktop/Fonts`. The output is byte-for-byte reproducible.

| | |
|---|---|
| Source | https://github.com/google/fonts/tree/4c1db3aec83c67dd223dd82a68c039cab30917a9/ofl/onest |
| Licence | SIL Open Font License 1.1, in `OFL.txt` next to the fonts (copied from `ofl/onest/OFL.txt`, SHA-256 `071195D8806E226FAEEE60259C28CA67B458227AF5195A73F5CFCAB06E3003BC`). Copyright 2021 The Onest Project Authors. The hinted instances are a Modified Version under the OFL; the Reserved Font Name clause does not apply (Onest declares none). |

| File | SHA-256 |
|---|---|
| `Onest-Regular.ttf` | `5EBE461C3559D999551AB5F3D305880FB52DB6846EE2098A09F949D5FD20AC99` |
| `Onest-Medium.ttf` | `50914F2741FD8EE26CE890BA63D3B251B68D79463ECB93260E937F50DD7453F0` |
| `Onest-SemiBold.ttf` | `BD3BEF0BAD11EC6018233FF8AFCA91B1701D14D11ED0F59700F46391A0BAFC91` |
| `Onest-Bold.ttf` | `E7B6AB8D8624E3B1D33324BEBC5705718E6F5A0D3DD60FCE0A70929542BF4F13` |

## Crispness check

`tools/fonts/render-ladder.cs` (`dotnet run tools/fonts/render-ladder.cs -- out.png "label=path\x.ttf#Family" ...`) draws a size ladder (12, 13, 14,
15, 16, 20, 28 px at 400/500/600) with WPF's own text stack, off-screen, in Display and Ideal formatting on the light and dark layers. Compared
with the unhinted instances and with the hinted Inter of 2.0.2: the hinted Onest keeps stems on whole pixels and even letter spacing down to
12 px with Display formatting, where the unhinted files run letters together ("Greenleaf") and blur 400 against 500. Inter is still a touch
tighter at 12 px, but the difference is small enough that Onest is used for every size. Ideal formatting softens 12 and 13 px text, so the
window uses Display formatting and only the 20 px and larger styles switch to Ideal (Theme/Typography.xaml).

The theme refers to the font in `Theme/Typography.xaml` (`Font.Family`); the charts load `Onest-Regular.ttf` into Skia (`Theme/FontSetup.cs`)
and the printed receipt uses `Font.Family`. `Lifestream.Desktop.Tests/FontTests.cs` checks the hashes above, the hinting, the licence file and
that every text style resolves to these files.
