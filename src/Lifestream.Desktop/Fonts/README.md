# Bundled font: Inter

The app uses one font family, **Inter**, for all text: **Inter** (400, 500, 600, 700) for text and **Inter Display** (600, 700) for the
large Display and Title steps. The files are the official *static, TrueType-hinted* builds from the Inter release, so stems snap to
whole pixels and 12 to 14 DIP text stays crisp with ClearType and Display formatting (the variable `InterVariable.ttf` used before 2.0.2
is unhinted, which WPF renders soft at small sizes). They ship inside `Lifestream.exe` as WPF resources, so nothing has to be installed
on the user's PC. Icons use the Windows icon font (`Segoe Fluent Icons`), which is not text.

| | |
|---|---|
| Version | Inter 4.1 (release `v4.1`, font revision 4.001) |
| Source | https://github.com/rsms/inter/releases/tag/v4.1 (`Inter-4.1.zip`, folder `extras/ttf/`) |
| SHA-256 of `Inter-4.1.zip` | `9883FDD4A49D4FB66BD8177BA6625EF9A64AA45899767DE3D36AA425756B11E` |
| Licence | SIL Open Font License 1.1, in `OFL.txt` next to the fonts (copied from `LICENSE.txt` in the release) |

| File | SHA-256 |
|---|---|
| `Inter-Regular.ttf` | `40D692FCE188E4471E2B3CBA937BE967878F631AD3EBBBDCD587687C7EBE0C82` |
| `Inter-Medium.ttf` | `97AD806F526E41546D46365BB3A393145F75B7B1568913DB74549AD8B8DBA872` |
| `Inter-SemiBold.ttf` | `78A843FADE9D4612A5567302FB595B56976EB5FCEBF4FEA5A5912D638BAFCDE3` |
| `Inter-Bold.ttf` | `288316099B1E0A47A4716D159098005EEF7C0066921F34E3200393DBDB01947F` |
| `InterDisplay-SemiBold.ttf` | `0310D7A325896129730C6C8CF9A6E0F81EE258BEDF77B1FF059B2A7B75F74E02` |
| `InterDisplay-Bold.ttf` | `B74C8E0DD744B3347FACA4C96BC7B2E32F7D6F62300A79B1D1A99331E44A5BC4` |

Why Inter: hinted static Inter, Manrope, Plus Jakarta Sans and Geist were compared at 12, 13, 14 and 16 px with ClearType on the app's
background. Inter's hand-tuned hinting kept even letter spacing and four distinct weights down to 12 px; the autohinted alternatives ran
letters together at 12 and 13 px or blurred the 400 and 500 weights.

The theme refers to the fonts in `Theme/Typography.xaml` (`Font.Family`, `Font.Display`); the charts load `Inter-Regular.ttf` into Skia
(`Theme/FontSetup.cs`) and the printed receipt uses `Font.Family`. `Lifestream.Desktop.Tests/FontTests.cs` checks the hashes above, the
hinting, the licence file and that every text style resolves to these files.
