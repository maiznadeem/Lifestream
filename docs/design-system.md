# Lifestream design system

Source: the UI/UX audit of the old app (sections 4.2 and 4.3), implemented in `src/Lifestream.Desktop/Theme` and
`src/Lifestream.Desktop/Controls`. Dark theme only for 2.0. Every value below is a token in `Theme/Tokens.xaml`,
`Theme/Typography.xaml`; views never contain a colour literal (test `Theme_NoHexLiteralsInViews`) and no spacing, radius or motion
number other than 0 (test `Theme_NoSpacingRadiusOrMotionLiteralsInViews_OnlyTokens`, which scans every `*.xaml` outside `Theme/`, the
feature screens included). Contrast is computed from `Tokens.xaml` by `Tokens_TextPairsMeetContrast`: every text colour on every surface
(4.5:1), every non-text colour on the surfaces it sits on (3:1), and a guard test fails when a colour token is neither checked nor
listed as exempt.

## Colour (dark)

| Token | Value | Checked against | Ratio |
|---|---|---|---|
| surface.canvas | #17121A | text.primary | 16.21 |
| surface.raised (cards, inputs, grid) | #221B24 | text.primary / text.secondary | 14.75 / 8.74 |
| text.primary | #F4EFEF | canvas | 16.21 |
| text.secondary | #C4B8BC | canvas | 9.61 |
| text.muted (placeholders, meta) | #AA9DA2 | raised 6.44; pressed 4.82; selected 5.29 (was #9C8F94: 4.05 on pressed) | 4.82 minimum |
| text.disabled | #7A6E73 | raised | 3.44 (exempt; still readable) |
| primary (idle) | #C8102E | white text 5.88; vs canvas 3.14 (non-text) | |
| primary.hover | #A80D27 | white text | 7.63 |
| primary.pressed | #870A1F | white text | 10.03 |
| focus.ring | #FFB4BF | canvas 11.01; on primary button 3.51 | |
| border.input | #8C7F85 | raised (1.4.11) | 4.39 |
| status.danger | #FF8A8A | raised | 7.40 |
| status.success | #6EDC9A | raised | 9.89 |
| status.warning | #FFC861 | raised | 10.95 |
| status.info | #8EC5FF | raised | 9.26 |
| skeleton.base / highlight | #2E2530 / #3A303D | decorative | 1.14 |
| chart series 1 / 2 (non-text, vs raised) | #FF7A8A / #7CB8FF | | 6.72 / 8.12 |

### Additions to the audit's table (deviation: tokens the components needed that the audit did not list)

| Token | Value | Used for | Checked |
|---|---|---|---|
| surface.hover | #2E2530 | row, menu and ghost-button hover | text.primary 12.95, text.secondary 7.68 |
| surface.pressed | #3A303D | pressed ghost/secondary, tooltip | text.primary 11.04, text.secondary 6.54 |
| surface.selected | #4A1F2B | selected grid row and sidebar item | text.primary 12.12 (needs 4.5) |
| border.subtle | #3A303D | row separators, card borders (decorative) | |
| danger.hover / danger.pressed | #3A1A22 / #4A222C | danger button hover and pressed layers | status.danger 6.85 / 5.95 |
| text.on-primary | #FFFFFF | label on the primary button | 5.88 |
| scrim | #B3000000 | behind dialogs | decorative |
| scroll.track | #221B24 | track of every scroll bar (`Theme/ScrollBars.xaml`, applied to every `ScrollBar`) | |
| scroll.thumb / hover / pressed | #8C7F85 / #B0A3A9 / #C4B8BC | the thin 8 DIP thumb in a 12 DIP bar | 4.39 / 6.92 / 8.74 against the track (needs 3) |

Rules from the audit, kept: no text on gradients or photos; danger red is never used for a non-destructive primary action in the
same view (danger buttons are outlined and carry an icon).

## Typography

