# Lifestream design system, direction A "Ward"

Lifestream's desktop UI is calm, clinical and native to Windows 11. This document is the contract for building and rebuilding screens. Every key,
style name, property and number below was read from the code in `src/Lifestream.Desktop` and `src/Lifestream.Client`. Where the code does not do
something yet, the text says "not built". Where the code and the original design recommendation disagree, the code is documented.

Contents

1. Principles
2. Tokens
3. Theme
4. Fonts and icons
5. Components
6. Window and shell
7. How to build a screen
8. Testing

Code comments refer to requirement IDs of the form `SHELL-nn` (for example SHELL-07 waking server, SHELL-24 unsaved changes and scroll memory,
SHELL-28 load surface, SHELL-32 forms, SHELL-34 focus ring, SHELL-36 date input, SHELL-38 and SHELL-39 data grid, SHELL-40 status). Keep those
references when you touch such code.

## 1. Principles

- **Calm, clinical, native.** The app sits next to Outlook, Teams and the hospital LIS. It uses Mica where Windows 11 provides it, Windows 11 corner
  radii, and flat surfaces. There is no decoration that does not carry information.
- **One ink primary per view.** The single filled button of a view is `Button.Primary` (dark ink on light, light ink on dark). Everything else is
  `Button.Secondary`, `Button.Subtle` or `Button.Danger`. A screen with two filled buttons is wrong.
