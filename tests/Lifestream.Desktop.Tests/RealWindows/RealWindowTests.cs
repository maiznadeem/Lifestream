using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Dialogs;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Desktop.Tests.RedesignA;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RealWindows;

/// <summary>
/// What only a real window shows. Every other render of this project draws elements without a window, so it cannot see what Windows itself draws
/// in the title bar (2.1.0 shipped with Windows' own caption buttons showing next to ours), the frame Windows gives a window, the inset of a
/// maximized window, or where a click lands. These tests show the sign-in window, the main window and every dialog as real windows outside every
/// monitor (never activated, no taskbar button, never maximized), capture them with PrintWindow and ask them what Windows asks. They need an
/// interactive desktop with the compositor running, so they skip on a GitHub runner, in a service session and while the session is locked
/// (<see cref="RealWindow.Unavailable"/>). Captures are saved to <c>real-window/</c> in the capture directory.
/// </summary>
[Trait("Category", "RealWindow")]
[Collection("Wpf")]
public sealed class RealWindowTests
{
    private const long Caption = 0x00C00000;
    private const long SystemMenu = 0x00080000;
    private const long ThickFrame = 0x00040000;
    private const long MinimizeBox = 0x00020000;
    private const long MaximizeBox = 0x00010000;
    private const int CornerDoNotRound = 1;
    private const int CornerRound = 2;
    private const int BackdropMica = 2;

    private static string Folder => Path.Combine(Hosted.CaptureDirectory, "real-window");

    public static TheoryData<string, string> MainWindows => new()
    {
        { "signin", "light" },
        { "signin", "dark" },
        { "main", "light" },
        { "main", "dark" },
    };