One family: **Inter** (SIL Open Font License), bundled in the executable as hinted static TrueType files, `Fonts/Inter-*.ttf` and `Fonts/InterDisplay-*.ttf` (version, source and SHA-256 in
`Fonts/README.md`, licence in `Fonts/OFL.txt`) and named once, in `Theme/Typography.xaml`. Text styles use `Font.Family` (Inter); Display and Title use `Font.Display` (Inter Display). Every text
style and every text-bearing control resolves to it (`FontTests`). Icons use the system icon font (Segoe Fluent Icons, Segoe MDL2 Assets
as fallback), through `IconText`, which is hidden from UI Automation; icons are not text. The charts draw with SkiaSharp, not WPF text, so
`Theme/ChartFont` loads the same embedded file into an `SKTypeface` and makes it the LiveCharts default; `ChartFontTests` renders the dashboard
and checks the glyphs are Inter's, not Segoe UI's.

| Token | Size / line height (DIP) | Weight | Use |
|---|---|---|---|
| display | 32 / 40 | SemiBold | KPI numbers (tabular figures) |
| title | 24 / 32 | SemiBold | Page titles |
| heading | 18 / 24 | SemiBold | Card and section headings |
| body | 14 / 20 | Regular | Inputs, grid cells, body |
| label | 13 / 18 | Medium | Field labels, buttons, column headers |
| caption | 12 / 16 | Regular | Hints, meta, chart axes (minimum size) |

## Spacing, shape, sizes

- 4-DIP base: 4, 8, 12, 16, 24, 32, 48 (`Space.*` numbers, `Pad.*` all-sides thicknesses). Field gap 16 (`Gap.Field`), section gap 32, page
  padding 24. A margin or padding that is not the same on every side is an `Inset.*` token whose name says the sides: `H` is left and
  right, `V` is top and bottom, `L`, `T`, `R`, `B` one side each (`Inset.T16` is 16 above, `Inset.H12V8` is 12 across and 8 down). The
  feature screens use the `Edge` helper, which composes `Space.*` tokens one side at a time. No view writes a spacing number. C# that builds elements itself reads the same tokens through `Theme/ThemeTokens` (`Space`, `Inset`,
  `Radius`), which throws when a token is missing instead of falling back to a number; `Theme_NoSpacingOrRadiusLiteralsInCSharp_OnlyTokens`
  scans every `*.cs` under the Desktop project for `Thickness`/`CornerRadius`/`Margin`/`Padding` literals other than 0. Exempt, with the
  reason in the test: the printed receipt layout and the LiveCharts bar gap.