- **Red means blood.** `Blood` and `Critical` (the same colour) appear only on blood group tags, the logo, donor avatars, low-stock and error states,
  and the danger button. Do not use red for emphasis, selection or decoration. (One known exception in the code: the selected sidebar item's bar is
  `Brush.Blood`; see 6.2. The selected tab's bar is `SelectedBar`.)
- **Flat panels with a 1 px stroke.** A resting panel is `Layer` with a 1 px `Stroke` and `Radius.Panel` (8). No card shadows.
- **Shadows live on their own layer.** A floating surface (menu, toast, dialog, palette, side panel) draws its shadow with a separate `Border`
  behind the content. Never put an `Effect` on an element that contains text; it renders the text through a bitmap and loses ClearType. A test
  enforces this.
- **Crisp pixels.** `UseLayoutRounding` and `SnapsToDevicePixels` are on for every window. Borders are `controls:CrispBorder`, which snaps its edge to
  whole device pixels at 100 to 175 % scaling. Radii are whole numbers. Every text, at every size, uses Display text formatting, grayscale
  anti-aliasing and the hinted Google Sans Flex files (section 4.1); no style or view sets its own text rendering.
- **Status is icon plus word, never colour alone.** A status badge always has an icon or a dot and a text label. High contrast and colour vision
  deficiency both depend on this.
- **The keyboard reaches everything.** Every action has a visible focus ring, a sensible tab order and, for primary actions, a shortcut shown in the
  tooltip and the command palette.

## 2. Tokens

Tokens live in four places. Views and controls refer to them by key; they never contain a literal that a token already covers.

| File | Holds |
|---|---|
| `Theme/Colors.Light.xaml`, `Theme/Colors.Dark.xaml` | Every colour and brush, with identical keys in both files. The only files where a colour literal may appear. |
| `Theme/Tokens.xaml` | Spacing, sizes, radii, elevation, motion numbers, breakpoints. |
| `Theme/Typography.xaml` | Font family, font sizes, line heights and the `Text.*` styles. |
| `Theme/Motion.xaml` | The motion numbers as `Duration` resources (see Motion). |

### 2.1 Colour

Every row is a pair `Color.<name>` and `Brush.<name>` unless the "Brush" column says otherwise. Use brushes in views. Contrast targets: text at least
4.5:1 on `Layer` and `Canvas`, meaningful borders and icons at least 3:1, in both themes.

| Key | Light | Dark | Use |
|---|---|---|---|
| `Canvas` | #F1F3F6 | #111418 | Page background (under Mica, and the solid fallback on Windows 10) |
| `Layer` | #FFFFFF | #191D23 | Panels, tables, fields, side panel, menus, dialogs |
| `Layer2` | #F7F8FA | #1F242B | Table header, hover fill of a field, disabled fill, meter track |
| `Stroke` | #E1E5EB | #2A3038 | Panel edges, row separators, quiet dividers |
| `Stroke2` | #C8CFD9 | #3B434E | Control borders (buttons, fields, checkbox ring), the dashed empty state |
| `StrokeStrong` | #767D86 | #8B9199 | The bottom edge of a text field, the off toggle border |
| `Ink` | #121820 | #EDF0F4 | Primary text |
| `Ink2` | #47515F | #AEB7C3 | Secondary text, labels, icons in lists |
| `Ink3` | #616D7C | #8A95A3 | Muted text: hints, counts, placeholders, disabled text |
| `Hover` | #F2F4F7 | #1E232A | Row, menu item and button hover |
| `Selected` | #EAF0F7 | #202832 | Selected row, pressed fill, selected menu item |
| `SelectedBar` | #1B2533 | #E8ECF1 | The 3 px bar inside the left edge of a selected row |
| `Primary` | #1B2533 | #E8ECF1 | The one filled button, selection text background, checked controls |
| `PrimaryHover` | #2B3749 | #FFFFFF | Primary hover |
| `PrimaryPressed` | #0D131B | #C7CED8 | Primary pressed |
| `OnPrimary` | #FFFFFF | #121820 | Text and glyphs on `Primary` |
| `OnPrimaryPressed` | #CCFFFFFF | #CC121820 | Text on a pressed primary button (80 %) |
| `Focus` | #1B2533 | #E8ECF1 | The 2 px keyboard focus ring |
| `Blood` | #B4162E | #F2596E | Blood group tags, logo, donor avatar text, tab bar |
| `BloodSoft` | #FBE8EB | #3A1A21 | Blood group tag fill, donor avatar fill |
| `Critical` | #B4162E | #F2596E | Errors, low stock, danger button text, bell dot |
| `CriticalSoft` | #FBE8EB | #3A1A21 | Critical badge fill, danger hover |
| `CriticalStroke` | #BF7C8C | #8D4D5C | Danger button edge |
| `CriticalPressed` | #F2D5D9 | #33131A | Danger button pressed fill |
| `Success` | #0F6E45 | #52C58E | Done, eligible, completed |
| `SuccessSoft` | #E2F3EA | #14302A | Success badge fill |
| `SuccessStroke` | #A2C1B9 | #264A3E | Success edge |
| `Warning` | #8F5200 | #EBAA4C | Expiring, incomplete, Caps Lock warning |
| `WarningSoft` | #FCEFD9 | #33281A | Warning badge fill |
| `Info` | #1D5BB8 | #82B2F5 | Information, pending, waking, update ready |
| `InfoSoft` | #E5EEFB | #19263A | Info badge fill, info banners |
| `OnBlood` | #FFFFFF | #FFFFFF | The wave drawn on the logo drop |
| `CloseHover` | #C42B1C | #C42B1C | The close caption button, the taskbar badge disc |
| `OnCloseHover` | #FFFFFF | #FFFFFF | Glyph on the close button, number on the taskbar badge |
| `CaptionHover` | #17121820 | #17EDF0F4 | Ink at 9 %: caption button hover, quiet tints over Mica |
| `CaptionPressed` | #0D121820 | #0DEDF0F4 | Ink at 5 %: caption button pressed |
| `Well` | #0F121820 | #0FEDF0F4 | Ink at 6 %: segmented control well, progress track, scrollbar track |
| `LayerOnMica` | #BFFFFFFF | #BF191D23 | Layer at 75 %: the title-bar search field |
| `NavActive` | #CCFFFFFF | #CC191D23 | Layer at 80 %: the active sidebar item |
| `Skeleton1` | #ECEFF3 | #20252C | Skeleton bar |
| `Skeleton2` | #F7F8FA | #2A3039 | Skeleton shimmer highlight |
| `Scrim` | #66121820 | #99000000 | Behind dialogs |
| `Chart1` | #B4162E | #F2596E | Chart series 1 (donated). Also `Brush.Chart1`. |
| `Chart2` | #1D5BB8 | #82B2F5 | Chart series 2 (issued). Also `Brush.Chart2`. |

Colours that exist only as `Color.*` (read them in code with `ThemeService.ColorOf`, there is no brush):

| Key | Light | Dark | Use |
|---|---|---|---|
| `Color.Shadow` | #0F1720 | #000000 | Colour of every shadow |
| `Color.ChartGrid` | #E1E5EB | #2A3038 | Chart grid lines |
| `Color.ChartText` | #616D7C | #8A95A3 | Chart axis text |
| `Color.MicaStart`, `MicaMid`, `MicaEnd` | #EEF1F6, #F3F1F3, #EFF2F5 | #13171C, #17141A, #12161B | Stops of the Mica fallback gradient |

Brushes that are not plain colours: `Brush.Mica` (a gradient of the three Mica stops; transparent in a window where real Mica is active),
`Brush.WarningHatch` (a 6 x 6 diagonal stripe tile in `Warning`, for the expiring share of a stock meter) and `Image.DefaultAvatar` (a drawing for a
person with no photo).

**Section 2 of the colour files is not for new code.** Below the tokens, both colour files map the 2.0 names (`Brush.SurfaceCanvas`,
`SurfaceRaised`, `SurfaceHover`, `SurfacePressed`, `SurfaceSelected`, `SurfaceInputHover`, `SurfaceInputFocus`, `SurfaceAccent*`, `SurfaceInfo`,
`SurfaceDanger`, `BorderCard`, `BorderSubtle`, `BorderInput`, `TextPrimary`, `TextSecondary`, `TextMuted`, `TextDisabled`, `TextOnPrimary`,
`FocusRing`, `DangerHover`, `DangerPressed`, `StatusDanger`, `StatusSuccess`, `StatusWarning`, `StatusInfo`, `SkeletonBase` and the matching
`Color.*`) onto the tokens, so screens not yet rebuilt follow the theme. Do not use them in a screen you write or rebuild. Replace them when you
touch a file. Mapping: `SurfaceRaised` is `Layer`; `SurfaceHover` is `Hover`; `SurfacePressed` and `SurfaceSelected` are `Selected`;
`TextPrimary`, `TextSecondary` and `TextMuted` are `Ink`, `Ink2` and `Ink3`; `BorderCard` and `BorderSubtle` are `Stroke`; `BorderInput` is
`Stroke2`; `StatusDanger/Success/Warning/Info` are `Critical/Success/Warning/Info`.

### 2.2 Type ramp

Family: Google Sans Flex (`Font.Family`). Styles are in `Theme/Typography.xaml`; every style sets `Foreground` to a DynamicResource brush and turns
tabular figures off unless stated. A style sets size, line height, weight and colour, never how text is drawn: that is the same for every style
(section 4.1).

| Style key | Size / line height | Weight | Colour | Use |
|---|---|---|---|---|
| `Text.Hero` | 34 / 42 | SemiBold | Ink | The sign-in compatibility heading only |
| `Text.Display` | 32 / 40 | SemiBold | Ink | The sign-in heading |
| `Text.Title` | 28 / 36 | SemiBold | Ink | Page title (inside `PageHeader`) |
| `Text.Section` | 20 / 28 | SemiBold | Ink | Dialog title, side panel title |
| `Text.Heading` | 15 / 22 | SemiBold | Ink | Panel heading (inside `Section`) |
| `Text.Body` | 14 / 20 | Regular | Ink | Body text, table cells, field text |
| `Text.BodyStrong` | 14 / 20 | SemiBold | Ink | A name, a row title, a toast message |
| `Text.Secondary` | 13 / 18 | Regular | Ink2 | A second line, descriptions |
| `Text.Label` | 13 / 18 | Medium | Ink2 | Field labels, column headers |
| `Text.Caption` | 12 / 16 | Regular | Ink3 | Hints, counts, group headers, axis labels. 12 is the minimum size. |
| `Text.Numeric` | 14 / 20 | Regular | Ink | A number in a right-aligned column: tabular figures, no wrap |
| `Text.Icon` | 16 | n/a | n/a | A glyph from the system icon font. Only for the legacy `IconText`; new code uses `controls:Icon`. |
| `Kbd.Text` | 11 / 16 | Medium | Ink2 | The text of a key cap (the only text below 12) |

Plain `TextBlock` defaults come from the window (`Font.Family`, 14, `Ink`), but always pick a `Text.*` style so wrapping, line height and colour are
set. Every `Text.*` style based on `Text.Base` wraps by default; add `TextWrapping="NoWrap" TextTrimming="CharacterEllipsis"` for one-line cells.

Rules: proportional figures in prose and in labels such as "17 units"; tabular figures only where numbers of several digits are stacked in a
right-aligned column (`Text.Numeric` or `Typography.NumeralAlignment="Tabular"`). A tabular 1 is as wide as an 8, so a tabular "17 units"
reads as "1 7 units": do not use tabular figures for a number that stands in a label. Blood groups carry a real minus sign (U+2212): use `BloodType.ToTypographic()` for the screen and keep
`ToDisplay()` (ASCII "O-") for search, files and the API. Nothing is smaller than 12 DIP except key caps.

### 2.3 Spacing

The scale is 4, 8, 12, 16, 20, 24, 32, 48 DIP. Page padding is 24 to 32. The gap between panels is 20.

| Key | Type | Value | Use |
|---|---|---|---|
| `Space.4` ... `Space.48` | double | 4, 8, 12, 16, 20, 24, 32, 48 | A single number: `Width`, `Height`, `Edge.*` helpers |
| `Pad.4` ... `Pad.48` | Thickness | the same, all sides | Uniform padding or margin |
| `Pad.PageA` | Thickness | 32,24,32,24 | Page padding (Standard and Wide) |
| `Pad.PageCompact` | Thickness | 22,18,16,16 | Page padding in a Compact window |
| `Pad.PanelHeader` | Thickness | 18,14,18,10 | Header row of a `Section` |
| `Pad.PanelBody` | Thickness | 18,0,18,14 | Default body padding of a `Section` |
| `Pad.Button` / `Pad.ButtonSmall` | Thickness | 14,0 / 10,0 | Button padding (34 and 28 high) |
| `Pad.Field` | Thickness | 10,0,10,0 | Text field and menu item padding |
| `Pad.Badge` | Thickness | 8,0,8,0 | Badge padding |
| `Gap.Panels` | Thickness | 0,20,0,0 | Space above a panel that follows another |
| `Gap.Field` | Thickness | 0,0,0,16 | Space below a form field (`FormField` sets it) |
| `Inset.*` | Thickness | see name | One-off margins and paddings; the name lists the sides |

`Inset` names: `H` is left and right, `V` is top and bottom, `L`, `T`, `R`, `B` are one side each, and the number follows its letter. `Inset.T16` is 16
above; `Inset.H12V8` is 12 across and 8 down; `Inset.B16` is 16 below. `Inset.Neg3` and `Inset.Neg4` (-3, -4) are for focus rings that sit outside a
control. Prefer an existing `Inset.*` over a new literal. `Pad.Page` (24) and the `Print.*` and `Chart.BarGap` tokens are legacy or specialised.

Note: the finished direction A screens (`ShellView`, `DashboardView`, `LoginView`) still contain literal margins in places. Do not copy those; use
the scale and tokens above.

### 2.4 Sizes

All in DIP.

| Key | Value | Use |
|---|---|---|
| `Height.Control` | 34 | Button, text field, combo box, search field, menu item |
| `Height.Compact` | 28 | Small button, small icon button, check box, radio, toggle switch |
| `Height.ControlLarge` | 40 | Sign-in's two buttons only |
| `Height.Row` | 44 | Table row (Comfortable) |
| `Height.RowCompact` | 34 | Table row (Compact density). Defined; not applied by any style yet. |
| `Height.HeaderRow` | 38 | Table header |
| `Height.NavItem` | 36 | Sidebar item |
| `Height.Badge` | 22 | Badge, blood group tag |
| `Height.TitleBar` / `Height.TitleBarCompact` | 44 / 40 | Title bar (40 in a Compact window) |
| `Height.CaptionButton` / `Width.CaptionButton` | 44 / 46 | Caption buttons |
| `Width.Sidebar` / `Width.Rail` | 236 / 52 | Sidebar expanded / icon rail |
| `Width.Panel` / `Width.PanelCompact` | 420 / 352 | Side panel |
| `Width.Palette` | 640 | Command palette |
| `Width.TitleSearch` | 440 | Title-bar search button |
| `Width.Dialog` | 480 | Default dialog width |
| `MaxWidth.Dashboard` / `MaxWidth.Form` / `MaxWidth.Settings` | 1600 / 720 / 960 | Content max widths |
| `Size.Icon` / `Size.IconSmall` | 18 / 16 | Icon sizes in lists and buttons |
| `Size.Avatar` | 32 | Default avatar diameter |
| `Size.FocusRing`, `Thickness.FocusRing` | 2 | Focus ring thickness |
| `Size.ScrollBar`, `Size.ScrollThumbRest`, `Size.ScrollThumbHover` | 12, 2, 6 | Overlay scrollbar |
| `Chrome.ResizeBorder` | 6 | Window resize border |


### 2.5 Radii

All whole numbers so edges stay crisp at every scale.

| Key | Value | Use |
|---|---|---|
| `Radius.Small` | 4 | Badges, tags, check boxes, key caps, menu items, data grid cell focus |
| `Radius.Control` | 6 | Buttons, fields, segments, tooltips |
| `Radius.Panel` (`Radius.Card`) | 8 | Panels, menus, popups, toasts, the palette, banners, segmented well |
| `Radius.Dialog` | 12 | Dialog surface |
| `Radius.FocusRing` | 10 | Focus ring around a 6 radius control (control radius + 4) |
| `Radius.FocusRingSmall` | 8 | Focus ring around a 4 radius control (the check box) |
| `Radius.ContentCorner` | 8,0,0,0 | The page area's top-left corner against the sidebar and title bar |
| `Radius.Underline` | 0,0,6,6 | The focus bar under a field |
| `Radius.Pill` (4), `Radius.Round` (999), `Radius.Avatar` (36), `Radius.AvatarLarge` (48), `Radius.2`, `Radius.6`, `Radius.14`, `Radius.Left8` | | Legacy or specialised |

### 2.6 Elevation

Resting panels are flat: 1 px `Stroke`, no shadow. Floating surfaces use `controls:ElevatedSurface` or their own template, which draws the shadow on a
separate `Border` behind the content.

| Level (`ElevationLevel`) | Blur / offset / opacity | Tokens | Used by |
|---|---|---|---|
| `Flyout` | 16 / 8 / 14 % | `Elevation.Flyout.*` | Menus, combo box popups, tooltips, toasts |
| `Dialog` | 64 / 32 / 22 % | `Elevation.Dialog.*` | Dialogs, the command palette (over a `Scrim`) |
| `Panel` | 24 / 0 / 10 %, cast to the left | `Elevation.Panel.*` | The side panel |

`Card` (`Controls/Card.cs`) is the 2.0 stacked-rectangle shadow card. It is still compiled and used by the 2.0 list detail panes. Do not use it in
new code.

### 2.7 Motion

The numbers are in `Tokens.xaml` as `Motion.*.Milliseconds`. Storyboard durations cannot be dynamic resources, so `Theme/Motion.xaml` turns them into
`Duration` resources (`Motion.Fast` and so on) through `MotionSettings`. `MotionSettings` holds no number; it reads the tokens and applies the
Windows "Animation effects" setting.

| `Duration` resource | ms | Use |
|---|---|---|
| `Motion.Fast` | 100 | Hover and press colour changes, toggle knob, focus bar fade. Focus ring itself is instant. |
| `Motion.Page` | 120 | Scrollbar widen and track fade. (Meant for the page cross-fade too; the shell does not cross-fade pages yet.) |
| `Motion.Standard` | 180 | Dialog and palette: fade plus scale from 0.98 (`Motion.DialogScaleFrom`) |
| `Motion.PanelOpen` | 200 | Side panel in: slide 24 px (`Motion.SlideDistance`) plus fade, decelerate |
| `Motion.PanelClose` | 150 | Side panel out, accelerate |
| `Motion.Shimmer` | 1400 | Skeleton shimmer, indeterminate progress bar |
| `Motion.SpinnerTurn` | 900 | Busy spinner, one turn |
| `Motion.ScrollIdle` (ms only) | 1200 | Time before an idle scrollbar fades |

Easing: `Ease.Decelerate` (CubicEase EaseOut) when something opens, `Ease.Accelerate` (EaseIn) when it closes. `Motion.ToastRise` (12) is defined;
toasts do not animate in yet.

When Windows "Animation effects" is off: every `Duration` resource is zero; the spinner runs once and stands still (`SpinnerRepeat` is 1); the
skeleton is static; `SidePanel` and `ModalOverlay` appear and disappear without animation; overlay scrollbars neither fade nor widen and are shown at
rest width while content scrolls. `Motion.ScrollIdle` is not an animation and does not drop to zero. The setting is read when the theme loads, so
changing it while the app runs needs a restart. No staggered entrances, no load choreography.

### 2.8 Breakpoints

Set by `Responsive.Source="True"` on the shell root from the main window's width (DIP). `Breakpoint.Standard` token 1280, `Breakpoint.Wide` token
1600; the C# constants are `Responsive.StandardWidth` and `Responsive.WideWidth`. The minimum window is 1024 x 640.

| Breakpoint | Window width | Shell | Screens |
|---|---|---|---|
| `Compact` | below 1280 | Sidebar is the 52 rail, title bar 40, search is a "Search Ctrl K" button, bell and avatar are hidden, page padding `Pad.PageCompact` | Side panel 352, the dashboard's panels stack at their own heights and only the page scrolls, filter chips fold into a Filters button, low-priority columns hide (planned) |
| `Standard` | 1280 to 1599 | Full sidebar (unless collapsed with Ctrl+B) | Full layout |
| `Wide` | 1600 and up | Same as Standard | Wide tables may show extra columns; reading layouts keep their max width |

## 3. Theme

### 3.1 How the theme is built and swapped

- `ThemeLoader.Resources` loads `Theme/Theme.xaml` once per UI thread (shared tokens, type, motion, scrollbars, every control dictionary, chrome and
  shell styles) and inserts exactly one colour dictionary, `Colors.Light.xaml` or `Colors.Dark.xaml`, at index 0 of its merged dictionaries. Every
  `ChromeWindow` merges this dictionary, and so does the application.
- `ThemeService.Apply(ThemeMode)` resolves the mode (`Light`, `Dark`, or `System`, which reads
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`), builds the new colour dictionary and replaces the old
  one (`ThemeLoader.ReplaceColors`). Because every view refers to brushes with `DynamicResource`, every open window repaints. Then it raises
  `ThemeService.Changed` on the UI thread. `App.cs` calls `Apply(preferences.Theme)` at start.
- With mode `System`, the service listens to `SystemEvents.UserPreferenceChanged` (General, Color, VisualStyle, Accessibility) and re-applies.
- `ThemeService.IsDark`, `Mode` and `SystemIsDark` are readable; tests can set `SystemIsDarkOverride`. State is thread-static: the app has one UI
  thread; tests render on several.
- Fonts and the chart font are set up once at load (`FontSetup.ApplyAppFont`, `ApplyChartFont`); they do not change with the theme.

### 3.2 Rules this implies

1. **`DynamicResource` for every brush and colour, in every view and control.** A `StaticResource` would freeze the colour of the theme that was
   active when the view loaded, and the view would not repaint on a switch.
2. **`DynamicResource` for tokens inside views too** (`Space.*`, `Inset.*`, `Radius.*`, `Height.*`, `Text.*` styles, `Font.Family`). A
   `StaticResource` in a view fails with a resource-not-found error when the view is parsed before it is in a window, because the theme is merged at
   the window, not in the view. Control dictionaries under `Controls/` merge `Tokens.xaml`, `Motion.xaml` and `Typography.xaml` themselves, so
   `StaticResource` is fine for tokens there. `Motion.*` `Duration` resources are the one case storyboards need as `StaticResource`; keep them in
   control templates.
3. **Never a colour literal outside `Colors.Light.xaml` and `Colors.Dark.xaml`.** No `#RRGGBB`, no named colour such as `Black` or `White`, no
   `Color.FromRgb` in code. A test scans every view XAML and every `*.cs` for hex literals and `Color.FromRgb` and `Color.FromArgb`. If you need a
   colour that does not exist, add it to both colour files with the same key.
4. **Add every new key to both colour files.** A test checks both define exactly the same keys and that every brush resolves in both themes.
5. **Code that paints itself reads colours at draw time.** Skia charts (LiveCharts on the dashboard) call `ThemeService.ColorOf("Color.Chart1")`,
   `Color.Chart2`, `Color.ChartGrid`, `Color.ChartText` and `Color.Layer` when they build their paints, subscribe to `ThemeService.Changed` in
   `Loaded`, redraw in the handler and unsubscribe in `Unloaded`. `ColorOf` takes the `Color.*` key (not `Brush.*`) and returns transparent when the
   key is missing. Do the same for anything that does not use brushes (taskbar overlay, DWM frame).
6. **No colour carries meaning alone** (see Principles), so the themes and high contrast cannot break a screen.

### 3.3 High contrast

When `SystemParameters.HighContrast` is on, `ThemeService.CreateColors` rewrites every `Color.*` and `Brush.*` key of the colour dictionary to a
Windows system colour, whatever the mode; it re-applies when the user changes the contrast theme.

| Keys (name after the dot) | Becomes |
|---|---|
| `Primary`, `PrimaryHover`, `PrimaryPressed`, `SelectedBar`, `Focus`, `Blood`, `Critical`, `Success`, `Warning`, `Info`, `Chart1`, `Chart2` | `Highlight` |
| `OnPrimary`, `OnPrimaryPressed`, `OnCloseHover` | `HighlightText` |
| `Ink`, `TextPrimary` | `WindowText` |
| `Ink2`, `Ink3`, `StrokeStrong`, `Stroke2`, `ChartText` (and the 2.0 text names) | `GrayText` |
| `Stroke`, `BorderSubtle`, `ChartGrid` | `WindowText` |
| `Hover`, `Selected`, `SurfaceHover`, `SurfacePressed`, `SurfaceSelected` | `Control` |
| Everything else (`Canvas`, `Layer`, `Layer2`, every `*Soft`, `Well`, `Scrim`, ...) | `Window` |

Consequences: the soft badge fills disappear and the status colours collapse to `Highlight`, so the icon and word must carry the status. Brushes that
are not solid colours (`Brush.WarningHatch`, `Brush.Mica`) also become a flat `Window` brush, so the hatched "expiring" share of a stock meter is not
visible in high contrast; its count text must say it. Test every new screen once with a Windows contrast theme.

### 3.4 Preferences

`UiPreferences` (`Lifestream.Client/Common/UiPreferences.cs`) holds the person's display choices on this PC. Every change is saved at once through
`IPreferencesStore`; the app uses `FilePreferencesStore`, which writes `%LOCALAPPDATA%\Lifestream\preferences.json` (indented JSON, enums as
strings). A missing, unreadable or corrupt file gives the defaults; a failed save is ignored. Nothing secret is stored.

| Property | Values | Default | Notes |
|---|---|---|---|
| `Theme` | `System`, `Light`, `Dark` | `System` | Account menu: Theme, Light, Dark, Match Windows (`ShellViewModel.SetThemeCommand`) |
| `Density` | `Comfortable`, `Compact` | `Comfortable` | Saved and loaded, but nothing reads it yet: row height is fixed at `Height.Row` (44). There is no settings page yet. |
| `IsNavCollapsed` | bool | false | Ctrl+B. A Compact window shows the rail whatever this says. |
| `RememberUsername`, `Username` | bool, string | false, null | Sign-in only; `Username` is stored only while `RememberUsername` is on |

## 4. Fonts and icons

### 4.1 Google Sans Flex

One family, Google Sans Flex (SIL Open Font License), bundled in `Lifestream.exe` as four static TrueType files in `src/Lifestream.Desktop/Fonts`:
Regular 400, Medium 500, SemiBold 600 and Bold 700. `Font.Family` is `pack://application:,,,/Lifestream;component/Fonts/#Google Sans Flex`.
`Font.Display` is the same family and the same files, kept for 2.0 code. `Font.Icons` ("Segoe Fluent Icons, Segoe MDL2 Assets") is only for the legacy
`IconText` glyphs and the caption buttons. "Google Sans Flex" is a trademark of Google LLC: name it only to say which font the app uses.

Why the files are built rather than downloaded: Google publishes one variable font with five axes and no hinting. WPF cannot select an instance of a
variable font, and draws unhinted outlines ragged in the whole-pixel layout the app uses. `tools/fonts/build-google-sans-flex.py` cuts static
instances at 400, 500, 600 and 700 from the official variable font (version 5.000, pinned to a google/fonts commit and SHA-256) and hints each with
ttfautohint (8 to 50 px). The other axes are fixed for the whole app: optical size 14 (one cut for headings and body text, so they are the same
design), width 100, roundness 0, slant 0. The output is the same bytes on every run.

Rebuild: `python -m pip install fonttools==4.64.0 ttfautohint-py==0.6.1`, then
`python -I tools/fonts/build-google-sans-flex.py "<GoogleSansFlex[ROND,opsz,slnt,wdth,wght].ttf>" src/Lifestream.Desktop/Fonts`. `Fonts/README.md`
lists source, versions, hashes and the reasons for each axis value; the licence is `Fonts/OFL.txt`. Charts load `GoogleSansFlex-Regular.ttf` into Skia
(`FontSetup.ApplyChartFont`) so chart text matches. If a font cannot load, WPF text falls back to the system UI font and charts to Skia's default,
with a logged warning, rather than ending the process.

**One way of drawing text.** `Theme/TextDefaults.cs` holds the text rendering of the whole app: Display formatting (glyphs on whole pixels, hinting
used: crisp at 12 to 15 DIP), grayscale anti-aliasing (the only kind WPF can draw on Mica and in the transparent dialog windows, so the only kind
that is the same everywhere) and kerning off (Display formatting rounds a kerning pair to a whole pixel, which closed up "2.1.0" and "10:30"). The
values are inherited, so they are set once per visual tree: `theme:TextDefaults.Apply="True"` on the root element of every window, and in the
styles of tool tips and context menus, which open in a tree of their own. Never set `TextOptions.TextFormattingMode`,
`TextOptions.TextRenderingMode` or `Typography.Kerning` on a style or in a view: a heading in another mode than the text under it reads as
another typeface (2.1.0 had Ideal headings over Display text, and grayscale beside ClearType). `RedesignA/TextChecks.cs` fails any render in which
a piece of text comes from another font file or is drawn with other settings.

### 4.2 Icon control

`controls:Icon` draws one outline icon of the direction A set: 24 x 24 geometry from `Controls/IconData.cs` (generated from
`tools/icons/icons-a.svg`; do not edit by hand), stroked 1.5 with round caps in `Foreground`, which inherits the text colour of its container.
It is decorative: hidden from UI Automation, not focusable, not hit-testable. The button or text next to it carries the name.

| Property | Default | Notes |
|---|---|---|
| `Kind` | `None` | An `IconKind` |
| `Size` | 18 | 16 inside buttons, 18 in lists and the sidebar, 20 for page-level marks, 32 in empty states |
| `Foreground` | inherited | Set to a status brush where the icon shows status |
| `IsFilled` | false | Fills the icon's "duo" area at 14 % (selected sidebar item, empty state icon) |
| `StrokeWidth` | 1.5 | In the 24-unit grid |

```xml
<controls:Icon Kind="Drop" Size="16" />
<controls:Icon Kind="Alert" Size="16" Foreground="{DynamicResource Brush.Critical}" />
```

`IconKind` values (the full list):

| Group | Kinds |
|---|---|
| Navigation | `Home`, `Donor`, `Cal`, `Calplus`, `Patient`, `Request`, `Stock`, `Receipt`, `Staff` |
| Actions | `Search`, `Plus`, `Filter`, `Sort`, `Columns`, `Edit`, `Trash`, `Print`, `More`, `Enter`, `Arrowright`, `Logout`, `Sync`, `Userplus`, `Sidebar` |
| Status | `Alert`, `Check`, `Checkc`, `X`, `Info`, `Clock`, `Bell`, `Heart`, `Drop`, `Thermo`, `Trend`, `History`, `Inbox` |
| People and contact | `User`, `Phone`, `Pin` |
| Chevrons | `Chevd`, `Chevr`, `Chevl` |
| Security and settings | `Key`, `Lock`, `Eye`, `Eyeoff`, `Gear`, `Sun`, `Moon`, `Monitor`, `Keyboard` |
| Special | `None` |

Use `Checkc` (circled check) for completed and success, `Alert` for warning, critical and error, `Clock` for pending and expiring, `X` for cancelled,
`Info` for information, `Sync` for waking and updating, `Inbox` for empty states. Always place the icon before its text. An icon-only button must have
`AutomationProperties.Name` and a tooltip. Do not add icons by drawing paths in a view; extend the SVG set and regenerate.

Legacy: `IconText` (a `TextBlock` with `Style="{DynamicResource Text.Icon}"`) renders Segoe glyphs. `ListFooter`, `DateField`, the caption buttons and
the 2.0 screens still use it. Replace it with `controls:Icon` when you rebuild a file.

### 4.3 LogoMark

`controls:LogoMark` is the Lifestream mark: a blood drop with a white stream wave, drawn from vector geometry at any size in `Brush.Blood` (the wave is
`Brush.OnBlood`). Its template is in `Chrome/Chrome.xaml`. It is decoration and is skipped by screen readers; the wordmark next to it is live text.

```xml
<controls:LogoMark Width="20" Height="20" />   <!-- title bar -->
<controls:LogoMark Width="34" Height="34" />   <!-- sign-in -->
```

Pair it with `TextBlock Text="Lifestream"` (14 SemiBold in the title bar, 20 SemiBold on sign-in).

## 5. Components

All styles below are in the theme (merged by `ThemeLoader`). Style keys are used as `{DynamicResource Key}` in views. Components with a
`TargetType` and no key (`Section`, `PageHeader`, `Badge`, ...) get their style automatically.

### 5.1 Buttons

Styles in `Controls/Buttons.xaml`. A bare `Button` gets `Button.Secondary`. All are 34 DIP high with 14 DIP padding, 14 Medium text, radius 6; the
`.Small` variants are 28 DIP high, 10 DIP padding and 13 text.

| Style | Look | Use |
|---|---|---|
| `Button.Primary` | Filled `Primary`, `OnPrimary` text | The one main action of a view |
| `Button.Secondary` (also `Button.Tonal`) | `Layer` fill, 1 px `Stroke2` edge | Every other action |
| `Button.Subtle` (also `Button.Ghost`) | No fill and no edge until hover | Quiet actions: Columns, View all, Cancel, Copy |
| `Button.Danger` | `Layer` fill, `Critical` text, `CriticalStroke` edge | Destructive confirm only: "Delete donor" |
| `Button.Icon` | Square 34 x 34 subtle button holding one `Icon` | Icon-only actions. Needs `AutomationProperties.Name` and a tooltip. |
| `Button.Chip` | Secondary with a dashed edge | Filter chip at rest ("+ Blood group"). The filled "set" state is not built. |
| `.Small` of `Primary`, `Secondary`, `Subtle`, `Danger`, `Icon` | 28 high | Rows, panels, banners, toasts |

States: rest, hover (`Hover` or one step of the variant), pressed, keyboard focus (a 2 px `Focus` ring 2 px outside the control, drawn only when
the last input was the keyboard, `FocusAssist`), disabled (`Layer2` fill, `Ink3` text), busy.

**The focus ring, on every control.** One ring: 2 px of `Focus`, 2 px outside the control, with the control's corner radius plus 4
(`Radius.FocusRing` for a 6 radius control, `Radius.FocusRingSmall` for a check box). It shows for keyboard focus only, never after a mouse
click, a tap or a pen press. A template shows it with a trigger on `controls:FocusAssist.ShowRing` and never on `IsKeyboardFocused`, which is
also true after a click (a test scans for it). `FocusAssist` decides like a browser's `:focus-visible`: a key that moves or acts (Tab, the
arrows, Space, Enter, a letter) turns keyboard mode on; a modifier or system key on its own, or a key pressed with Alt or the Windows key
(Alt+Tab, Win+Shift+S, Print Screen), does not; a press of the mouse, a finger or a pen turns it off and removes the ring that is showing,
also when the focus stays where it was. A text field is the one exception by design: its focus bar shows whenever it has the caret.

Busy: `controls:ButtonAssist.IsBusy="{Binding Cmd.IsRunning}"` and `controls:ButtonAssist.BusyText="Saving"`. A spinner and the progressive verb
replace the label, the width does not change, further clicks are ignored (marked handled), UI Automation `ItemStatus` is `Busy`, and busy wins over
disabled so a running command does not look dead. The default `BusyText` is "Working..."; always set a specific verb.

A button whose content shows no text (an icon alone, or an icon with a label that a compact window has collapsed) turns busy with the spinner alone
and stays as wide as its icon. The template's root (`ButtonHost`) finds this out each time the button is measured and sets the read-only
`ButtonAssist.HasLabel`; nothing needs setting in the view (an empty `BusyText` is not needed for it). `BusyText=""` is a choice, not a workaround: a
button with a short label ("Edit", "Reschedule") that a progressive verb would widen turns busy with the spinner alone, centred.

`Button.Subtle` has no fill of its own, so it sits on a panel, on the page canvas or on Mica. Its hover and pressed fills are tints of ink
(`Well`, then `CaptionHover` over it), which show on all three; the opaque `Hover` token is within a shade of the canvas and would vanish there.

```xml
<Button Style="{DynamicResource Button.Primary}" Command="{Binding SaveCommand}" IsDefault="True"
        controls:ButtonAssist.IsBusy="{Binding SaveCommand.IsRunning}" controls:ButtonAssist.BusyText="Saving"
        AutomationProperties.AcceleratorKey="Ctrl+S" ToolTip="Save (Ctrl+S)">
  <StackPanel Orientation="Horizontal">
    <controls:Icon Kind="Check" Size="16" Margin="{DynamicResource Inset.R8}" />
    <TextBlock Text="Save donor" />
  </StackPanel>
</Button>
<Button Style="{DynamicResource Button.Icon}" Command="{Binding MoreCommand}" AutomationProperties.Name="More actions" ToolTip="More actions">
  <controls:Icon Kind="More" Size="16" />
</Button>
```

Do: one `Button.Primary` per view; a verb plus object on the label ("Record donation", "Add donor"); the danger button names the object ("Delete
donor"); icon before text at size 16.
Don't: two primary buttons; a danger button as a primary action; "OK" or "Submit"; a filled red button.

### 5.2 Text fields

`TextBox`, `PasswordBox` and `ComboBox` get their Lifestream styles automatically (`TextBox.Lifestream`, `PasswordBox.Lifestream`,
`ComboBox.Lifestream`). 34 DIP high, radius 6, `Layer` fill, 1 px `Stroke2` border with a `StrokeStrong` bottom edge, 10 DIP side padding.

| State | Look |
|---|---|
| Hover | `Layer2` fill |
| Focus | A 2 px `Primary` bar along the bottom, faded in over `Motion.Fast` |
| Error | `Critical` border and a 2 px `Critical` bar. Shown when `Validation.HasError` is true on the input, and when `InputAssist.HasError` is true (see below). |
| Disabled | `Layer2` fill, `Stroke` border, `Ink3` text |
| Read-only | The quiet `Layer2` fill and `Stroke` border of a disabled field, but `Ink` text: it can be read and copied |

**One error look, and never WPF's red adorner.** Every input style sets `Validation.ErrorTemplate` to null (`Input.Base`, the check box, the
password field, the date field, the record picker), so the saturated red rectangle WPF draws over an invalid control never appears. The error is the
field's own Critical edge and bar. `InputAssist.HasError` is an inherited attached property that the field templates trigger on: a `FormField` sets
it while it shows an `Error`, and a composite input (`PasswordField`, `DateField`, `RecordPicker`) sets it from its own `Validation.HasError`, so the
box inside looks exactly like a plain field in error. A new input control must set `Validation.ErrorTemplate="{x:Null}"` and show the same look.

The label lives above the field, never inside it. Wrap the input in `controls:FormField`:

| Property | Meaning |
|---|---|
| `Label` | 13 Medium `Ink2` label, 6 above the input; the label is associated with the input for UI Automation |
| `Hint` | 12 `Ink3` help text 6 below; replaced by the error while there is one |
| `Error` | 12 `Critical` message with a 14 `Alert` icon; assertive live region, so a new error is announced |
| `IsRequired` | Marks the field required |
| `HasError` | Read-only; true while `Error` has text |

`FormField` sets `Margin` to `Gap.Field` (16 below). `controls:FormFocus.IsEnabled="True"` on the form's root and
`controls:FormFocus.FieldName="Name"` on each `FormField` let the view model move focus to the first invalid field on submit.

`controls:InputAssist.Placeholder="..."` shows a hint inside an empty field (`Ink3`). It is not part of the value, and it also becomes the automation
help text. Use it for format hints, not as a label.

```xml
<controls:FormField Label="Phone" Hint="Include the country code." Error="{Binding PhoneError}">
  <TextBox Text="{Binding Phone, UpdateSourceTrigger=PropertyChanged}" MaxLength="24"
           controls:InputAssist.Placeholder="+44 7700 900123" AutomationProperties.Name="Phone" />
</controls:FormField>
```

**Search field:** `Style="{DynamicResource TextBox.Search}"` adds a 16 `Search` icon at the left (`Ink3`) and, with
`controls:SearchAssist.Shortcut="Ctrl F"`, two `Kbd` key caps at the right while the field is empty (the string is split on spaces, one key each).

```xml
<TextBox Style="{DynamicResource TextBox.Search}" Width="320" HorizontalAlignment="Left"
         Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
         controls:InputAssist.Placeholder="Search donors" controls:SearchAssist.Shortcut="Ctrl F"
         AutomationProperties.Name="Search donors" AutomationProperties.AcceleratorKey="Ctrl+F" />
```

**Password:** `controls:PasswordField` (`Password` two-way, `ShowToggle`, `IsRevealed`, `IsReadOnly`) is the only masked input; it has an optional Show
toggle and `Password` always mirrors the box. **Record picker:** `controls:RecordPicker` is the searchable async combo for donor, patient, staff,
appointment and request (300 ms debounce, skeleton rows while searching, "No matches", a summary chip with Change after choosing).

Do: a label above every field; errors that say cause and fix ("Enter the phone number with its country code"). Don't: a label inside the field, an
error that only says "Invalid", a placeholder as the only label.

### 5.3 Select (ComboBox)

`ComboBox` is styled by default: 34 DIP, same border and focus behaviour as a text field, error state through `Validation.HasError`. The popup is
the menu surface (`Layer`, 1 px `Stroke`, radius 8, padding 4, flyout shadow on a separate border behind it); items are 34 DIP, the selected item is
`Selected` and SemiBold with a `Check` at the right. Put it in a `FormField`.

```xml
<controls:FormField Label="Blood group">
  <ComboBox ItemsSource="{Binding BloodGroups}" SelectedItem="{Binding BloodGroup}" AutomationProperties.Name="Blood group" />
</controls:FormField>
```

### 5.4 Date picker

Use `controls:DateField` wherever a date is entered. It is a text box in the one app format `dd MMM yyyy` (for example "05 Mar 2022", help text
"Format: 05 Mar 2022") plus a calendar button. A typed text that does not parse is never guessed: the previous date stays and `ParseError` explains the
format, which a `FormField` can show as its `Error`. `MinDate` and `MaxDate` limit both the typed text and the calendar (blacked-out days cannot be
clicked); the chosen date stays selectable when outside the limits (rescheduling). The built-in `DatePicker` (`DatePicker.Lifestream`) and `Calendar`
(`Calendar.Lifestream`, `Calendar.Popup`) are themed but cannot take that format. `DateField` still uses 2.0 brush names and an `IconText` glyph.

### 5.5 Check box, radio button, toggle switch

| Control | Style | Look |
|---|---|---|
| Check box | `CheckBox.Lifestream` (default) | 18 x 18, radius 4. Checked: `Primary` fill and a 13 `Check`; indeterminate: an 8 x 2 bar. 13 `Ink2` label, 8 gap. |
| Radio button | `RadioButton.Lifestream` (default) | 18 circle with a `Stroke2` ring; selected: `Primary` ring and an 8 `Primary` dot. Label 13 `Ink2`. Always set `GroupName`. |
| Toggle switch | `ToggleSwitch` (on a `CheckBox`) | 40 x 20 track, radius 10. Off: `Layer` with a `StrokeStrong` edge and a 12 `Ink2` knob at the left. On: `Primary` track, `OnPrimary` knob at the right; the knob slides over `Motion.Fast`. |

All have hover, pressed, focus ring and disabled states. Use a toggle for a setting that applies at once, a check box for a choice that is saved with a form.

```xml
<CheckBox Content="Keep me signed in" IsChecked="{Binding KeepSignedIn}" />
<CheckBox Style="{DynamicResource ToggleSwitch}" Content="Show expired units" IsChecked="{Binding ShowExpired}" />
```

### 5.6 Segmented control

A well (`Brush.Well`, radius 8, padding 2,3) of segments (padding 12,5, 13 text, radius 6); the selected segment is a `Layer` face, `Ink`, SemiBold, with
a soft shadow on a separate border behind it. Two forms: `Style="{DynamicResource Segmented}"` on a `ListBox` (its `ItemContainerStyle` is
`Segmented.Item`), or radio buttons in a `Border Style="Segmented.Well"` with `Style="Segmented.Option"` and one `GroupName`. Use for 2 to 4 modes of
the same data (Chart or Table, Light or Dark).

```xml
<Border Style="{DynamicResource Segmented.Well}">
  <StackPanel Orientation="Horizontal">
    <RadioButton Style="{DynamicResource Segmented.Option}" GroupName="Mode" Content="Chart" IsChecked="True" />
    <RadioButton Style="{DynamicResource Segmented.Option}" GroupName="Mode" Content="Table" />
  </StackPanel>
</Border>
```

### 5.7 Tabs and Tab.Option

Underline tabs: items with 10 DIP side and 8 DIP vertical padding, 13 text; the selected tab is SemiBold `Ink` with a 2 px bar along its bottom, over a 1 px `Stroke` line.
`Tabs` and `Tabs.Item` style a `TabControl`. For view tabs in a page header (saved views such as Today, Upcoming, Past), use radio buttons:

```xml
<Border Style="{DynamicResource Tabs.Strip}">
  <StackPanel Orientation="Horizontal" Margin="0,0,0,-1">
    <RadioButton Style="{DynamicResource Tab.Option}" GroupName="View" Content="All" IsChecked="{Binding IsAll, Mode=OneWay}" Command="{Binding ShowAllCommand}" />
    <RadioButton Style="{DynamicResource Tab.Option}" GroupName="View" Content="Low" Command="{Binding ShowLowCommand}" />
    <RadioButton Style="{DynamicResource Tab.Option}" GroupName="View" Content="Expiring" Command="{Binding ShowExpiringCommand}" />
  </StackPanel>
</Border>
```

The `-1` bottom margin lets the 2 px bar cover the strip's line. Assign the strip to `PageHeader.Tabs`.

The selected bar is `Brush.SelectedBar` in both `Tabs.Item` and `Tab.Option` (red means blood; a tab is navigation).

### 5.8 Badge

`controls:Badge`: 22 high, radius 4, 12 Medium text, a soft fill in the status colour. It never carries meaning by colour alone: give a status an icon or
a dot.

| Property | Meaning |
|---|---|
| `Text` | The label (also the automation name) |
| `Kind` | `Neutral` (`Layer2` fill, `Ink2`, 1 px `Stroke`), `Success`, `Warning`, `Critical`, `Info` (soft fill, status text) |
| `ShowDot` | A 6 px dot in the text colour before the text |
| `Icon` | A 13 px icon before the text |

```xml
<controls:Badge Text="Eligible" Kind="Success" ShowDot="True" />
<controls:Badge Text="Low stock" Kind="Critical" Icon="Alert" />
<controls:Badge Text="40" Kind="Neutral" />   <!-- a count -->
```

A badge is as wide as its words, and a column that holds badges is budgeted for them (`MinWidth`). If a badge is ever given less, its label ends in an
ellipsis and the badge shows the whole text as its tooltip (a tooltip you set on the badge is left alone): never cut mid-letter.

### 5.9 BloodGroupTag

`controls:BloodGroupTag`: `Blood` text on `BloodSoft`, 12 SemiBold, at least 34 wide and 22 high. `IsLarge="True"` makes it 32
high, 15 px and at least 46 wide (detail headers); it is `Blood` text at both sizes, whatever the text colour of the panel around it. Its automation name is "Blood group {Text}". Bind a value that went through
`BloodType.ToTypographic()` so the minus is U+2212.

```xml
<controls:BloodGroupTag Text="{Binding Group}" />
<controls:BloodGroupTag Text="AB+" IsLarge="True" />
```

```csharp
public string Group => BloodType.ToTypographic();   // "O−", never "O-" on screen
```

Don't colour a tag by group or status; red here is the group's identity.

### 5.10 StatusChip

`controls:StatusChip` is a `Badge` chosen by a record status, so the icon, word and colour always agree.

| `StatusKind` | Word | Badge kind | Icon |
|---|---|---|---|
| `Pending` | Pending | Info | `Clock` |
| `Completed` | Completed | Success | `Checkc` |
| `Cancelled` | Cancelled | Neutral | `X` |
| `Incomplete` | Incomplete | Warning | `Alert` |
| `LowStock` | Low stock | Critical | `Alert` |
| `Expiring` | Expiring | Warning | `Clock` |

`Label` replaces the word when the record's own word is more exact ("No show", "Fulfilled"); the icon and colour stay those of `Status`.

```xml
<controls:StatusChip Status="{Binding Status}" Label="{Binding StatusLabel}" />
```

### 5.11 Avatar

`controls:Avatar`: a circle with `Initials` (one or two letters) on `Selected`, or on `BloodSoft` with `Blood` text when `IsBlood="True"` (donors and
the signed-in staff in the account button). `Source` shows a photo clipped to the circle. `Size` is the diameter (default 32); the font is
round(Size x 0.375).

A person with no photo is shown by their initials, never by a blank or generic disc: `HasPhoto` is false, and the initials show, when `Source` is
null and also when it is `Image.DefaultAvatar` (the placeholder drawing the 2.0 `AvatarConverter` returns for "no photo"). Give `Source` a converter
that returns null for no photo (`PhotoSourceConverter`). On a selected table row, which is filled with the avatar's own `Selected` colour, the disc
is `Layer`, so it stays visible.

```xml
<controls:Avatar Initials="AM" Size="40" IsBlood="True" />
```

### 5.12 Section

`controls:Section`: a panel (`Layer`, 1 px `Stroke`, `Radius.Panel`) with a header row and the content below it, clipped to the rounded bottom corners.

| Property | Meaning |
|---|---|
| `Header` | 15 SemiBold heading (a level 2 heading for screen readers; also the automation name) |
| `Subtitle` | Muted text after the heading ("4 items") |
| `HeaderActions` | Content right-aligned in the header row (a button, a segmented control, a legend) |
| `Padding` | Content padding; default `Pad.PanelBody` (18,0,18,14). Set `Padding="0"` when the content is a table that must touch the edges. |

The header row is hidden when `Header` is empty and `HeaderActions` is null. No eyebrow labels, no decorative dividers.

```xml
<controls:Section Header="Upcoming appointments" Subtitle="4 items">
  <controls:Section.HeaderActions>
    <Button Style="{DynamicResource Button.Subtle.Small}" Content="View all" />
  </controls:Section.HeaderActions>
  <ItemsControl ItemsSource="{Binding Upcoming}" />
</controls:Section>
```

### 5.13 PageHeader

`controls:PageHeader` is the top of a page that sets `controls:ShellPage.OwnsHeader="True"`.

| Property | Meaning |
|---|---|
| `Overline` | A 13 muted line above the title (the date on the dashboard); hidden when empty |
| `Title` | 28/36 SemiBold, a level 1 heading for screen readers |
| `Count` | Text of a neutral badge next to the title ("40"); hidden when empty, and while `State` is `Loading`, `Waking` or `Error` (a "0" there would be false) |
| `State` | The page's load state. The default style reads it from the `State` of the view model the header is bound to, so a list screen need not set it |
| `Actions` | Content at the right end of the title row, bottom-aligned with the title: the one primary action and its neighbours |
| `Tabs` | A full-width row 16 below the title (a `Tabs.Strip`) |

```xml
<controls:PageHeader Title="Donors" Count="{Binding Total}" Margin="{DynamicResource Inset.B16}">
  <controls:PageHeader.Actions>
    <Button Style="{DynamicResource Button.Primary}" Command="{Binding NewCommand}" ToolTip="Add donor (Ctrl+N)"
            AutomationProperties.AcceleratorKey="Ctrl+N">
      <StackPanel Orientation="Horizontal">
        <controls:Icon Kind="Plus" Size="16" Margin="{DynamicResource Inset.R8}" />
        <TextBlock Text="Add donor" />
      </StackPanel>
    </Button>
  </controls:PageHeader.Actions>
</controls:PageHeader>
```

### 5.14 Data grid

`controls:LifestreamDataGrid` (a `DataGrid`) is the grid every list uses. It is virtualised and recycling (10,000 items create a screenful of rows);
columns size to content with a 64 DIP minimum; a text cell shows a tooltip with the whole text only when it is truncated; sorting is requested from the
server through `SortCommand` (the grid shows the indicator but does not reorder loaded rows), and the sort lives in the view model so it survives
filtering and paging. A list view model that implements `IScrollMemory` gets its scroll position back.

| Part | Look |
|---|---|
| Grid | Transparent, no border (a panel wraps it). `GridLinesVisibility="None"`, column headers only |
| Header (`DataGridColumnHeader.Lifestream`) | 38 high on `Layer2`, 13 Medium `Ink2`, a 1 px `Stroke` line under it, a 13 `Chevd` sort glyph (turned for ascending), 12 DIP side padding |
| Row (`DataGridRow.Lifestream`) | At least 44 high, 1 px `Stroke` line under each, `Hover` on hover, `Selected` with a 3 px `SelectedBar` inside the left edge when selected. Never the system highlight, also when the grid has lost focus. |
| Cell (`DataGridCell.Lifestream`) | 12 DIP side padding, 16 left in the first column; keyboard focus draws an inset 2 px ring |

Cell text styles: `DataGrid.TextCell` (14 `Ink`, one line, ellipsis, tooltip when cut; carries no padding of its own), and for a two-line cell
`DataGrid.PrimaryLine` (14 Medium) over `DataGrid.SecondaryLine` (12 `Ink3`). Right-align numbers with `Text.Numeric`.

| Property | Meaning |
|---|---|
| `SortCommand` | Run with the `SortMemberPath` of the clicked column |
| `SortColumn`, `SortDescending` | The view model's sort, bound `Mode=OneWay`, so the header shows it |

```xml
<controls:LifestreamDataGrid ItemsSource="{Binding Items}" SelectedItem="{Binding SelectedItem, Mode=TwoWay}"
                             SortCommand="{Binding SortCommand}" SortColumn="{Binding SortColumn, Mode=OneWay}"
                             SortDescending="{Binding SortDescending, Mode=OneWay}" AutomationProperties.Name="Donors">
  <DataGrid.Columns>
    <DataGridTemplateColumn Header="Name" SortMemberPath="name" Width="2*">
      <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
          <StackPanel VerticalAlignment="Center">
            <TextBlock Style="{DynamicResource DataGrid.PrimaryLine}" Text="{Binding Name}" />
            <TextBlock Style="{DynamicResource DataGrid.SecondaryLine}" Text="{Binding Code}" />
          </StackPanel>
        </DataTemplate>
      </DataGridTemplateColumn.CellTemplate>
    </DataGridTemplateColumn>
    <DataGridTemplateColumn Header="Group" SortMemberPath="bloodType" Width="90">
      <DataGridTemplateColumn.CellTemplate>
        <DataTemplate><controls:BloodGroupTag Text="{Binding Group}" HorizontalAlignment="Left" VerticalAlignment="Center" /></DataTemplate>
      </DataGridTemplateColumn.CellTemplate>
    </DataGridTemplateColumn>
    <DataGridTemplateColumn Header="Donations" SortMemberPath="donations" Width="100">
      <DataGridTemplateColumn.CellTemplate>
        <DataTemplate><TextBlock Style="{DynamicResource Text.Numeric}" Text="{Binding Donations}" HorizontalAlignment="Right" VerticalAlignment="Center" /></DataTemplate>
      </DataGridTemplateColumn.CellTemplate>
    </DataGridTemplateColumn>
  </DataGrid.Columns>
</controls:LifestreamDataGrid>
```

A row is exactly `Height.Row` (44) tall with its separator: the 1 px line is drawn over the row's last pixel row, it does not add one.

`controls:ColumnAssist.HeaderAlignment="Right"` on a column puts its header over right-aligned numbers, and `"Center"` over centred values (the Stock
age bands); the grid applies it when it loads (a view cannot name the grid's header style before it is in a window). Every numeric column sets it:
do not build a header style in a view. The render tests check each such header against its values (`RenderChecks`).

`TruncationTooltip.IsTrimmed` measures a text in the mode it is drawn in (Display in the app's windows), so a text that fits exactly is not taken for
a cut one.

Not built: `ColumnPriority` (see the screen guide), a column chooser, and the Compact (34 DIP) row height. `MinRowHeight` is fixed at `Height.Row`.

### 5.15 SidePanel

`controls:SidePanel`: the right-hand detail sheet. `Layer` with a 1 px `Stroke` on its left edge and a soft shadow cast to the left (on a separate
layer). It is collapsed while closed, so an `Auto` grid column holding it takes no room. It never takes focus by itself.

| Property | Meaning |
|---|---|
| `IsOpen` | Show or hide. Opening slides 24 px and fades over 200 ms (decelerate); closing takes 150 ms (accelerate). With animations off it just appears. |
| `Overline` | Small muted line at the left of the header row ("Donor D-0175"); also the automation name |
| `HeaderActions` | Content before the close button (a "..." menu button) |
| `CloseCommand` | Run by the close button and Esc; when null they set `IsOpen` to false |
| `PanelWidth` | 420 by default (`Width.Panel`); 352 (`Width.PanelCompact`) in a Compact window, by the panel's own style. A view that sets `PanelWidth` keeps its own width |

The panel pads its content 24,24,24,20 (the 20 below keeps a form's sticky footer, with its divider above the buttons, clear of the window's bottom
edge) and shows a close button (`Button.Icon.Small`, name "Close panel", tooltip "Close (Esc)"). The content is yours;
see section 7.6 for the standard layout.

**A scrolling body in a panel** uses `Style="{DynamicResource ScrollViewer.Panel}"`. The panel marks everything inside it (`SidePanel.IsInside`,
inherited); the style then lets the viewer reach through the panel's 24 right padding and pads its content by the same 24. So the content keeps its
width and its 24 right edge whether or not it scrolls, and the scroll bar lies in the padding, never over a tag, a banner or a bag number. Do not
add a gutter of your own. Outside a side panel the style changes nothing.

### 5.16 DialogSurface and ModalOverlay

`controls:DialogSurface` is the dialog container: `Layer`, radius 12, 1 px `Stroke`, the dialog shadow behind it, at most 560 wide. `Title`
(20/28 SemiBold, level 2 heading, automation name), the content with 24 padding, and an optional `Footer` right-aligned above a 1 px `Stroke` line.

`controls:ModalOverlay` is the full-size modal layer: a `Scrim`, the content centred, a fade plus scale from 0.98 over 180 ms on opening, Esc and a click
on the scrim run `CloseCommand` (or set `IsOpen` to false), Tab cycles inside the content, focus moves into the content on open and returns to where it
was on close. Place it as the last child of the window's root grid so it covers everything.

```xml
<controls:ModalOverlay IsOpen="{Binding IsDeleteOpen}" CloseCommand="{Binding CancelDeleteCommand}">
  <controls:DialogSurface Title="Delete donor?" Width="{DynamicResource Width.Dialog}">
    <TextBlock Style="{DynamicResource Text.Body}" Text="Hana Greenleaf and her donation history will be removed. This cannot be undone." />
    <controls:DialogSurface.Footer>
      <StackPanel Orientation="Horizontal">
        <Button Style="{DynamicResource Button.Secondary}" Content="Keep donor" Command="{Binding CancelDeleteCommand}" Margin="{DynamicResource Inset.R8}" />
        <Button Style="{DynamicResource Button.Danger}" Content="Delete donor" Command="{Binding DeleteCommand}" />
      </StackPanel>
    </controls:DialogSurface.Footer>
  </controls:DialogSurface>
</controls:ModalOverlay>
```

Dialogs are for multi-step work (record a donation, fulfil a request) and confirmations. Editing a record happens in the side panel (planned, not built).
The older `Controls/DialogWindow` (a separate window) is the 2.0 route and is still used by existing dialogs. A confirmation names the record, starts
focus on the cancelling button, and its danger button says verb and object.

### 5.17 ElevatedSurface

`controls:ElevatedSurface`: a floating surface (menu, popover, palette) with `Layer`, 1 px `Stroke` and the shadow of its `Level`, drawn by a separate
border behind the content.

| Property | Meaning |
|---|---|
| `Level` | `Flyout`, `Dialog` or `Panel` (see Elevation) |
| `CornerRadius` | Radius of the surface and its shadow; use `{DynamicResource Radius.Panel}` |

```xml
<controls:ElevatedSurface Level="Flyout" CornerRadius="{DynamicResource Radius.Panel}" Padding="{DynamicResource Pad.4}">...</controls:ElevatedSurface>
```

A popup that hosts one needs `AllowsTransparency="True"` and a margin of about 16 around the surface (`Inset.Popup` is 8,4,8,16) so the blur is not
clipped.

### 5.18 Toast

`ToastHost` (bottom right of the page area, margin 24) shows `ToastView` items from `IToastService`. A toast is a `Layer` panel with the flyout
shadow behind it, 280 to 470 wide, an icon by kind (`Success` is `Checkc` in `Success`, `Warning` is `Alert` in `Warning`, `Error` is `Alert` in
`Critical`, otherwise `Info`), the message (14 SemiBold), an optional action button (View, Undo, Reload) and a dismiss button. The host is a polite live
region; a new toast raises a live-region event.

Lifetimes: 8 s by default, 10 s when the toast carries an action such as Undo; a caller may ask for 6 to 10 s (clamped). A toast pauses while hovered
or focused. A toast outlives the screen that showed it.

```csharp
toasts.ShowForRecord(ToastKind.Success, "Donation", donor.Name, "recorded", "Undo", UndoAsync);   // "Donation for Hana Greenleaf recorded"
```

Copy: the message uses the same verb as the button that caused it (button "Record donation", toast "Donation recorded") and names the record. If the
action's server call waits on a waking server, the shell shows a waking notice for it.

### 5.19 EmptyState and ErrorState

`controls:EmptyState`: a dashed `Stroke2` rectangle on `Layer`, a 32 `Icon` (`Inbox` by default), a `Title` (14 SemiBold), a `Message` (13, at most 360
wide) and an action button (`ActionText`, `ActionCommand`). Hidden parts collapse when null. Copy rule: one line saying the cause in context, plus a next
action. "No donors match these filters" and "Clear filters", not "Nothing here".

`controls:ErrorState`: a solid `Stroke2` panel, a 32 `Alert` icon in `Critical`, a plain-language `Message` (assertive live region), a **Retry** button
(`RetryCommand`) and a **Details** toggle that reveals the technical `Detail` in a read-only box with Copy. A raw exception never appears in the main
text.

Normally you do not place these yourself; `LoadSurface` shows them.

### 5.20 LoadSurface and skeletons

`controls:LoadSurface` shows exactly one of four things in one cell (SHELL-28): the skeleton, the content, an empty state or an error state. Because they
share a cell, the skeleton is replaced in place and nothing shifts. Only the state on show takes room: the skeleton is collapsed outside `Loading` and
`Waking`, and the content is collapsed in `Empty` and `Error`, so those two states start at the top of the surface, where the list's first row would
be, on every screen (while loading the content stays hidden, not collapsed, so a surface that sizes to its content keeps its place). Bind it to a view model's `State` (`LoadState`: `Loading`, `Ready`, `Empty`,
`Error`, `Waking`).

| Property | Meaning |
|---|---|
| `State` | `Loading` shows the skeleton; `Waking` shows the skeleton and a "Waking up the server, this can take about a minute and a half" banner with Retry and Cancel; `Empty` and `Error` show those states; `Ready` shows the content |
| `SkeletonKind` | `List`, `Detail`, `Dashboard`, `StockTiles`, `Picker` or `Profile` |
| `SkeletonColumns` | For `List`: the same column spec as the grid ("2*,100,90,*,130") so the bars line up |
| `ErrorMessage`, `ErrorDetail`, `RetryCommand`, `CancelCommand` | Error state and waking banner |
| `EmptyIcon`, `EmptyTitle`, `EmptyMessage`, `EmptyActionText`, `EmptyActionCommand` | Empty state (`EmptyIcon` default `Inbox`; `EmptyGlyph` is the legacy Segoe glyph) |

`controls:Skeleton` (`Kind`, `ColumnWidths`, `RowCount`) draws bars shaped like the real surface; it shimmers over 1.4 s, is static when animations are
off, never takes focus, and is exposed to UI Automation only as one polite "Loading". A load still running after 10 s becomes an error state in the
view model.

### 5.21 Progress bar

The default `ProgressBar` style: a 4 high `Well` track with a `Primary` fill, radius 2. Indeterminate shows a 30 % segment sliding over 1.4 s (resting
in the middle when animations are off). Use it for a known long operation inside a panel; use a busy button for a single action.

### 5.22 MeterBar

`controls:MeterBar` is the stock meter: an 8 DIP track with a 1 DIP inner stroke, the filled share, an optional hatched share drawn over the start of the
fill (units expiring soon) and a minimum tick that reaches 4 DIP above and below the track. It draws no text; the count beside it is normal text. Edges
are snapped to device pixels.

| Property | Meaning |
|---|---|
| `Value` | Filled share, 0 to 1 |
| `Hatched` | Hatched share at the start of the fill, 0 to 1, never more than `Value` |
| `Minimum` | Position of the minimum tick, 0 to 1; negative hides it |
| `Track`, `TrackStroke`, `Fill`, `Hatch`, `Tick` | Brushes; set them from the theme |

```xml
<controls:MeterBar Value="{Binding FillFraction}" Hatched="{Binding ExpiringFraction}" Minimum="{Binding MinimumFraction}"
                   Track="{DynamicResource Brush.Layer2}" TrackStroke="{DynamicResource Brush.Stroke}" Fill="{DynamicResource Brush.Ink2}"
                   Hatch="{DynamicResource Brush.WarningHatch}" Tick="{DynamicResource Brush.Ink3}" />
```

A low group switches `Fill` and the count to `Brush.Critical`; the count must say so in words as well ("2 units, below the minimum of 10").

### 5.23 Tooltip, context menu, Kbd

**Tooltip** (`ToolTip.Lifestream`, applied to every `ToolTip`): the menu surface with 10,6 padding, 13 Medium text, at most 380 wide, flyout shadow behind
it. Add the shortcut after the text: `ToolTip="Record donation (Ctrl+D)"`. Every icon-only button has a tooltip.

**Context menu** (`ContextMenu.Lifestream`, `MenuItem.Lifestream`, and a themed `Separator`): `Layer`, 1 px `Stroke`, radius 8, padding 4, items 34
high with radius 4, flyout shadow behind. They are implicit and name the font themselves, so every menu (account menu, the text-edit menu on every field,
the "..." menu of a panel) is themed. Show a shortcut with `InputGestureText="Ctrl+B"`.

```xml
<ContextMenu Style="{DynamicResource ContextMenu.Lifestream}">
  <MenuItem Style="{DynamicResource MenuItem.Lifestream}" Header="Edit donor" Command="{Binding EditCommand}" InputGestureText="Ctrl+E" />
  <Separator />
  <MenuItem Style="{DynamicResource MenuItem.Lifestream}" Header="Delete donor" Command="{Binding DeleteCommand}" />
</ContextMenu>
```

**Kbd**: a key cap. A `Border Style="{DynamicResource Kbd}"` holding a `TextBlock Style="{DynamicResource Kbd.Text}"`, one per key.

```xml
<Border Style="{DynamicResource Kbd}"><TextBlock Style="{DynamicResource Kbd.Text}" Text="Ctrl" /></Border>
<Border Style="{DynamicResource Kbd}"><TextBlock Style="{DynamicResource Kbd.Text}" Text="K" /></Border>
```

### 5.24 Overlay scrollbars

`Theme/ScrollBars.xaml` restyles every `ScrollViewer` (implicit style) and every `ScrollBar`. The bar sits in the same grid cell as the content, so it
never shifts the layout. At rest a 2 DIP thumb (`Ink3` at 55 %) shows 3 DIP in from the edge; with the pointer on the 12 DIP strip a `Well` track appears
and the thumb widens to 6 DIP over 120 ms; while dragging the thumb is `Ink2`. After 1.2 s idle the bars fade (`OverlayScroll`, switched on by the
implicit style). The resting thumb is quiet on purpose (about 2:1). A thumb in use is not: while the pointer is on the strip, the thumb is dragged, or
the keyboard is in use inside the viewer (a key press in it, or focus arriving by keyboard, until a mouse press or focus leaving), the bar is
`OverlayScroll.IsEmphasized`: the thumb is `Ink3` at 85 % or stronger, 3:1 or more against `Canvas`, `Layer`, `Layer2`, `Hover` and `Selected` in both
themes (`ScrollBarThemeTests`), with Windows animations on or off, and the bars do not fade while the keyboard holds them. A viewer whose content fits shows no bar. You normally write nothing: put content in a `ScrollViewer` with
`VerticalScrollBarVisibility="Auto"`.

| Style | Use |
|---|---|
| (implicit) | Overlay with auto-hide: pages, lists, panels, dialogs, the palette |
| `ScrollViewer.Plain` | Overlay that never fades or widens; always visible at rest width. For small popups. |
| `ScrollViewer.DataGrid` | The data grid's own viewer (header row above the rows). Used by the grid template. |

### 5.25 CappedHeightBox

`controls:CappedHeightBox` asks for at most `Cap` of height for its content but takes all the height it is given. Put a scrolling list in it when the
list stands beside a panel of another height: the list asks for a few rows, is as tall as its neighbour, and scrolls when it has more rows than room
(the dashboard's appointments beside "Needs attention"). With no `Cap` it is a plain decorator. It also keeps a control that asks for more than it
needs (the chart asks for about 300) from stretching a row.

### 5.26 Keeping a blood group in one piece

WPF may break a line before the minus sign (U+2212), which leaves "(O" at the end of one line and the minus at the start of the next. On wrapping
text that names blood groups, bind through `controls:KeepGroupsTogetherConverter`: it puts a word joiner (U+2060) before each minus sign. The bound
text is unchanged, so tooltips, automation names and copies keep the plain text.

```xml
<TextBlock Text="{Binding Detail, Converter={StaticResource KeepGroups}}" TextWrapping="Wrap" />
```

The minus itself: the font's U+2212 is as wide as its plus and sits at the same height, which is right beside a capital. The en dash is wider but
sits lower (at hyphen height) and looks dropped after a capital. The minus is used as it is.

### 5.27 Scroll bars beside the content

An overlay scroll bar is drawn over the right 12 DIP of its viewer. So that it never lies over content, give the viewer that strip: the dashboard's
page viewer reaches 14 into the page's right padding (`Inset.RN14`) and insets its content by the same (`Inset.R14`); a list inside a panel keeps
18 at its right inside the viewer (the dashboard's appointments). A side panel's body does this with one style, `ScrollViewer.Panel` (5.15).

## 6. Window and shell

### 6.1 ChromeWindow

`Chrome/ChromeWindow` is the one window chrome for every window (login, main, dialogs); it is our own, not WPF-UI's `FluentWindow`, because the caption
buttons must be exactly 46 x 44 with a click area equal to the highlight, and the Snap Layouts hit test must be ours.

- **Title bar 44 DIP** (`ChromeWindow.CaptionHeight`; the shell lowers `TitleBarHeight` to 40 in a Compact window). Caption buttons are 46 x 44 (three,
  `CaptionButtonsWidth` 138). Hover is `CaptionHover` (ink at 9 %), pressed `CaptionPressed`, close hover is `CloseHover` with a white glyph.
- **`ExtendsContentIntoTitleBar="True"`**: the window content draws the title bar (the main window's search and account, the sign-in screen) and the
  window adds only the caption buttons at the right; keep `ShellView.CaptionButtonsColumn` (138) free there. Without it the window shows a default
  caption (mark and title). Mark interactive elements in a custom bar with `WindowChrome.IsHitTestVisibleInChrome="True"`.
- **Snap Layouts:** the maximize button is not hit-testable by WPF; `WM_NCHITTEST` over it is answered with `HTMAXBUTTON` (9), which makes Windows 11
  show the Snap Layouts flyout. Hover and press are tracked from `WM_NCLEFTBUTTONDOWN/UP` and `WM_NCMOUSELEAVE`. The glyph is the restore glyph while
  maximized. Double-click maximizes; Alt+Space opens the system menu.
- **Resize border** 6 DIP (`Chrome.ResizeBorder`). **Dialogs** (`IsDialog="True"`) have only a close button and cannot be resized or maximized.
- **Drag area:** exactly the title bar. (`WindowChrome` adds its resize border to `CaptionHeight`, so the window answers "client" for the rows
  right under the title bar itself.) While maximized the title bar starts below the window's own top edge, by the maximized inset.
- **Maximized insets:** the root is padded by `ChromeWindow.MaximizedInset`, the frame Windows pushes past the screen: the resize frame plus the
  padded border (`SM_CXSIZEFRAME + SM_CXPADDEDBORDER`, 8 DIP), for the window's own DPI. `SystemParameters.WindowResizeBorderThickness` is the
  resize frame alone, half of it. A restored window on Windows 11 gets DWM's 1 px border and round corners (build 22000 and later); on Windows 10
  the root draws a 1 px `Stroke`.
- **Mica:** on Windows 11 build 22621 and later (22H2) `Dwm.SetMica` turns on the system backdrop and the window chrome extends the frame under
  the whole window; the window's copy of `Brush.Mica` becomes transparent so real Mica shows through, the composition background is transparent,
  and `IsMicaActive` is true. Elsewhere (Windows 10, early Windows 11, `LIFESTREAM_NO_MICA=1`, renders in tests) the `Brush.Mica` gradient paints
  the window, and the content area is `Canvas`. The window also sets the DWM dark frame from `ThemeService.IsDark` and refreshes it on
  `ThemeService.Changed`.
- **The frame is never extended from the top** (`Chrome.GlassFrameMica` is `0,0,0,10000`, not `-1`). The window keeps its caption and system-menu
  styles, because the system menu, Snap and the taskbar depend on them, so Windows still has caption buttons of its own for it. It draws them in the
  top part of an extended frame, and they show through wherever the title bar is transparent: with a "sheet of glass" (`-1`) 2.1.0 showed two sets
  of buttons. Extended from the bottom, the frame is under the whole window and Windows has no title bar to draw (Windows Terminal does the same:
  top margin 0 when Mica is on). `WindowChrome.UseAeroCaptionButtons="False"` only stops those buttons being clickable.
- **Transparent windows** (`AllowsTransparency`: the dialogs, which draw their own card and shadow) ask Windows for no frame: no round corners
  (`DWMWCP_DONOTROUND`), no border (`DWMWA_COLOR_NONE`), no extended frame and no root stroke. A window that asks for round corners gets Windows'
  rounded border around its whole rectangle, shadow room included.
- **Only a real window shows any of this.** The renders draw elements without a window. `tests/Lifestream.Desktop.Tests/RealWindows` shows the
  sign-in window, the main window and the dialogs as real windows outside every monitor and checks what Windows draws and answers
  (`Category=RealWindow`; skipped where there is no interactive desktop, such as a GitHub runner).
- **Text:** the window sets `UseLayoutRounding`, `SnapsToDevicePixels`, Display formatting, ClearType and fixed hinting for everything inside it.
- Main window: minimum 1024 x 640, default 1440 x 900, `Title="{Binding WindowTitle}"`.

### 6.2 Sidebar

236 DIP wide, or the 52 DIP icon rail. It sits on Mica (or `Brush.Mica`) beside the page area, which is `Canvas` with a 1 px `Stroke` on its left and top
edges and `Radius.ContentCorner` at its top-left.

| Group | Items (in `NavigationCatalog`) | Icon | Count |
|---|---|---|---|
| (none) | Dashboard | `Home` | none |
| Donations | Donors | `Donor` | none |
| Donations | Appointments | `Cal` | Pending today (the whole local day) |
| Transfusions | Patients | `Patient` | none |
| Transfusions | Requests | `Request` | Open requests |
| Inventory | Stock | `Stock` | Groups below minimum, red (`HasAlert`) when above zero |
| Inventory | Receipts | `Receipt` | none |
| Administration | Staff | `Staff` | none; shown only to administrators and the demo account (`Audience.AdminOrDemo`), hidden for others rather than showing Access denied |

- Items are `RadioButton`s with style `NavItem` (36 high, 18 `Icon`, 14 label, count in 12 `Ink3` with tabular figures), or `RailItem` in the rail.
  The selected item has a `NavActive` fill, a filled icon (`IsFilled`), a SemiBold label and a 3 px `Blood` bar at its left edge (a second place,
  with tabs, where red is used for selection). Group headers use `NavGroupHeader` (12 `Ink3`); the rail hides them and draws a 24 px `Stroke2` separator between groups.
- A count is `NavItem.Badge`; the screen reader name is "Requests, 6 open" (`AutomationName` with `BadgeNoun`). Counts refresh through
  `ShellViewModel.RefreshCountsAsync` and are quiet on failure. The shell computes them itself, the same on every screen: Appointments counts today's
  pending appointments, Requests the open requests (the Requests page's "Open" tab), Stock turns red when any group is below its minimum. On the rail the
  count sits on a 16 DIP badge at the icon's top-right corner (`NavItem.RailBadge`: the number, "99+" from three digits; `CriticalSoft` with `Critical`
  text when the destination needs attention), so collapsing the sidebar does not hide what is waiting.
- The rail shows a tooltip "Donors  Alt+2" (title and shortcut). `NavItem.Shortcut` is "Alt+1" to "Alt+8" by position among the visible items.
- The account button at the foot (avatar, name, "Doctor, morning shift"; `AccountButton` style, accessible name "Account menu") opens a menu: Profile,
  Change password, Theme (Light, Dark, Match Windows), Collapse the sidebar (Ctrl+B), Sign out.
- **Ctrl+B** toggles `UiPreferences.IsNavCollapsed` (remembered). A Compact window shows the rail regardless.

### 6.3 Title bar content (main window)

Logo and "Lifestream" at the left; the search button (`TitleSearch` style, "Search donors, patients, requests and bags", `Width.TitleSearch` 440, opens
the palette) next; then the bell (`Bell`, a 6 px `Critical` dot while anything needs attention; it opens the notifications flyout, below; its name says
how many items, "Notifications, 4 items need attention") and the avatar button (opens the account menu); then the caption buttons. In a Compact window the title bar is 40 DIP, the search becomes a
"Search" icon plus `Ctrl K` key caps button, and the bell and avatar are hidden. Text on Mica uses grayscale rendering.

**Notifications flyout** (`Shell/NotificationsFlyout`, view model `NotificationsViewModel`). The bell opens it under itself, right edges aligned,
420 DIP wide, an `ElevatedSurface` at the `Flyout` level inside the window (like the palette, not a popup). It lists what needs attention now, the same
items as the dashboard's "Needs attention" (both come from `AttentionBuilder`): each with its tinted icon tile, title, why it matters and its one
action, which closes the flyout and opens the screen on that record or filter. Below the list, "View all on the dashboard". States: the items; "Nothing
needs attention" with a green check; "Checking what needs attention" while the first answer is on its way; the load's error with Retry (never the empty
state when a call failed). It loads each time it opens; the dashboard also hands it its list whenever it loads, so the dot is right from the first
screen. Opening it moves the focus to its first action, Tab stays inside, and Esc, a click outside, the bell again or going to another screen close
it (focus returns to the bell). It is not shown in a Compact window, where the bell is hidden.

### 6.4 Command palette

`CommandPaletteView` (`Shell/`), bound to `CommandPaletteViewModel`: an `ElevatedSurface` (`Level="Dialog"`) 640 DIP wide, just under the title bar, over a
transparent layer (a click outside closes it). A 46 DIP query row (18 `Search` icon, 15 px field, a sync icon while searching, an `Esc` key cap), then a
list of grouped results at most 420 high.

- Groups, in order: Recent, Donors, Patients, Requests, Actions, Go to, Settings. With an empty query it shows recent items, then actions and screens.
- Donors, patients and requests come from the API's record search, debounced 150 ms, at most five of each kind the user may open. Screens, actions
  and settings are filtered locally: every word of the query must appear in the title or subtitle.
- Up and Down move (wrapping), Enter runs the selected row, Esc closes. The result rows show the icon, title, subtitle and the action's shortcut as
  key caps. No match shows "No results for "xyz"" (a polite live region).
- Running a row closes the palette, remembers it as recent, then runs it. Each row's accessible name is "Title, subtitle, shortcut".

### 6.5 Keyboard shortcuts

Bound on `ShellView` (so active everywhere in the main window) unless noted.

| Shortcut | Action |
|---|---|
| Ctrl+K | Open the command palette |
| Ctrl+F | Focus the current page's search box (`ViewModelBase.RequestFocusSearch`; the page view must handle `FocusSearchRequested`) |
| Ctrl+N | Run the current page's new-record action (`ViewModelBase.NewCommand`; a page with no `NewCommand` ignores it) |
| Ctrl+B | Collapse or expand the sidebar |
| Ctrl+D | Record donation: opens the next pending appointment of today and starts recording; if nobody is booked it opens today's appointments and says so |
| Alt+1 to Alt+8 | Open the nth visible sidebar destination (`NavigateToIndexCommand`) |
| F6, Shift+F6 | Cycle focus between the title-bar search, the sidebar and the page (not while the palette is open) |
| Esc | Close the palette, the open dialog (`ModalOverlay`) or the side panel |
| Alt+Space | System menu of the window |

Not built: Ctrl+S (save) and Ctrl+/ (shortcut sheet), and F6 does not visit the side panel. Show the shortcut in the tooltip and
`AutomationProperties.AcceleratorKey` of every action that has one.

### 6.6 Taskbar

`MainWindow` drives `TaskbarItemInfo` from `ShellViewModel`:

- **Overlay badge:** a 16 px red disc (`Color.CloseHover`) with the number of blood groups below their minimum in white (`9+` above nine), drawn by
  `MainWindow.CreateBadge`; none when the count is zero. The item description reads "{n} blood groups below their minimum". It is rebuilt on
  `ThemeService.Changed`. (The number is drawn in the app's font, `Font.Family`.)
- **Progress:** `Indeterminate` while a call waits on a waking server (`IsServerWaking`), otherwise none. An error state of the progress is not built.
- **Thumbnail buttons:** "Record donation" (`Drop` icon) and "Book appointment" (`Calplus`), white outline icons, running `RecordDonationCommand` and
  `BookAppointmentCommand` without dismissing the thumbnail.
- **Window title:** "Lifestream, Donors" (`ShellViewModel.WindowTitle`, from the page's `PageTitle`), so the thumbnail and Alt+Tab say which screen is
  open. Give every new screen a meaningful `PageTitle`.
- Closing with unsaved changes asks "Discard changes?" and stays open on Cancel (SHELL-24).

## 7. How to build a screen

The finished direction A screens are the dashboard and sign-in. The six list screens (Donors, Patients, Appointments, Requests, Stock, Receipts) and Staff
and Profile are still 2.0 layouts that follow the new theme through the alias names. This section is the recipe for rebuilding one.

### 7.1 Steps

1. **Page root.** A `UserControl` with `controls:ShellPage.OwnsHeader="True"` and `Focusable="False"`. The shell then hides its plain title and supplies
   the page padding (`PageHost` margin 32,24,32,24; 22,18,16,16 in a Compact window). Do not add outer page padding yourself.
2. **Namespaces.** `xmlns:controls="clr-namespace:Lifestream.Desktop.Controls"`; for view models `xmlns:shell="clr-namespace:Lifestream.Desktop.Shell"`.
3. **Header.** One `controls:PageHeader`: `Title`, `Count`, the one `Button.Primary` in `Actions`, and view tabs (`Tab.Option` in a `Tabs.Strip`) in
   `Tabs` only where the screen has saved views (Appointments: Today, Upcoming, Past, Cancelled; Requests: Open, Due in 48 h, Fulfilled, Cancelled;
   Stock: All, Low, Expiring).
4. **Command bar.** One row: search (`TextBox.Search` with `SearchAssist.Shortcut="Ctrl F"`), filter chips (`Button.Chip`), and a `Button.Subtle`
   "Columns" at the right. 20 DIP gap to the panel below.
5. **List panel.** A `controls:Section` or a bordered panel holding `LifestreamDataGrid` and the paging footer, wrapped in `LoadSurface` for loading,
   empty and error states.
6. **Detail.** A `SidePanel` on the right for the selected record. Open on selection, Esc closes, the list keeps keyboard selection.
7. **Responsive.** React to `controls:Responsive.Breakpoint` (inherited from the shell; read it with a `DataTrigger`).
8. **Wire the shell.** Handle `FocusSearchRequested` (Ctrl+F) in code-behind, expose `NewCommand` on the view model (Ctrl+N), set `PageTitle`.
9. **Accessibility pass** (7.10) and **render tests** (section 8).

### 7.2 List screen skeleton

The view model is a `PagedListViewModel<TItem>` (`Items`, `SelectedItem`, `SearchText`, `Total`, `RangeText`, `PageCountText`, `PreviousPageCommand`,
`NextPageCommand`, `SortCommand`, `SortColumn`, `SortDescending`, `State`, `ErrorMessage`, `ErrorDetail`, `RetryCommand`, `ClearFiltersCommand`,
`EmptyTitle`, `EmptyMessage`, `EmptyActionText`, `EmptyActionCommand`).

```xml
<UserControl x:Class="Lifestream.Desktop.Features.Donors.DonorsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="clr-namespace:Lifestream.Desktop.Controls"
             xmlns:lists="clr-namespace:Lifestream.Desktop.Features.ListScreens"
             controls:ShellPage.OwnsHeader="True" Focusable="False">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="*" />
      <ColumnDefinition Width="Auto" />   <!-- the SidePanel: collapsed while closed, so it takes no room -->
    </Grid.ColumnDefinitions>

    <Grid Grid.Column="0">
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />   <!-- page header -->
        <RowDefinition Height="Auto" />   <!-- command bar -->
        <RowDefinition Height="*" />      <!-- list panel -->
      </Grid.RowDefinitions>

      <controls:PageHeader Title="Donors" Count="{Binding Total}" Margin="{DynamicResource Inset.B16}">
        <controls:PageHeader.Actions>
          <Button Style="{DynamicResource Button.Primary}" Command="{Binding NewCommand}"
                  AutomationProperties.AcceleratorKey="Ctrl+N" ToolTip="Add donor (Ctrl+N)">
            <StackPanel Orientation="Horizontal">
              <controls:Icon Kind="Plus" Size="16" Margin="{DynamicResource Inset.R8}" />
              <TextBlock Text="Add donor" />
            </StackPanel>
          </Button>
        </controls:PageHeader.Actions>
      </controls:PageHeader>

      <!-- Command bar: search at the left, chips, then Columns at the right -->
      <Grid Grid.Row="1" Margin="{DynamicResource Inset.B16}">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto" />
          <ColumnDefinition Width="*" />
          <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <TextBox x:Name="SearchBox" Style="{DynamicResource TextBox.Search}" Width="320"
                 Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
                 controls:InputAssist.Placeholder="Search donors" controls:SearchAssist.Shortcut="Ctrl F"
                 AutomationProperties.Name="Search donors" AutomationProperties.AcceleratorKey="Ctrl+F" />
        <StackPanel x:Name="FilterChips" Grid.Column="1" Orientation="Horizontal" Margin="{DynamicResource Inset.L12}">
          <Button Style="{DynamicResource Button.Chip}" Margin="{DynamicResource Inset.R8}" Command="{Binding OpenGroupFilterCommand}"
                  AutomationProperties.Name="Filter by blood group">
            <StackPanel Orientation="Horizontal">
              <controls:Icon Kind="Plus" Size="14" Margin="{DynamicResource Inset.R6}" />
              <TextBlock Text="Blood group" />
            </StackPanel>
          </Button>
        </StackPanel>
        <Button Grid.Column="2" Style="{DynamicResource Button.Subtle}" Command="{Binding OpenColumnsCommand}"
                AutomationProperties.Name="Choose columns">
          <StackPanel Orientation="Horizontal">
            <controls:Icon Kind="Columns" Size="16" Margin="{DynamicResource Inset.R8}" />
            <TextBlock Text="Columns" />
          </StackPanel>
        </Button>
      </Grid>

      <!-- List panel: a Section with Padding 0 so the table touches the panel edges -->
      <controls:Section Grid.Row="2" Header="All donors" Subtitle="{Binding RangeText}" Padding="0">
        <controls:LoadSurface State="{Binding State}" SkeletonKind="List" SkeletonColumns="2*,90,*,120,100"
                              ErrorMessage="{Binding ErrorMessage}" ErrorDetail="{Binding ErrorDetail}" RetryCommand="{Binding RetryCommand}"
                              EmptyIcon="Donor" EmptyTitle="{Binding EmptyTitle}" EmptyMessage="{Binding EmptyMessage}"
                              EmptyActionText="{Binding EmptyActionText}" EmptyActionCommand="{Binding EmptyActionCommand}">
          <Grid>
            <Grid.RowDefinitions>
              <RowDefinition Height="*" />
              <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <controls:LifestreamDataGrid ItemsSource="{Binding Items}" SelectedItem="{Binding SelectedItem, Mode=TwoWay}"
                                         SortCommand="{Binding SortCommand}" SortColumn="{Binding SortColumn, Mode=OneWay}"
                                         SortDescending="{Binding SortDescending, Mode=OneWay}" AutomationProperties.Name="Donors">
              <DataGrid.Columns>
                <!-- Name and id (two lines), group tag, a text column, a numeric column, as shown in 5.14 -->
              </DataGrid.Columns>
            </controls:LifestreamDataGrid>
            <Border Grid.Row="1" BorderBrush="{DynamicResource Brush.Stroke}" BorderThickness="0,1,0,0" Padding="{DynamicResource Inset.H16V12}">
              <lists:ListFooter />   <!-- "Showing 1-50 of 1,230", Page n of m, Previous, Next -->
            </Border>
          </Grid>
        </controls:LoadSurface>
      </controls:Section>
    </Grid>

    <!-- Detail: one panel, to the right of the list -->
    <controls:SidePanel Grid.Column="1" IsOpen="{Binding IsDetailOpen}" Overline="{Binding Detail.Code}"
                        CloseCommand="{Binding CloseDetailCommand}" Margin="{DynamicResource Inset.L24}">
      <!-- header, actions, key/value grid, history: see 7.6 -->
    </controls:SidePanel>
  </Grid>
</UserControl>
```

Notes on the skeleton:

- `Section.Padding="0"` removes the default body padding. A header row is needed for the panel's rounded top corners: `Section` clips only the bottom
  corners of its content area, so give a list panel a `Header` (as above) so the grid's square header row sits under it. A render check of this
  combination has not been done yet.
- `ListFooter` is the existing 2.0 paging footer (`Features/ListScreens/ListFooter`; data context is the list view model). It still uses 2.0 brush names
  and Segoe glyph icons; restyle it to `Button.Secondary.Small` with `controls:Icon` (`Chevl`, `Chevr`) when you rebuild the first list.
- The gap between list and panel should be 20 DIP, but no `Inset.L20` token exists; the skeleton uses `Inset.L24`. Add `Inset.L20` to `Tokens.xaml`
  rather than writing the literal in the view.
- Where the panel sits relative to the window edge is not settled in code: the shell's `PageHost` margin (32 DIP) keeps a side panel inside the page area.
  The first rebuilt list decides whether it uses a negative margin to reach the edge; record the choice here.

```csharp
// Code-behind: Ctrl+F focuses the search box (the shell calls RequestFocusSearch on the page's view model)
public DonorsView()
{
    InitializeComponent();
    DataContextChanged += (_, e) =>
    {
        if (e.OldValue is ViewModelBase old) { old.FocusSearchRequested -= OnFocusSearch; }
        if (e.NewValue is ViewModelBase now) { now.FocusSearchRequested += OnFocusSearch; }
    };
}

private void OnFocusSearch(object? sender, EventArgs e) { SearchBox.Focus(); SearchBox.SelectAll(); }
```

### 7.3 Rows, density and column priority

- **Rows are 44 DIP** (`Height.Row`), the header 38. Two-line name cells (14 Medium name over a 12 `Ink3` id) fit in 44.
- **Density.** The design has Comfortable (rows 44, controls 34) and Compact (rows 34, controls 28). `UiPreferences.Density` is stored, and the tokens
  `Height.RowCompact` (34) and `Height.Compact` (28) exist, but **no style reads the preference yet**: the grid's `MinRowHeight` is fixed at
  `Height.Row`. Do not hard-code 34; wait for the settings work to wire it, and build rows so they do not depend on a fixed height (`MinHeight`, not
  `Height`).
- **Column widths.** The name column is `Width="Auto"` with a `MinWidth` and `MaxWidth="320"`: it takes what its names need. Detail columns are
  `Width="*"` with a `MinWidth` and no cap: they share what is left, so the table is always full and a name is not cut while another column has room.
  Fixed columns (a blood group tag, a count, a date) have a pixel width.
- **Column priority** is `lists:ListColumns.HideBelow="700"` on a column with `lists:ListColumns.IsResponsive="True"` on the grid: the column folds
  away while the table is narrower than that. On top of the thresholds, when the columns on show still need more than the table has (long names in a
  narrow table), the lowest-priority column on show (the highest `HideBelow`) folds away too, and when no detail column is left the name column takes
  the spare width (`ListColumns.Fit`). A column goes before a name is cut. Order columns by importance (name, group, status, then detail columns)
  and give every detail column a threshold. (`controls:ColumnPriority` from the plan was not built; this is what there is.)
- **Wide tables** may reveal extra columns at 1600 DIP and above; reading layouts keep their max width.
- **Max content widths:** dashboard 1600 (`MaxWidth.Dashboard`), forms 720 (`MaxWidth.Form`), settings and profile 960 (`MaxWidth.Settings`). List
  screens fill the width.

### 7.4 Filters and the command bar

- Chips are `Button.Chip` (dashed edge, 34 high) opening a popup checklist or range. The built-in style is the "unset" look ("+ Blood group"); the
  "set" look ("Blood group: O−, O+", filled) is not built, so until it is, show the chosen values as the chip text and keep the dashed edge.
- When filters are active, a `Button.Subtle` "Clear filters" appears (`ClearFiltersCommand`); the empty state offers the same action.
- `lists:ListToolbar` wraps: while the chips fit beside the search field they stay there; when they do not (a compact window with the side panel open)
  the whole chip row moves under the field and has the list's full width. Give it your chips in `Filters` (one chip, or a panel of chips): it moves
  them into its own row, so each wraps on its own. No chip may run off the list's edge.
- In a Compact window the chips fold into one **Filters** button (`Filter` icon) that opens the same choices in a popup: planned, not built. Build it
  with a `DataTrigger` on `Responsive.Breakpoint` that collapses `FilterChips` and shows the button.
- Search applies after a 300 ms pause (`ListDefaults.SearchDebounce`) or at once on Enter (`SearchNowCommand`).

### 7.5 States: loading, empty, error

Always go through `LoadSurface`; never hand-roll a spinner or a "no data" text.

| State | What the user sees | Copy rule |
|---|---|---|
| Loading | `Skeleton` shaped like the rows, in place (use `SkeletonColumns` equal to the grid columns) | No text |
| Waking | Skeleton plus the "Waking up the server, this can take about a minute and a half" banner with Retry and Cancel (from the third second of a slow call) | Fixed text |
| Empty, no records | `EmptyState` | "No donors yet." plus an action ("Add donor") |
| Empty, filtered | `EmptyState` | "No donors match these filters." plus "Clear filters" |
| Error | `ErrorState` with Retry and Details | Plain cause and what to do ("Can't reach the server. Check your connection, then retry."); the exception text only behind Details |

Copy rules for states: one line naming the cause in context, then a next action as a button. No apologies ("Sorry", "Oops"), no blame, no exclamation
marks. Do not say "Nothing here yet" when you know why it is empty.

### 7.6 The detail side panel

Standard layout of the content of a `SidePanel` (420 DIP, 352 in Compact), top to bottom:

1. **Header block:** `Avatar` (40, `IsBlood="True"` for donors), the name (`Text.Section`, 20/28 SemiBold), a `BloodGroupTag` (`IsLarge`), and a subtitle
   (`Text.Secondary`: id, phone, age). Set `SidePanel.Overline` to the kind and id ("Donor D-0175"). Put the "..." menu button in
   `SidePanel.HeaderActions`.
2. **Actions:** one row, the same on every list screen: the one filled `Button.Primary` for the main thing you do with this record (Donor: "Book
   appointment"; Patient: "New request"; Request: "Fulfil request"; Staff: "Edit"; Receipt: "Print"), which fills the row; then a `Button.Secondary`
   ("Edit", or the screen's second action); then a `Button.Icon` with the `More` icon (name "More actions") opening a `ContextMenu` with the destructive
   and rare actions under their full labels ("Delete staff member", "Cancel appointment"). A screen with nothing to edit or delete (Stock) has no menu.
   Only one filled button per screen: while the panel is open the page header's action is `Button.Secondary` (`lists:HeaderAction.IsDemoted`).
   A destructive action asks first in the confirmation dialog (`IDialogService.ConfirmAsync`; `Controls/DialogWindow`, on the `DialogSurface` like the
   form dialogs): the body names the record, Cancel has the focus, and the danger button carries the full action. There is no inline confirm control.
   Record codes ("D-0175", "A-0905", "B-0412") come from `RecordCodes` (Client/Features/Receipts): never put a code together in a view or a view model.
3. **Key/value grid:** a two-column `Grid` of `Text.Label` (key, `Ink2`) and `Text.Body` (value), 8 DIP between rows. Missing values show an en dash,
   never a blank.
4. **History:** a `Section`-like list (donation number, date, where each bag went), newest first, each row a name line and a `Text.Caption` line, with a
   `StatusChip` where there is a status. A "View all" `Button.Subtle.Small` if it is long.

```xml
<controls:SidePanel IsOpen="{Binding IsDetailOpen}" Overline="{Binding Detail.Overline}" CloseCommand="{Binding CloseDetailCommand}">
  <controls:SidePanel.HeaderActions>
    <Button Style="{DynamicResource Button.Icon.Small}" AutomationProperties.Name="More actions" ToolTip="More actions"
            Command="{Binding OpenMoreCommand}">
      <controls:Icon Kind="More" Size="16" />
    </Button>
  </controls:SidePanel.HeaderActions>
  <ScrollViewer VerticalScrollBarVisibility="Auto" Focusable="False">
    <StackPanel>
      <Grid Margin="{DynamicResource Inset.B16}">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto" />
          <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>
        <controls:Avatar Initials="{Binding Detail.Initials}" Size="40" IsBlood="True" Margin="{DynamicResource Inset.R12}" />
        <StackPanel Grid.Column="1">
          <StackPanel Orientation="Horizontal">
            <TextBlock Style="{DynamicResource Text.Section}" Text="{Binding Detail.Name}" TextWrapping="NoWrap" TextTrimming="CharacterEllipsis" />
            <controls:BloodGroupTag Text="{Binding Detail.Group}" IsLarge="True" Margin="{DynamicResource Inset.L8}" VerticalAlignment="Center" />
          </StackPanel>
          <TextBlock Style="{DynamicResource Text.Secondary}" Text="{Binding Detail.Subtitle}" />
        </StackPanel>
      </Grid>
      <StackPanel Orientation="Horizontal" Margin="{DynamicResource Inset.B16}">
        <Button Style="{DynamicResource Button.Primary}" Command="{Binding RecordDonationCommand}" Content="Record donation" Margin="{DynamicResource Inset.R8}" />
        <Button Style="{DynamicResource Button.Secondary}" Command="{Binding EditCommand}" Content="Edit" />
      </StackPanel>
      <!-- key/value grid, then history -->
    </StackPanel>
  </ScrollViewer>
</controls:SidePanel>
```

In a Compact window the panel is 352 DIP by itself (the `SidePanel` style has the trigger); do not set `PanelWidth` for it in the view. Edit-in-place (fields switching to
editors with a sticky Save and Discard footer and Ctrl+S) is the planned replacement of the 2.0 modal editors for donors, patients and staff; it is not
built, and Ctrl+S is not bound.

### 7.7 Responsive behaviour

The shell sets the inherited `controls:Responsive.Breakpoint` on its root; every page reads it with a trigger:

```xml
<DataTrigger Binding="{Binding (controls:Responsive.Breakpoint), RelativeSource={RelativeSource Self}}" Value="Compact">
  <Setter Property="Visibility" Value="Collapsed" />
</DataTrigger>
```

In C#, use `Responsive.GetBreakpoint(element)` and a `DependencyPropertyDescriptor` on `Responsive.BreakpointProperty` (as `ShellView` and
`DashboardView` do), and remove the handler on `Unloaded`.

| Breakpoint | Do |
|---|---|
| Compact (below 1280) | The shell shows the rail and a 40 DIP title bar. Side panel 352. Fold filter chips into a Filters button. Hide priority 3 columns (planned). Stack dashboard rows to one column. |
| Standard (1280 to 1599) | The full layout |
| Wide (1600 and up) | Show extra columns on wide tables; keep reading layouts at their max width |

Dashboard: it must fit 1280 x 800 without scrolling in Standard; in Compact the panels stack and the page scrolls.

### 7.8 Forms

Forms are at most 720 DIP wide (`MaxWidth.Form`), a single column of `FormField`s with 16 DIP between them, labels above, hints below, errors with an icon
and a message. The primary button ("Save donor") is at the bottom left or in a sticky footer; Enter submits (`IsDefault="True"`); the first invalid
field takes focus on submit (`FormFocus`). A form with unsaved changes asks "Discard changes?" before closing (`IHasUnsavedChanges`). Settings and
profile pages use `MaxWidth.Settings` (960), centred, with `Section`s.

### 7.9 Copy rules

- **Sentence case** everywhere: headings, buttons, menu items, tabs, column headers. "Record donation", not "Record Donation".
- **Verbs on buttons**, naming the object where it is not obvious: "Add donor", "Save changes", "Delete donor". Never "OK", "Submit" or "Yes".
  Cancel buttons say what they keep ("Keep donor") when the dialog asks about destroying something.
- **Busy text** is the progressive verb: "Saving", "Signing in", "Opening the demo".
- **Toasts** use the same verb as the button: "Record donation" produces "Donation recorded", and name the record ("Donation for Hana Greenleaf recorded").
- **Errors say the cause and the fix**, in plain words: "The password is wrong. Check Caps Lock and try again." Not "Authentication failed (401)".
  Technical detail goes behind Details.
- **No apologies** ("Sorry", "Oops", "Unfortunately"), no exclamation marks, no blame ("You entered").
- **Empty states** give a one-line cause in context and a next action.
- **Numbers and dates:** dates as `dd MMM yyyy` ("05 Mar 2022"); relative dates for due dates ("in 2 days"); counts as digits; units after a space
  ("17 units"). Blood groups with a real minus (U+2212).
- **Status words** are the same everywhere: Pending, Completed, Cancelled, Incomplete, Low stock, Expiring.

### 7.10 Accessibility checklist

- [ ] Every icon-only button has `AutomationProperties.Name` and a tooltip (with its shortcut where it has one).
- [ ] Page title is a level 1 heading (`PageHeader` does this); section headings are level 2 (`Section` and `DialogSurface` do this). Set
  `AutomationProperties.HeadingLevel` on any hand-made heading.
- [ ] Toasts, banners and form errors are live regions: polite for information (`AutomationProperties.LiveSetting="Polite"`), assertive for errors
  (`FormField.Error`, `ErrorState`, a sign-in problem row).
- [ ] Focus ring visible on every focusable control (2 px `Focus`, 2 px outside; keyboard only). Do not set `FocusVisualStyle` to something else.
- [ ] Tab order follows reading order: header, command bar, list, panel. After opening a panel, the list keeps keyboard selection; Esc closes the
  panel and returns focus to the row. F6 reaches title bar, sidebar and page.
- [ ] Text is at least 4.5:1 on `Layer` and `Canvas`, borders and icons at least 3:1, in both themes. Do not use `Ink3` for text that matters on a
  `Selected` or `Hover` fill without checking; the contrast tests list the pairs.
- [ ] Status has an icon (or dot) and a word. Colour only decorates.
- [ ] A custom control that draws its own content has an automation name (`MeterBar` rows are named by their row: "Stock for O−: 4 units, below the
  minimum").
- [ ] A skeleton is announced once as "Loading"; the load becomes an error state after 10 s.
- [ ] Works at 125 % and 150 % text scaling: no fixed heights on text rows (`MinHeight`, not `Height`), nothing clipped at 1024 x 640.
- [ ] Verified once with a Windows high-contrast theme (3.3).

## 8. Testing

| What | Where |
|---|---|
| Foundation (colours, themes at runtime, every keyed style, the no-effect rule, title bar, sidebar, palette) | `tests/Lifestream.Desktop.Tests/RedesignA/FoundationTests.cs` |
| Render tests of the finished screens (sign-in, dashboard) | `tests/Lifestream.Desktop.Tests/RedesignA/RenderTests.cs`, trait `Category=RenderA` |
| Every screen inside the real shell (sidebar, title bar, side panel open) at 1440 x 900, and the command palette at the three sizes | `RedesignA/ShellRenderTests.cs`, trait `Category=RenderA`; files `shell-{screen}-{theme}-1440x900.png`, `palette-{theme}-{w}x{h}.png` |
| The design review's shared-control rules (one error look and no WPF adorner, avatar fallback, tag colour, tab bar, hover on the canvas, page header count, side panel inset, blood groups kept together) | `RedesignA/DesignReviewTests.cs` |
| The demo clinic they render (Thursday 19 November 2026, 10:42, Hana Greenleaf signed in, served by the fake API) | `tests/Lifestream.Desktop.Tests/RedesignA/DesignData.cs` |
| Host and off-screen rendering helpers | `Hosted.cs`, `StaHost.cs`, `WpfTestUi.cs` in `tests/Lifestream.Desktop.Tests`; `RedesignA/TestTheme.cs` forces the light theme unless a test picks one |
| Token scanners and contrast checks (2.0 era) | `tests/Lifestream.Desktop.Tests/ThemeTests.cs` |

Run the render tests (they draw off-screen; no window is shown):

```powershell
$env:LIFESTREAM_CAPTURE_DIR = "G:\Claude\workspace\lifestream\scratch\captures\<topic>"
dotnet test tests/Lifestream.Desktop.Tests --filter Category=RenderA
```

Each render is saved as a PNG named `<screen>-<theme>-<width>x<height>.png` (for example `signin-dark-1440x900.png`) into `LIFESTREAM_CAPTURE_DIR`.
Without the variable the tests save to the run's capture folder when it exists, else to the test output folder (`Hosted.CaptureDirectory`).
Always set the variable for a working run: without it a plain `dotnet test` writes over the captures a review was made from. The
render sizes are 1440 x 900, 1920 x 1080 (maximized) and 1024 x 700 (Compact), each in light and dark. Compare them with the target images
(`A-login.png`, `A-dashboard.png`, `A-dashboard-dark.png`, `A-donors.png`, `A-donors-max.png`, `A-donors-narrow.png`).

For each screen you rebuild, add a render test that builds the real view model from `DesignData`, hosts the real view at those three sizes in both
themes, saves the PNG, and asserts the facts a broken layout would lose: nothing clipped, the breakpoint, the theme's `Layer` colour present.

**The effect rule.** No element that contains text may carry an `Effect`. `NoElementThatContainsText_CarriesAnEffect_OnSignInDashboardOrComponents`
renders the sign-in screen, the shell with the palette open, and a gallery of `ElevatedSurface`, `DialogSurface`, `SidePanel` and `ContextMenu` in
both themes and fails if any element with an effect has text anywhere in its subtree. Draw shadows on a sibling `Border` behind the content. When you
add a floating component, add it to that gallery.

Other rules enforced by tests:

- Colour literals: every view XAML and every `*.cs` is scanned (`Theme_NoHexLiteralsInViews`).
- Both colour dictionaries define exactly the same keys and every brush resolves in both themes; the direction A colours equal the design tokens; text
  is at least 4.5:1 on `Layer` and `Canvas` in both themes.
- Every keyed control style applies and builds its template in both themes.
- The theme switches at runtime and an open view repaints.
- The title bar's caption buttons are 46 x 44, content extends under it, and the maximized inset is the real frame inset.
- The sidebar collapses with Ctrl+B, is remembered, and is a rail in a Compact window; the palette opens with Ctrl+K, filters, searches records, moves
  with the arrows, runs with Enter and closes with Esc.
- Font files match their hashes and every text style resolves to the bundled family (`FontTests`).

Before opening a PR, run the Desktop tests that your change touches, render the screens in both themes at the three sizes, and read the final diff.