    public static TheoryData<string, string> Dialogs
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var kind in new[] { "change-password", "reset-password", "receipt", "confirm", "error" })
            {
                data.Add(kind, "light");
                data.Add(kind, "dark");
            }

            return data;
        }
    }

    /// <summary>
    /// The safety of every test here: a real window cannot be activated. Asked to become its thread's active window (what a click, UI Automation
    /// or the window's own code can ask), it is refused by the guard, it is not active, no window of this process is the foreground window,
    /// and it stays off every monitor in the normal state.
    /// </summary>
    [Fact]
    public void ARealWindow_CannotBeActivated_AndStaysOffEveryMonitor()
    {
        Assert.SkipWhen(RealWindow.Unavailable is not null, RealWindow.Unavailable ?? string.Empty);
        var facts = StaHost.Run(() =>
        {
            using var scene = Scene.Open("signin", "light");
            var real = scene.Real;
            var before = real.ActivationsRefused;
            var becameActive = real.AskToBeActive();
            real.Settle(100);
            var bounds = real.Bounds;
            var screen = RealWindow.VirtualScreen;
            return (becameActive, Refused: real.ActivationsRefused - before, scene.Window.IsActive, Foreground: RealWindow.HasTheForeground, scene.Window.WindowState,
                Apart: bounds.X + bounds.Width < screen.X - 1000 && bounds.Y + bounds.Height < screen.Y - 1000, scene.Window.ShowInTaskbar);
        });

        Assert.False(facts.becameActive, "the window became active when asked");
        Assert.True(facts.Refused >= 1, "the guard did not see the activation");
        Assert.False(facts.IsActive);
        Assert.False(facts.Foreground, "a window of the test process is the foreground window");
        Assert.Equal(WindowState.Normal, facts.WindowState);
        Assert.True(facts.Apart, "the window is not well away from every monitor");
        Assert.False(facts.ShowInTaskbar);
    }

    /// <summary>
    /// The title bar shows exactly one set of buttons, ours: nothing is drawn in the caption-button area that our own buttons do not draw (a
    /// second set of glyphs fails here), ours are all there and crisp, and none of what the fix must not cost is lost: the window keeps its
    /// caption, system menu, sizing frame and minimize and maximize boxes, Mica is still behind the title bar, and the frame keeps its round corners.
    /// </summary>
    [Theory]
    [MemberData(nameof(MainWindows))]
    public void TitleBar_ShowsOneSetOfButtons_Ours(string which, string theme)
    {
        Assert.SkipWhen(RealWindow.Unavailable is not null, RealWindow.Unavailable ?? string.Empty);
        var facts = StaHost.Run(() =>
        {
            using var scene = Scene.Open(which, theme);
            var capture = scene.Real.Capture();
            capture.Save(Path.Combine(Folder, $"{which}-{theme}.png"));
            var buttons = scene.Part<FrameworkElement>("CaptionButtons");
            var area = scene.Real.BoundsOf(buttons);
            var seen = capture.Crop(area);
            capture.Crop(new Int32Rect(Math.Max(0, capture.Width - 200), 0, Math.Min(200, capture.Width), Math.Min(70, capture.Height))).Save(Path.Combine(Folder, $"{which}-{theme}-buttons.png"), 5);
            var scale = scene.Real.Scale;
            var minimize = scene.Real.BoundsOf(scene.Part<FrameworkElement>("PART_Minimize"));
            return (
                Compared: ButtonArea.Compare(seen, ButtonArea.Coverage(buttons, area.Width, area.Height, scale), scale),
                MinimizeRows: ButtonArea.InkRows(capture.Crop(minimize)),
                Scale: scale,
                scene.Real.Style,
                Menu: scene.Real.SystemMenuItems,
                scene.Window.IsMicaActive,
                Backdrop: scene.Real.DwmAttribute(RealWindow.DwmBackdropType),
                Corner: scene.Real.DwmAttribute(RealWindow.DwmCornerPreference),
                Glass: WindowChrome.GetWindowChrome(scene.Window).GlassFrameThickness);
        });

        var compared = facts.Compared;
        Assert.True(compared.Stray == 0, $"{which}, {theme}: {compared.Stray} pixels are drawn in the title-bar button area outside our own buttons (first at {compared.FirstStray}): a second set of buttons. Background {compared.Background}; our buttons draw {compared.Ours} pixels.");
        Assert.True(compared.Ours >= 30, $"our own buttons drew only {compared.Ours} pixels");
        Assert.True(compared.OursSeen >= compared.Ours * 0.9, $"only {compared.OursSeen} of the {compared.Ours} pixels of our own buttons are in the window");
        Assert.True(facts.MinimizeRows >= 1 && facts.MinimizeRows <= Math.Ceiling(facts.Scale), $"the minimize dash is {facts.MinimizeRows} rows thick at scale {facts.Scale}: not crisp, or drawn twice");

        // What the window keeps: the system menu, the caption (Snap, minimize and maximize animations), the sizing frame, and the boxes Win+arrows need.
        const long needed = Caption | SystemMenu | ThickFrame | MinimizeBox | MaximizeBox;
        Assert.True((facts.Style & needed) == needed, $"window style 0x{facts.Style:X} lost one of the caption, system menu, sizing frame, minimize and maximize styles");
        Assert.True(facts.Menu >= 6, $"the system menu has {facts.Menu} items");

        // The frame is never extended from the top, where Windows draws its own title bar.
        Assert.Equal(0, facts.Glass.Top);
        Assert.Equal(0, facts.Glass.Left);
        Assert.Equal(0, facts.Glass.Right);
        if (Dwm.SupportsMica && !Dwm.MicaDisabled)
        {
            Assert.True(facts.IsMicaActive, "Mica is not active on a Windows that has it");
            Assert.Equal(BackdropMica, facts.Backdrop);
            Assert.True(facts.Glass.Bottom >= 4320, $"the frame is extended only {facts.Glass.Bottom} from the bottom: not under the whole window");
            // Where the frame is not extended, the transparent title bar is black.
            Assert.True(Math.Max(compared.Background.R, Math.Max(compared.Background.G, compared.Background.B)) > 8, $"the title bar is black ({compared.Background}): the frame is not under it, so Mica is lost");
        }

        if (Dwm.DrawsRoundCorners)
        {
            Assert.Equal(CornerRound, facts.Corner);
        }
    }

    /// <summary>
    /// Clicks and drags land where they should, by what the window answers to Windows' own question (WM_NCHITTEST): the maximize button answers
    /// HTMAXBUTTON over its whole rectangle (Snap Layouts), minimize, close, the search field, the bell and the account button are ours over their
    /// whole rectangle, the empty parts of the title bar drag the window (and double-click maximizes there), the page under it does not, the edges
    /// and corners resize, and no control of the window sits where Windows would drag or resize instead of clicking it.
    /// </summary>
    [Fact]
    public void TitleBar_DragsFromItsEmptyParts_AndEveryControlIsClickable()
    {
        Assert.SkipWhen(RealWindow.Unavailable is not null, RealWindow.Unavailable ?? string.Empty);
        var (answers, blocked, checkedControls) = StaHost.Run(() =>
        {
            using var scene = Scene.Open("main", "light");
            scene.Real.Capture(); // drawn and laid out
            Directory.CreateDirectory(Folder);
            var real = scene.Real;
            var scale = real.Scale;
            int Px(double dips) => (int)Math.Round(dips * scale);
            var size = real.Bounds;
            var found = new Dictionary<string, int>();

            void Whole(string label, Int32Rect r)
            {
                // The middle, the middle of each side one pixel in, and the corners a little way in (inside a rounded shape).
                var inset = Px(5);
                var points = new (string Where, int X, int Y)[]
                {
                    ("middle", r.X + (r.Width / 2), r.Y + (r.Height / 2)),
                    ("top", r.X + (r.Width / 2), r.Y + 1),
                    ("bottom", r.X + (r.Width / 2), r.Y + r.Height - 2),
                    ("left", r.X + 1, r.Y + (r.Height / 2)),
                    ("right", r.X + r.Width - 2, r.Y + (r.Height / 2)),
                    ("top left", r.X + inset, r.Y + inset),
                    ("top right", r.X + r.Width - 1 - inset, r.Y + inset),
                    ("bottom left", r.X + inset, r.Y + r.Height - 1 - inset),
                    ("bottom right", r.X + r.Width - 1 - inset, r.Y + r.Height - 1 - inset),
                };
                foreach (var (where, x, y) in points)
                {
                    found[$"{label}, {where}"] = real.HitTest(x, y);
                }
            }

            var search = real.BoundsOf(scene.Named<Button>("SearchField"));
            var bell = real.BoundsOf(scene.Named<Button>("NotificationsButton"));
            var account = real.BoundsOf(scene.Named<Button>("AvatarButton"));
            var minimize = real.BoundsOf(scene.Part<FrameworkElement>("PART_Minimize"));
            var maximize = real.BoundsOf(scene.Part<FrameworkElement>("PART_Maximize"));
            var close = real.BoundsOf(scene.Part<FrameworkElement>("PART_Close"));
            Whole("search", search);
            Whole("bell", bell);
            Whole("account", account);
            Whole("minimize", minimize);
            Whole("close", close);
            Whole("maximize", maximize);
            // The corners of the caption buttons themselves: the click area is the highlight, to the pixel.
            foreach (var (label, r) in new[] { ("minimize", minimize), ("maximize", maximize), ("close", close) })
            {
                found[$"{label}, first pixel"] = real.HitTest(r.X, r.Y);
                found[$"{label}, last pixel"] = real.HitTest(r.X + r.Width - 1, r.Y + r.Height - 1);
            }

            var titleBottom = Px(scene.Window.TitleBarHeight);
            var gap = (search.X + search.Width + bell.X) / 2;
            found["title bar, between search and bell"] = real.HitTest(gap, titleBottom / 2);
            found["title bar, its last row"] = real.HitTest(gap, titleBottom - 1);
            found["title bar, over the wordmark"] = real.HitTest(real.BoundsOf(scene.Named<TextBlock>("Wordmark")).X + 4, titleBottom / 2);
            found["page, first row under the title bar"] = real.HitTest(gap, titleBottom);
            found["page"] = real.HitTest(gap, titleBottom + Px(40));
            found["left edge"] = real.HitTest(2, size.Height / 2);
            found["right edge"] = real.HitTest(size.Width - 3, size.Height / 2);
            found["bottom edge"] = real.HitTest(size.Width / 2, size.Height - 3);
            found["top edge"] = real.HitTest(gap, 2);
            found["top left corner"] = real.HitTest(2, 2);
            found["bottom left corner"] = real.HitTest(2, size.Height - 3);
            found["bottom right corner"] = real.HitTest(size.Width - 3, size.Height - 3);

            // Every control a user can click, anywhere in the window: its middle and its top and bottom rows belong to it.
            var stuck = new List<string>();
            var count = 0;
            foreach (var control in Hosted.Descendants(scene.Window).OfType<FrameworkElement>()
                .Where(e => e is ButtonBase or TextBoxBase or PasswordBox or ComboBox)
                .Where(e => e.IsVisible && e.IsHitTestVisible && e.IsEnabled && e.ActualWidth >= 8 && e.ActualHeight >= 8))
            {
                var r = real.BoundsOf(control);
                if (r.X < 0 || r.Y < 0 || r.X + r.Width > size.Width || r.Y + r.Height > size.Height)
                {
                    continue; // scrolled out of view
                }

                count++;
                foreach (var (where, x, y) in new[] { ("middle", r.X + (r.Width / 2), r.Y + (r.Height / 2)), ("top row", r.X + (r.Width / 2), r.Y + 1), ("bottom row", r.X + (r.Width / 2), r.Y + r.Height - 2) })
                {
                    if (scene.Window.InputHitTest(scene.Window.PointFromScreen(new Point(size.X + x, size.Y + y))) is DependencyObject top && (ReferenceEquals(top, control) || control.IsAncestorOf(top))
                        && real.HitTest(x, y) is var hit && hit != RealWindow.HitClient)
                    {
                        stuck.Add(string.Create(CultureInfo.InvariantCulture, $"{control.GetType().Name} '{control.Name}' at {r.X},{r.Y} {r.Width}x{r.Height}: {where} answers {hit}"));
                    }
                }
            }

            File.WriteAllLines(Path.Combine(Folder, "main-light-hit-tests.txt"), found.Select(a => $"{a.Key}: {a.Value}").Concat(stuck.Select(b => "NOT CLICKABLE " + b)).Append($"controls checked: {count}"));
            return (found, stuck, count);
        });

        string Answer(string key) => $"{key}: {answers[key]}";
        foreach (var control in new[] { "search", "bell", "account", "minimize", "close" })
        {
            Assert.All(answers.Where(a => a.Key.StartsWith(control + ",", StringComparison.Ordinal)), a => Assert.True(a.Value == RealWindow.HitClient, $"{a.Key} answers {a.Value}, not a click on the control"));
        }

        Assert.All(answers.Where(a => a.Key.StartsWith("maximize,", StringComparison.Ordinal)), a => Assert.True(a.Value == RealWindow.HitMaximizeButton, $"{a.Key} answers {a.Value}, not HTMAXBUTTON (Snap Layouts)"));
        Assert.Equal("title bar, between search and bell: 2", Answer("title bar, between search and bell"));
        Assert.Equal("title bar, its last row: 2", Answer("title bar, its last row"));
        Assert.Equal("title bar, over the wordmark: 2", Answer("title bar, over the wordmark"));
        Assert.Equal("page, first row under the title bar: 1", Answer("page, first row under the title bar"));
        Assert.Equal("page: 1", Answer("page"));
        Assert.Equal(RealWindow.HitLeft, answers["left edge"]);
        Assert.Equal(RealWindow.HitRight, answers["right edge"]);
        Assert.Equal(RealWindow.HitBottom, answers["bottom edge"]);
        Assert.Equal(RealWindow.HitTop, answers["top edge"]);
        Assert.Equal(RealWindow.HitTopLeft, answers["top left corner"]);
        Assert.Equal(RealWindow.HitBottomLeft, answers["bottom left corner"]);
        Assert.Equal(RealWindow.HitBottomRight, answers["bottom right corner"]);
        Assert.True(checkedControls >= 12, $"only {checkedControls} controls were found in the main window");
        Assert.True(blocked.Count == 0, "Controls that cannot be clicked where they are drawn: " + string.Join("; ", blocked));
    }

    /// <summary>
    /// The maximized layout, in a window the size Windows gives a maximized one on the primary monitor (the work area plus the frame on every
    /// side) but not maximized, because Windows would put it on the monitor: the content is pulled in by exactly the frame, so nothing is cut off
    /// at a screen edge and nothing is left over; the buttons sit in the top-right corner of what is on screen; and the whole title bar still
    /// drags, though its top is now below the window's own top edge.
    /// </summary>
    [Fact]
    public void MaximizedLayout_CutsNothingOff_AndKeepsTheButtonsInTheCorner()
    {
        Assert.SkipWhen(RealWindow.Unavailable is not null, RealWindow.Unavailable ?? string.Empty);
        var facts = StaHost.Run(() =>
        {
            var inset = ChromeWindow.MaximizedInset;
            var work = SystemParameters.WorkArea;
            using var scene = Scene.Open("main", "light", window =>
            {
                window.Width = work.Width + inset.Left + inset.Right;
                window.Height = work.Height + inset.Top + inset.Bottom;
            });
            var real = scene.Real;
            scene.Window.ApplyStateLayout(maximized: true);
            real.Settle(300);
            var capture = real.Capture();
            capture.Save(Path.Combine(Folder, "main-light-maximized-layout.png"));
            capture.Crop(new Int32Rect(Math.Max(0, capture.Width - 200), 0, Math.Min(200, capture.Width), 70)).Save(Path.Combine(Folder, "main-light-maximized-layout-buttons.png"), 5);
            var scale = real.Scale;
            var root = scene.Part<Border>("PART_Root");
            var buttons = real.BoundsOf(scene.Part<FrameworkElement>("CaptionButtons"));
            var content = real.BoundsOf(scene.Part<FrameworkElement>("ContentHost"));
            var maximize = real.BoundsOf(scene.Part<FrameworkElement>("PART_Maximize"));
            var search = real.BoundsOf(scene.Named<Button>("SearchField"));
            var bell = real.BoundsOf(scene.Named<Button>("NotificationsButton"));
            var px = (int)Math.Round(inset.Top * scale);
            var titleBottom = px + (int)Math.Round(scene.Window.TitleBarHeight * scale);
            var gap = (search.X + search.Width + bell.X) / 2;
            var maximized = (
                Inset: inset,
                Scale: scale,
                Size: real.Bounds,
                root.Padding,
                Buttons: buttons,
                Content: content,
                Caption: WindowChrome.GetWindowChrome(scene.Window).CaptionHeight,
                Title: scene.Window.TitleBarHeight,
                TitleLastRow: real.HitTest(gap, titleBottom - 1),
                UnderTitle: real.HitTest(gap, titleBottom),
                Maximize: real.HitTest(maximize.X + (maximize.Width / 2), maximize.Y + (maximize.Height / 2)),
                CornerButton: real.HitTest(buttons.X + buttons.Width - 1, buttons.Y),
                OffScreenStrip: ButtonArea.IsOneColour(capture.Crop(new Int32Rect(0, 0, capture.Width, px))));
            scene.Window.ApplyStateLayout(maximized: false);
            real.Settle(100);
            return (maximized, RestoredPadding: root.Padding, RestoredCaption: WindowChrome.GetWindowChrome(scene.Window).CaptionHeight);
        });

        var m = facts.maximized;
        var frame = (int)Math.Round(m.Inset.Left * m.Scale);
        Assert.Equal(m.Inset, m.Padding);
        // What is on screen of a maximized window is the window less the frame on every side: the content fills exactly that.
        Assert.Equal(new Int32Rect(frame, frame, m.Size.Width - (2 * frame), m.Size.Height - (2 * frame)), m.Content);
        Assert.Equal(m.Size.Width - frame, m.Buttons.X + m.Buttons.Width); // flush with the right edge of the screen
        Assert.Equal(frame, m.Buttons.Y);                                  // and with its top
        Assert.True(m.OffScreenStrip, "something is drawn in the strip that is off the screen while maximized");
        Assert.Equal(m.Title + m.Inset.Top, m.Caption);
        Assert.Equal(RealWindow.HitCaption, m.TitleLastRow);
        Assert.Equal(RealWindow.HitClient, m.UnderTitle);
        Assert.Equal(RealWindow.HitMaximizeButton, m.Maximize);
        Assert.Equal(RealWindow.HitClient, m.CornerButton); // the close button reaches the corner of the screen
        Assert.Equal(new Thickness(0), facts.RestoredPadding);
        Assert.Equal(m.Title, facts.RestoredCaption);
    }

    /// <summary>
    /// A dialog is a transparent window that draws its own card and shadow, so Windows must draw nothing for it: it asks for no round corners
    /// (a window that does gets Windows' rounded border around its whole rectangle, shadow room included: the outline around the released
    /// Change password dialog), the frame is not extended into it, it has no caption or sizing frame, it shows no caption buttons, and it draws
    /// nothing itself along the edge of the window. PrintWindow returns only what the dialog draws, so the border Windows drew cannot be captured;
    /// what is checked is that nothing asks for it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Dialog_AsksWindowsForNoFrame_AndDrawsNothingAtItsEdge(string kind, string theme)
    {
        Assert.SkipWhen(RealWindow.Unavailable is not null, RealWindow.Unavailable ?? string.Empty);
        var facts = StaHost.Run(() =>
        {
            using var scene = Scene.Open(kind, theme);
            var capture = scene.Real.Capture();
            capture.Save(Path.Combine(Folder, $"dialog-{kind}-{theme}.png"));
            var root = scene.Part<Border>("PART_Root");
            var ring = (int)Math.Ceiling(2 * scene.Real.Scale);
            return (
                Corner: scene.Real.DwmAttribute(RealWindow.DwmCornerPreference),
                NonClient: scene.Real.DwmAttribute(RealWindow.DwmNonClientRendering),
                Glass: WindowChrome.GetWindowChrome(scene.Window).GlassFrameThickness,
                scene.Real.Style,
                scene.Window.IsMicaActive,
                Buttons: scene.Part<FrameworkElement>("CaptionButtons").IsVisible,
                Stroke: root.BorderThickness,
                Background: (scene.Window.Background as SolidColorBrush)?.Color,
                Edge: ButtonArea.IsOneColour(capture.Crop(new Int32Rect(0, 0, capture.Width, ring)))
                    && ButtonArea.IsOneColour(capture.Crop(new Int32Rect(0, capture.Height - ring, capture.Width, ring)))
                    && ButtonArea.IsOneColour(capture.Crop(new Int32Rect(0, 0, ring, capture.Height)))
                    && ButtonArea.IsOneColour(capture.Crop(new Int32Rect(capture.Width - ring, 0, ring, capture.Height))),
                Colours: capture.DistinctColours(64));
        });

        if (Dwm.DrawsRoundCorners)
        {
            Assert.True(facts.Corner == CornerDoNotRound, $"the dialog asks Windows for corner preference {facts.Corner}: Windows rounds the window and draws its border around it");
        }

        Assert.True(facts.NonClient is null or 0, "Windows renders a non-client frame for the dialog");
        Assert.Equal(new Thickness(0), facts.Glass);
        Assert.True((facts.Style & (Caption | ThickFrame)) != Caption && (facts.Style & ThickFrame) == 0, $"window style 0x{facts.Style:X} has a caption or a sizing frame");
        Assert.False(facts.IsMicaActive);
        Assert.False(facts.Buttons, "the dialog shows the window's caption buttons");
        Assert.Equal(new Thickness(0), facts.Stroke);
        Assert.Equal(Colors.Transparent, facts.Background);
        Assert.True(facts.Edge, "something is drawn along the edge of the dialog window");
        Assert.True(facts.Colours >= 24, $"the dialog drew only {facts.Colours} colours");
    }

    /// <summary>One of the app's windows, shown as a real window off every monitor over the fake API, in a theme.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly Rig _rig;
        private readonly List<IDisposable> _owned = [];

        private Scene(Rig rig, ChromeWindow window)
        {
            _rig = rig;
            Window = window;
        }

        public ChromeWindow Window { get; }

        public RealWindow Real { get; private set; } = null!;

        public static Scene Open(string which, string theme, Action<ChromeWindow>? beforeShow = null)
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            MotionSettings.Override = false;
            RecordPicker.InlineResults = true; // never a popup window: Windows puts a popup on a monitor

            Func<Task> load = () => Task.CompletedTask;
            Scene scene;
            switch (which)
            {
                case "signin":
                {
                    var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
                    scene = new Scene(rig, new LoginWindow { DataContext = rig.Get<LoginViewModel>() });
                    break;
                }

                case "main":
                {
                    // The demo clinic's dashboard, as the shell render tests show it.
                    var rig = new Rig(dispatcher: WpfTestUi.Dispatcher(), clock: new DesignClock());
                    DesignData.Configure(rig.Api);
                    rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
                    var shell = rig.Get<ShellViewModel>();
                    shell.Attach(null, new DesignClock());
                    scene = new Scene(rig, new MainWindow { DataContext = shell });
                    load = () => shell.StartAsync();
                    break;
                }

                case "confirm":
                    scene = new Scene(Part2Harness.NewRig(), DialogWindow.CreateConfirm("Delete donor Hana Greenleaf?", "This removes Hana Greenleaf and their appointment history. It can't be undone.", "Delete donor", "Keep donor"));
                    break;
                case "error":
                    scene = new Scene(Part2Harness.NewRig(), DialogWindow.CreateError("Something went wrong", "Lifestream hit an unexpected problem.", "InvalidOperationException: boom"));
                    break;
                default:
                {
                    var rig = Part2Harness.NewRig();
                    DialogViewModel model;
                    switch (which)
                    {
                        case "change-password":
                            model = new ChangePasswordDialogViewModel(rig.Api);
                            break;
                        case "reset-password":
                            model = new ResetPasswordDialogViewModel(rig.Api, 2, DemoData.Name(9));
                            break;
                        case "receipt":
                            var viewer = new ReceiptViewerViewModel(rig.Api, new NoReceiptPrinter(), 1, isDuplicate: true);
                            load = () => viewer.LoadAsync();
                            model = viewer;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(which), which, "no such window");
                    }

                    model.Bind(null, TimeProvider.System, new FakeDialogs());
                    var window = new FormDialogWindow { DataContext = model };
                    model.CloseRequested += (_, _) => window.Close(); // what the presenter does
                    scene = new Scene(rig, window);
                    scene._owned.Add(model);
                    break;
                }
            }

            beforeShow?.Invoke(scene.Window);
            scene.Real = RealWindow.Show(scene.Window);
            Hosted.Await(load);
            scene.Real.Settle(200);
            return scene;
        }

        /// <summary>A named part of the window's own template.</summary>
        public T Part<T>(string name)
            where T : FrameworkElement => (T)Window.Template.FindName(name, Window);

        /// <summary>A named element anywhere in the window.</summary>
        public T Named<T>(string name)
            where T : FrameworkElement => Hosted.Descendants(Window).OfType<T>().First(e => e.Name == name);

        public void Dispose()
        {
            Real?.Dispose();
            foreach (var owned in _owned)
            {
                owned.Dispose();
            }

            _rig.Dispose();
        }
    }
}

