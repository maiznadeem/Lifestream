# Lifestream design system

Source: the UI/UX audit of the old app (sections 4.2 and 4.3), implemented in `src/Lifestream.Desktop/Theme` and
`src/Lifestream.Desktop/Controls`. Dark theme only for 2.0. Every value below is a token in `Theme/Tokens.xaml`,
`Theme/Typography.xaml` or `Theme/MotionSettings.cs`; views never contain a colour literal (test `Theme_NoHexLiteralsInViews`),
and every text and non-text pair is checked by `Tokens_TextPairsMeetContrast`.

## Colour (dark)

| Token | Value | Checked against | Ratio |
|---|---|---|---|
| surface.canvas | #17121A | text.primary | 16.21 |
| surface.raised (cards, inputs, grid) | #221B24 | text.primary / text.secondary | 14.75 / 8.74 |
| text.primary | #F4EFEF | canvas | 16.21 |
| text.secondary | #C4B8BC | canvas | 9.61 |
| text.muted (placeholders, meta) | #9C8F94 | raised | 5.42 |
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

Rules from the audit, kept: no text on gradients or photos; danger red is never used for a non-destructive primary action in the
same view (danger buttons are outlined and carry an icon).

## Typography

One family: **Segoe UI Variable** (Windows 11; Segoe UI is the Windows 10 fallback). Icons use the system icon font (Segoe Fluent
Icons, Segoe MDL2 Assets as fallback), through `IconText`, which is hidden from UI Automation.

| Token | Size / line height (DIP) | Weight | Use |
|---|---|---|---|
| display | 32 / 40 | SemiBold | KPI numbers (tabular figures) |
| title | 24 / 32 | SemiBold | Page titles |
| heading | 18 / 24 | SemiBold | Card and section headings |
| body | 14 / 20 | Regular | Inputs, grid cells, body |
| label | 13 / 18 | Medium | Field labels, buttons, column headers |
| caption | 12 / 16 | Regular | Hints, meta, chart axes (minimum size) |

## Spacing, shape, sizes

- 4-DIP base: 4, 8, 12, 16, 24, 32, 48 (`Space.*`, `Pad.*`). Field gap 16, section gap 32, page padding 24.
- Control height 32 (compact) or 40 (default); grid rows 40 (design range 36 to 40).
- Radius 4 for inputs and buttons, 8 for cards and dialogs, 12 for chips.
- Caption buttons 46 x 32. Sidebar 232 wide.
- One shadow token for dialogs and drawers only; cards are separated by surface colour.

## Motion

100 to 150 ms (120 ms) for hover and press, 150 to 200 ms (180 ms) for drawers and dialogs, skeleton shimmer 1.2 s, spinner 0.9 s
a turn. When Windows "Animation effects" is off every duration is zero and the shimmer and spinner are static.

Deviation: the motion values live in `Theme/MotionSettings.cs` and are exposed to XAML by `Theme/Motion.xaml` through `x:Static`,
not as literal durations in `Tokens.xaml`, because storyboard and visual-transition durations cannot be dynamic resources and
must be zeroed when the Windows setting is off. The setting is read when the theme loads, so changing it needs a restart.

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
| Toast | `Controls/ToastView`, `ToastHost` | Names the record, 8 s, pauses on hover or focus, optional View or Undo action, polite live region. |
| Confirmation dialog | `Controls/DialogWindow` | Title and body name the record, focus starts on Cancel, Esc cancels, the danger button says verb and object ("Delete donor"). |
| Window chrome | `Chrome/ChromeWindow` | One chrome for every window: 6 DIP resize border, Snap Layouts (the maximize button answers HTMAXBUTTON), double-click maximize, restore glyph, Alt+Space, per-monitor maximize, 46 x 32 caption buttons whose hit area is their highlight. |
| Account menu | `Shell/ShellView` | Avatar, name and chevron, 40 x 40 or larger, accessible name "Account menu", opens Profile, Change password, Sign out. |

## Deviations from the audit, in one place

1. Extra tokens listed above.
2. Motion values in code rather than `Tokens.xaml` (reason above).
3. The window chrome is our own (`ChromeWindow`) instead of WPF-UI's `FluentWindow`, because the caption buttons must be exactly 46 x 32
   with a click area equal to the highlight and the Snap Layouts hit test must be ours. WPF-UI is still referenced by the stack spike.
4. The date input is `DateField` (text in `dd MMM yyyy` plus a calendar); the built-in `DatePicker` cannot be given that format.
5. There is no "Forgot password" control: the API has no endpoint that could make it work, and the requirement allows it only if it works.
