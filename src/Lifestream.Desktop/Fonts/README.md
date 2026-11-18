# Bundled font: Inter

The app uses one font family, **Inter** (WPF sees the family as `Inter Variable Text`, its optical-size instance, with all nine weights), for all text. It ships inside `Lifestream.exe` as a WPF
resource, so nothing has to be installed on the user's PC. Icons use the Windows icon font (`Segoe Fluent Icons`), which is not text.

| | |
|---|---|
| Font | `InterVariable.ttf` (the official variable font, upright) |
| Version | Inter 4.1 (release `v4.1`, font revision 4.001) |
| Source | https://github.com/rsms/inter/releases/tag/v4.1 (`Inter-4.1.zip`, `InterVariable.ttf`) |
| SHA-256 of `InterVariable.ttf` | `4989B125924991B90D05B2D16E0E388C48F7D5BB8B30539BBF9C755278D0CCAF` |
| SHA-256 of `Inter-4.1.zip` | `9883FDD4A49D4FB66BD8177BA6625EF9A64AA45899767DE3D36AA425756B11E` |
| Licence | SIL Open Font License 1.1, in `OFL.txt` next to the font (copied from `LICENSE.txt` in the release) |

The theme refers to it in `Theme/Typography.xaml` (`Font.Family`). `Lifestream.Desktop.Tests/FontTests.cs` checks the hash above, the
licence file and that every text style resolves to this font.