/// <summary>The frame tokens, which hold on every machine (no window is needed).</summary>
[Collection("Wpf")]
public sealed class ChromeFrameTests
{
    /// <summary>
    /// The frame is never extended from the top, with or without Mica. Windows draws its own title bar, caption buttons included, in the top
    /// part of an extended frame, and it shows through the title bar's transparent pixels: a "sheet of glass" (-1) put a second set of buttons
    /// next to ours in 2.1.0. For Mica the frame reaches under the whole window from the bottom instead.
    /// </summary>
    [Fact]
    public void TheWindowFrame_IsNeverExtendedFromTheTop()
    {
        var (plain, mica) = StaHost.Run(() => (ThemeTokens.Inset("Chrome.GlassFrame"), ThemeTokens.Inset("Chrome.GlassFrameMica")));

        Assert.Equal((0.0, 0.0, 0.0), (plain.Left, plain.Top, plain.Right));
        Assert.Equal((0.0, 0.0, 0.0), (mica.Left, mica.Top, mica.Right));
        Assert.True(plain.Bottom >= 0);
        Assert.True(mica.Bottom >= 4320, $"the Mica frame reaches {mica.Bottom} DIP up from the bottom: not under a whole window on a tall monitor");
    }

    /// <summary>
    /// The inset of a maximized window is what Windows really pushes off the screen: the frame of a sizable window (the resize frame plus the
    /// padded border), not the resize frame alone, which is half of it and cut 4 DIP of a maximized window off at every screen edge in 2.1.0.
    /// </summary>
    [Fact]
    public void MaximizedInset_IsTheFrameWindowsPushesOffTheScreen()
    {
        var inset = StaHost.Run(() => ChromeWindow.MaximizedInset);
        var frame = FrameOfASizableWindow();

        Assert.True(frame.Left > 0, "Windows reports no frame for a sizable window");
        Assert.Equal(frame, inset);
    }