- Control height 32 (compact) or 40 (default); grid rows 40 (design range 36 to 40).
- Radius 8 for inputs and buttons (`Radius.Control`), 12 for cards and dialogs (`Radius.Card`) and chips (`Radius.Pill`), 12 for the
  keyboard focus ring that sits 4 DIP outside a control (`Radius.FocusRing`); 2, 6 and 14 (`Radius.2`, `Radius.6`, `Radius.14`) and the
  left-side-only 12 (`Radius.Left8`) for the few shapes that need them. Cards and popups carry a soft shadow (`controls:Card`, drawn in the
  card's own visual so text keeps ClearType), and every window draws small text with `TextFormattingMode=Display` (headings stay `Ideal`).
  Every bordered element is a `controls:CrispBorder`: its edge is a whole number of device pixels at 100 to 175 % scaling (a plain 1 DIP
  stroke is 1.25 to 1.75 px there and looks soft), and nothing at rest carries a render transform.
- Caption buttons 46 x 32. Sidebar 232 wide.
- One shadow token for dialogs and drawers only; cards are separated by surface colour.

## Motion

100 to 150 ms (120 ms) for hover and press, 150 to 200 ms (180 ms) for drawers and dialogs, skeleton shimmer 1.2 s, spinner 0.9 s
a turn. The numbers are tokens in `Theme/Tokens.xaml` (`Motion.Fast.Milliseconds`, `Motion.Standard.Milliseconds`,
`Motion.Shimmer.Milliseconds`, `Motion.SpinnerTurn.Milliseconds`). When Windows "Animation effects" is off every duration is zero and the
shimmer and spinner are static.

Because storyboard and visual-transition durations cannot be dynamic resources, `Theme/Motion.xaml` turns the millisecond tokens into
`Duration` resources through `x:Static` members of `Theme/MotionSettings.cs`; that class holds no number of its own, it only reads the
tokens and applies the Windows setting. The setting is read when the theme loads, so changing it needs a restart.

## Components

| Component | Where | Behaviour |
|---|---|---|
| Button (`Button.Primary`, `Secondary`, `Ghost`, `Danger`, `Icon`) | `Controls/Buttons.xaml`, `ButtonAssist` | States idle, hover (layer one step darker), pressed (two steps), keyboard focus (2 px ring, offset 2 px, shown only after keyboard input), busy (spinner and `ButtonAssist.BusyText` replace the label, same width, clicks ignored, UI Automation `ItemStatus` = Busy), disabled. Primary 40 DIP high, others 32 to 40. Busy wins over disabled so a running command does not look dead. |
| Text input, combo box, check box | `Controls/Inputs.xaml`, `FormField` | Label above (never inside), hint, inline error with icon below, announced through a live region, focus moves to the first invalid field. |
| Password field | `Controls/PasswordField` | The only masked input; optional Show toggle; its `Password` always mirrors the box in both directions. |
| Record picker | `Controls/RecordPicker` | Searchable async combo for donor, patient, staff, appointment, request; 300 ms debounce, skeleton rows while searching, "No matches", summary chip with Change after choosing. |
| Data grid | `Controls/LifestreamDataGrid`, `DataGridStyles.xaml` | Virtualised and recycling, 40 DIP rows, 14 DIP text, row separators only, selection surface keeps 4.5:1, sort indicators from view-model state (server-side sort), content sized columns with a 64 DIP minimum, tooltip only for truncated cells. |
| Skeleton | `Controls/Skeleton`, `LoadSurface` | Kinds List, Detail, Dashboard, StockTiles, Picker, Profile; List takes the same column spec as its grid. Shown in the same cell as the content, so it is replaced in place; shimmer 1.2 s, static under reduced motion; the view model turns a load that is still running after 10 s into an error state. |
| Empty state, error state | `Controls/Surfaces` | Plain-language cause plus an action (Add, Clear filters, Retry); technical detail behind a Details control with Copy. |
| Status chip | `Controls/StatusChip` | Pending, Completed, Cancelled, Incomplete, Low stock, Expiring: icon, text and token colour. |
| Toast | `Controls/ToastView`, `ToastHost` | Names the record, 8 s (10 s when it carries an Undo action; a caller may ask for 6 to 10 s), pauses on hover or focus, optional View or Undo action, polite live region. |
| Scroll bar | `Theme/ScrollBars.xaml` | One style for every `ScrollBar` (so every scroll viewer, list and grid): dark track, 8 DIP rounded thumb, hover and pressed states, page-up and page-down from the empty track. |
| Login window | `Auth/LoginView` | Starts empty; inline messages with Retry; the "Waking up the server, this can take up to a minute" banner with Cancel from the third second of a slow call; the "update ready" prompt with Restart and update. |
| Confirmation dialog | `Controls/DialogWindow` | Title and body name the record, focus starts on Cancel, Esc cancels, the danger button says verb and object ("Delete donor"). |
| Window chrome | `Chrome/ChromeWindow` | One chrome for every window: 6 DIP resize border, Snap Layouts (the maximize button answers HTMAXBUTTON), double-click maximize, restore glyph, Alt+Space, per-monitor maximize, 46 x 32 caption buttons whose hit area is their highlight. |
| Account menu | `Shell/ShellView` | Avatar, name and chevron, 40 x 40 or larger, accessible name "Account menu", opens Profile, Change password, Sign out. |

## Deviations from the audit, in one place

1. Extra tokens listed above.
2. Motion durations reach XAML as `Duration` resources made from the millisecond tokens (reason above).
3. The window chrome is our own (`ChromeWindow`) instead of WPF-UI's `FluentWindow`, because the caption buttons must be exactly 46 x 32
   with a click area equal to the highlight and the Snap Layouts hit test must be ours. The WPF-UI package is still referenced; the stack spike that uses it lives in the test project, not in the app.
4. The date input is `DateField` (text in `dd MMM yyyy` plus a calendar); the built-in `DatePicker` cannot be given that format.
5. There is no "Forgot password" control: the API has no endpoint that could make it work, and the requirement allows it only if it works.