    /// <summary>
    /// The frame Windows gives a sizable window on its left, right and bottom, in DIPs at the system's DPI: how far a maximized window reaches
    /// past the work area on every side (at the top the frame has the same border, and the caption above it).
    /// </summary>
    public static Thickness FrameOfASizableWindow()
    {
        const uint overlappedWindow = 0x00CF0000;
        var dpi = GetDpiForSystem();
        var frame = default(Frame);
        Assert.True(AdjustWindowRectExForDpi(ref frame, overlappedWindow, false, 0, dpi));
        var scale = dpi / 96.0;
        return new Thickness(-frame.Left / scale, frame.Bottom / scale, frame.Right / scale, frame.Bottom / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Frame
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref Frame rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);
}

/// <summary>Compares what a real window shows where the caption buttons are with what our own buttons draw.</summary>
internal static class ButtonArea
{
    /// <summary>
    /// How far a pixel must be from the background, on its strongest channel, to count as drawn. The buttons Windows draws for a window that is
    /// not active are faint: 56 from the background in both themes.
    /// </summary>
    private const int Drawn = 24;

    /// <summary>How far a pixel must be from the background to count as a full-strength stroke (a stroke smeared over two rows has half of it on each).</summary>
    private const int Strong = 96;

    internal sealed record Comparison(int Stray, string FirstStray, int Ours, int OursSeen, (byte R, byte G, byte B) Background);

    /// <summary>How much of each pixel our own buttons cover (0 to 255), drawn by WPF alone from the same elements.</summary>
    public static byte[] Coverage(FrameworkElement element, int width, int height, double scale)
    {
        // A render of an element is drawn at the element's offset in its parent, so the bitmap reaches from the parent's origin to the element's far corner.
        var offset = VisualTreeHelper.GetOffset(element);
        var left = (int)Math.Round(offset.X * scale);
        var top = (int)Math.Round(offset.Y * scale);
        var bitmap = new RenderTargetBitmap(left + width, top + height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var data = new byte[width * height * 4];
        bitmap.CopyPixels(new Int32Rect(left, top, width, height), data, width * 4, 0);
        var coverage = new byte[width * height];
        for (var i = 0; i < coverage.Length; i++)
        {
            coverage[i] = data[(i * 4) + 3];
        }

        return coverage;
    }

    /// <summary>
    /// Pixels the window shows that our buttons do not draw (within one pixel, for the edge of a glyph), and how much of our buttons the window
    /// shows. The window's outermost two pixels and its corner are left out: the compositor may draw the frame line and the round corner there.
    /// </summary>
    public static Comparison Compare(WindowCapture seen, byte[] ours, double scale)
    {
        var background = Background(seen);
        var width = seen.Width;
        var height = seen.Height;
        var edge = (int)Math.Ceiling(2 * scale);
        var corner = (int)Math.Ceiling(8 * scale);
        bool IsDrawn(int x, int y)
        {
            var (r, g, b) = seen.At(x, y);
            return Math.Max(Math.Abs(r - background.R), Math.Max(Math.Abs(g - background.G), Math.Abs(b - background.B))) > Drawn;
        }

        bool Near(Func<int, int, bool> test, int x, int y)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (nx, ny) = (x + dx, y + dy);
                    if (nx >= 0 && ny >= 0 && nx < width && ny < height && test(nx, ny))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var stray = 0;
        var first = string.Empty;
        var oursCount = 0;
        var oursSeen = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var frame = y < edge || x >= width - edge || (y < corner && x >= width - corner);
                if (!frame && IsDrawn(x, y) && !Near((nx, ny) => ours[(ny * width) + nx] > 40, x, y))
                {
                    stray++;
                    if (first.Length == 0)
                    {
                        first = string.Create(CultureInfo.InvariantCulture, $"{x},{y} = {seen.At(x, y)}");
                    }
                }

                if (ours[(y * width) + x] > 200)
                {
                    oursCount++;
                    if (Near(IsDrawn, x, y))
                    {
                        oursSeen++;
                    }
                }
            }
        }

        return new Comparison(stray, first, oursCount, oursSeen, background);
    }

    /// <summary>The number of pixel rows of a capture that have a stroke on them.</summary>
    public static int InkRows(WindowCapture seen)
    {
        var background = Background(seen);
        var rows = 0;
        for (var y = 0; y < seen.Height; y++)
        {
            for (var x = 0; x < seen.Width; x++)
            {
                var (r, g, b) = seen.At(x, y);
                if (Math.Max(Math.Abs(r - background.R), Math.Max(Math.Abs(g - background.G), Math.Abs(b - background.B))) > Strong)
                {
                    rows++;
                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>True when every pixel of the capture has the same colour: nothing is drawn there.</summary>
    public static bool IsOneColour(WindowCapture seen)
    {
        if (seen.Width == 0 || seen.Height == 0)
        {
            return true;
        }

        var first = seen.At(0, 0);
        for (var y = 0; y < seen.Height; y++)
        {
            for (var x = 0; x < seen.Width; x++)
            {
                if (seen.At(x, y) != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The colour behind the buttons: the middle value of each channel (what is drawn on it is a small part of the area).</summary>
    private static (byte R, byte G, byte B) Background(WindowCapture seen)
    {
        var count = seen.Width * seen.Height;
        if (count == 0)
        {
            return (0, 0, 0);
        }

        var (r, g, b) = (new byte[count], new byte[count], new byte[count]);
        for (var i = 0; i < count; i++)
        {
            (r[i], g[i], b[i]) = (seen.Pixels[(i * 4) + 2], seen.Pixels[(i * 4) + 1], seen.Pixels[i * 4]);
        }

        Array.Sort(r);
        Array.Sort(g);
        Array.Sort(b);
        return (r[count / 2], g[count / 2], b[count / 2]);
    }
}
