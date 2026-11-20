using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lifestream.Client.Auth;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Tests.RedesignA;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// How the app's text really looks: the sign-in window and a list screen in REAL windows, drawn by the Windows compositor with the PC's own text
/// settings (ClearType, display scale), not by an off-screen bitmap. Every other render of this project is a <see cref="RenderTargetBitmap"/>, which always
/// draws grayscale text and so cannot show what a user sees.
/// <para>
/// The windows are never on a monitor: they are shown outside the virtual screen, without activation and without a taskbar button, captured with
/// <c>PrintWindow(PW_RENDERFULLCONTENT)</c> and closed within seconds. Nothing is clicked, typed or focused, no UI Automation pattern is called (calling
/// one on an off-screen window made it the foreground window elsewhere), no window is maximized (that puts it on a monitor), and no other window is
/// captured. While a window exists a hook on its thread refuses every activation and every focus change, and the harness checks before, during and
/// after the capture that the window is off every monitor and that the foreground window is not one of this process; if either is ever not so it
/// closes the window at once and fails.
/// </para>
/// <para>
/// Runs only when <c>LIFESTREAM_REALWINDOW_DIR</c> is set (the folder for the images), so a normal test run and CI never create a window.
/// <c>LIFESTREAM_REALWINDOW_SETUPS</c> may name setups to compare, separated by ';', each <c>tag|font folder#family|formatting|rendering|kerning</c> (kerning: <c>kern</c> or <c>nokern</c>) with empty parts
/// meaning "as the app ships": <c>shipped|||;ideal|||Ideal|;other|C:\fonts\x#Some Family|Ideal|Grayscale</c>.
/// </para>
/// </summary>
[Collection("Wpf")]
public sealed class RealWindowTextTests
{
    private static string Sample => Environment.GetEnvironmentVariable("LIFESTREAM_REALWINDOW_SAMPLE") is { Length: > 0 } text ? text : DefaultSample;

    private const string DefaultSample = "Adrian Mukherjee  O\u2212 A+ AB\u2212  Eligible to donate  29 Jul 2026  1,234.50  W\u00f3jcik Bergstr\u00f6m Ay\u015fe Garc\u00eda  No account needed. The demo clinic uses made-up people";

    /// <summary>One way of drawing the text: a tag for the file name and what it overrides (null: as the app ships).</summary>
    internal sealed record Setup(string Tag, string? FontFolder, string? Family, TextFormattingMode? Formatting, TextRenderingMode? Rendering, bool? Kerning = null)
    {
        public static List<Setup> FromEnvironment()
        {
            var text = Environment.GetEnvironmentVariable("LIFESTREAM_REALWINDOW_SETUPS");
            if (string.IsNullOrWhiteSpace(text))
            {
                return [new Setup("shipped", null, null, null, null)];
            }

            var setups = new List<Setup>();
            foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = item.Split('|');
                string? Part(int i) => parts.Length > i && parts[i].Length > 0 ? parts[i] : null;
                var font = Part(1)?.Split('#');
                setups.Add(new Setup(
                    parts[0],
                    font?[0],
                    font is { Length: > 1 } ? font[1] : null,
                    Part(2) is { } formatting ? Enum.Parse<TextFormattingMode>(formatting) : null,
                    Part(3) is { } rendering ? Enum.Parse<TextRenderingMode>(rendering) : null,
                    Part(4) is { } kerning ? kerning == "kern" : null));
            }

            return setups;
        }

        /// <summary>The font family of this setup: the theme's, or the family in the given folder.</summary>
        public FontFamily? Font() => FontFolder is null ? null : new FontFamily(new Uri(Path.GetFullPath(FontFolder).TrimEnd('\\') + "\\"), "./#" + Family);

        /// <summary>Puts the setup's font in the theme of this thread (every text refers to it) and returns it.</summary>
        public void ApplyFont()
        {
            if (Font() is { } font)
            {
                ThemeLoader.Resources["Font.Family"] = font;
                ThemeLoader.Resources["Font.Display"] = font;
            }
        }

        /// <summary>Forces the setup's formatting and rendering on the window and on every element that sets its own.</summary>
        public void ApplyTextOptions(Window window)
        {
            if (Kerning is { } kerning)
            {
                Typography.SetKerning(window, kerning); // inherited by every text of the window
            }

            if (Formatting is null && Rendering is null)
            {
                return;
            }

            foreach (var node in Hosted.Descendants(window).Prepend(window))
            {
                Force(node, TextOptions.TextFormattingModeProperty, Formatting, ReferenceEquals(node, window));
                Force(node, TextOptions.TextRenderingModeProperty, Rendering, ReferenceEquals(node, window));
            }
        }

        private static void Force(DependencyObject node, DependencyProperty property, object? value, bool always)
        {
            if (value is null)
            {
                return;
            }

            var source = DependencyPropertyHelper.GetValueSource(node, property).BaseValueSource;
            if (always || source is not (BaseValueSource.Default or BaseValueSource.Inherited))
            {
                node.SetValue(property, value);
            }
        }
    }

    private static string? OutputFolder => Environment.GetEnvironmentVariable("LIFESTREAM_REALWINDOW_DIR") is { Length: > 0 } dir ? dir : null;

    [Theory]
    [Trait("Category", "RealWindow")]
    [InlineData("light")]
    [InlineData("dark")]
    public void SignInWindow_AsTheCompositorDrawsIt(string theme)
    {
        Assert.SkipWhen(OutputFolder is null, "LIFESTREAM_REALWINDOW_DIR is not set");
        var mica = Environment.GetEnvironmentVariable("LIFESTREAM_REALWINDOW_MICA") == "1";
        foreach (var setup in Setup.FromEnvironment())
        {
            var report = StaHost.Run(() =>
            {
                MotionSettings.Override = false; // end states, as in the off-screen renders
                Dwm.MicaDisabled = !mica;
                ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
                setup.ApplyFont();
                using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
                DesignData.Configure(rig.Api);
                var vm = rig.Get<LoginViewModel>();
                vm.RememberUsername = true;
                // The size of the owner's screenshot of 2.1.0 (scratch/font-gsf/owner-signin.png): 1135 x 755.
                var window = new LoginWindow { DataContext = vm, Width = 1135, Height = 755 };
                return RealWindow.ShowCaptureClose(window, Path.Combine(OutputFolder!, $"signin-{theme}-{setup.Tag}{(mica ? "-mica" : string.Empty)}.png"), setup, () => Hosted.Await(() => vm.CheckServerAsync()));
            });
            Assert.True(report.Width >= 1135 && report.Height >= 755, $"the capture is {report.Width} x {report.Height}");
            Assert.True(report.Colours > 50, $"the capture of the sign-in window shows {report.Colours} colours: nothing was drawn");
        }
    }

    [Theory]
    [Trait("Category", "RealWindow")]
    [InlineData("light")]
    [InlineData("dark")]
    public void DonorsList_InTheShell_AsTheCompositorDrawsIt(string theme)
    {
        Assert.SkipWhen(OutputFolder is null, "LIFESTREAM_REALWINDOW_DIR is not set");
        foreach (var setup in Setup.FromEnvironment())
        {
            var report = StaHost.Run(() =>
            {
                MotionSettings.Override = false;
                Dwm.MicaDisabled = true;
                ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
                setup.ApplyFont();
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher(), clock: new DesignClock());
                RecordPicker.InlineResults = true; // never a popup window
                DesignData.Configure(rig.Api);
                ListScreensDesignData.ConfigureDonors(rig.Api);
                rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
                var shell = rig.Get<ShellViewModel>();
                shell.Attach(null, new DesignClock());
                var window = new ChromeWindow { Title = "Lifestream", ExtendsContentIntoTitleBar = true, Content = new ShellView { DataContext = shell }, Width = 1440, Height = 900 };
                return RealWindow.ShowCaptureClose(window, Path.Combine(OutputFolder!, $"donors-{theme}-{setup.Tag}.png"), setup, () =>
                {
                    Hosted.Await(() => shell.StartAsync());
                    StaHost.Settle(300);
                    Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == "Donors").Destination));
                    StaHost.Settle(400);
                    var page = (DonorsViewModel)shell.CurrentPage!;
                    Hosted.Await(() =>
                    {
                        page.SelectedItem = page.Items[0];
                        return page.PendingDetail;
                    });
                    Hosted.Await(() => shell.RefreshCountsAsync());
                });
            });
            Assert.True(report.Width >= 1440 && report.Height >= 900, $"the capture is {report.Width} x {report.Height}");
            Assert.True(report.Colours > 50, $"the capture of the donors list shows {report.Colours} colours: nothing was drawn");
        }
    }

    /// <summary>The same line of text in every setup, one under the other, at the sizes and weights of the type ramp: the quickest way to compare crispness.</summary>
    [Theory]
    [Trait("Category", "RealWindow")]
    [InlineData("light")]
    [InlineData("dark")]
    public void TextLadder_EverySetup_AsTheCompositorDrawsIt(string theme)
    {
        Assert.SkipWhen(OutputFolder is null, "LIFESTREAM_REALWINDOW_DIR is not set");
        // A window is never taller than the screen, so the setups go five to a window.
        var pages = Setup.FromEnvironment().Chunk(5).ToList();
        for (var page = 0; page < pages.Count; page++)
        {
            var setups = pages[page];
            var name = $"ladder-{theme}-{page + 1}.png";
            var report = StaHost.Run(() =>
            {
                ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
                var resources = ThemeLoader.Resources;
                var stack = new StackPanel { Margin = new Thickness(16) };
                foreach (var setup in setups)
                {
                    var font = setup.Font() ?? (FontFamily)resources["Font.Family"];
                    var formatting = setup.Formatting ?? TextFormattingMode.Display;
                    var rendering = setup.Rendering ?? TextRenderingMode.ClearType;
                    var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
                    TextOptions.SetTextFormattingMode(block, formatting);
                    TextOptions.SetTextRenderingMode(block, rendering);
                    TextOptions.SetTextHintingMode(block, TextHintingMode.Fixed);
                    if (setup.Kerning is { } kerning)
                    {
                        Typography.SetKerning(block, kerning);
                    }

                    block.Children.Add(Line($"{setup.Tag}: {formatting}, {rendering}", (FontFamily)resources["Font.Family"], 12, FontWeights.Bold, "Brush.Ink3"));
                    foreach (var (size, weight) in new[] { (12d, FontWeights.Normal), (13d, FontWeights.Normal), (13d, FontWeights.Medium), (14d, FontWeights.Normal), (14d, FontWeights.SemiBold), (15d, FontWeights.SemiBold), (20d, FontWeights.SemiBold), (28d, FontWeights.SemiBold) })
                    {
                        block.Children.Add(Line($"{size:0} {Sample}", font, size, weight, "Brush.Ink"));
                    }

                    stack.Children.Add(block);
                }

                var root = new Border { Child = stack };
                root.SetResourceReference(Border.BackgroundProperty, "Brush.Layer");
                root.Resources.MergedDictionaries.Add(resources);
                var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = root, Width = 1300, Height = 40 + (setups.Length * 235), UseLayoutRounding = true, SnapsToDevicePixels = true };
                return RealWindow.ShowCaptureClose(window, Path.Combine(OutputFolder!, name), null, () => { });
            });
            Assert.True(report.Colours > 50, $"{name} shows {report.Colours} colours: nothing was drawn");
        }
    }

    private static TextBlock Line(string text, FontFamily font, double size, FontWeight weight, string brush)
    {
        var line = new TextBlock { Text = text, FontFamily = font, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 2, 0, 2) };
        line.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return line;
    }
}

/// <summary>Shows a window outside every monitor, captures what the compositor draws for it, and closes it. See <see cref="RealWindowTextTests"/> for the limits it keeps.</summary>
internal static class RealWindow
{
    private const int OffScreen = -32000;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint PwRenderFullContent = 0x2;
    private const int WhCbt = 5;
    private const int HcbtActivate = 5;
    private const int HcbtSetFocus = 9;

    /// <summary>Kept alive for as long as the process: the hook calls it from native code.</summary>
    private static readonly HookProc RefuseActivation = static (code, wParam, lParam) =>
        code is HcbtActivate or HcbtSetFocus ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, wParam, lParam); // 1: the activation or focus change does not happen

    internal sealed record Report(int Width, int Height, int Colours, double Scale, string Rect);

    public static Report ShowCaptureClose(Window window, string path, RealWindowTextTests.Setup? setup, Action load)
    {
        // The virtual screen is every monitor together. The window goes far outside it, and is checked there before anything is captured.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Assert.False(screen.IntersectsWith(new Rect(OffScreen, OffScreen, window.Width, window.Height)), $"{OffScreen},{OffScreen} is on the virtual screen {screen}: no window is shown");
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = OffScreen;
        window.Top = OffScreen;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Topmost = false;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExNoActivate | WsExToolWindow)); // never activated, never in Alt+Tab
        };
        // No window of this thread can be activated or take the keyboard focus while the hook is in place; without it no window is shown.
        var hook = SetWindowsHookEx(WhCbt, RefuseActivation, IntPtr.Zero, GetCurrentThreadId());
        Assert.True(hook != IntPtr.Zero, "the hook that refuses activation could not be installed: no window is shown");
        var foregroundBefore = GetForegroundWindow();
        try
        {
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            CloseIfForeground(window, "when it was shown");
            Assert.True(GetWindowRect(hwnd, out var rect), "the window has no rectangle");
            var placed = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            var pixels = new Rect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            if (placed.IntersectsWith(pixels))
            {
                window.Close();
                Assert.Fail($"Windows placed the window at {placed}, on the virtual screen {pixels}; it was closed at once and nothing was captured");
            }

            StaHost.Settle(300);
            load();
            CloseIfForeground(window, "while it loaded");
            StaHost.Settle(700);
            setup?.ApplyTextOptions(window);
            StaHost.Settle(500);
            // The compositor gets the window's picture some frames after WPF has laid it out (longer for the first window of a thread): capture until
            // the picture has content and two captures in a row are the same, ten seconds at most.
            var png = Capture(hwnd, rect.Right - rect.Left, rect.Bottom - rect.Top);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                StaHost.Settle(250);
                CloseIfForeground(window, "while it was captured");
                var next = Capture(hwnd, rect.Right - rect.Left, rect.Bottom - rect.Top);
                var settled = next.AsSpan().SequenceEqual(png) && StaHost.CountDistinctColours(next) > 50;
                png = next;
                if (settled)
                {
                    break;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            File.WriteAllText(Path.ChangeExtension(path, ".txt"), Describe(window, placed));
            return new Report(rect.Right - rect.Left, rect.Bottom - rect.Top, StaHost.CountDistinctColours(png), VisualTreeHelper.GetDpi(window).DpiScaleX, placed.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            var foreground = GetForegroundWindow();
            window.Close();
            UnhookWindowsHookEx(hook);
            File.AppendAllText(Path.ChangeExtension(path, ".guard.txt"), $"foreground before 0x{foregroundBefore:X}, at close 0x{foreground:X}, ours: {IsOurs(foreground)}{Environment.NewLine}");
        }
    }

    /// <summary>True when the window belongs to this process.</summary>
    private static bool IsOurs(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(hwnd, out var process);
        return process == (uint)Environment.ProcessId;
    }

    /// <summary>The foreground window is the owner's, never one of this process: if it is, the window is closed at once and the test fails, saying so.</summary>
    private static void CloseIfForeground(Window window, string when)
    {
        var foreground = GetForegroundWindow();
        if (IsOurs(foreground))
        {
            window.Close();
            Assert.Fail($"A window of the test process (0x{foreground:X}) was the foreground window {when}; the test window was closed at once. REPORT THIS: the owner's focus was taken.");
        }
    }

    /// <summary>What the capture shows, for the record: where the window was, its scale, and each visible text with its font file and text options.</summary>
    private static string Describe(Window window, Rect placed)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"window at {placed} (device pixels), scale {VisualTreeHelper.GetDpi(window).DpiScaleX:0.##}, {DateTime.UtcNow:u}");
        foreach (var text in Hosted.Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible && t.ActualWidth > 0 && !string.IsNullOrWhiteSpace(t.Text)))
        {
            var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            var file = typeface.TryGetGlyphTypeface(out var glyphs) ? Path.GetFileName(glyphs.FontUri.AbsolutePath) : "(no font file)";
            var content = text.Text.Length > 40 ? text.Text[..40] : text.Text;
            report.AppendLine(CultureInfo.InvariantCulture, $"{text.FontSize,4:0.#} {text.FontWeight.ToOpenTypeWeight(),3} {file,-28} {TextOptions.GetTextFormattingMode(text),-7} {TextOptions.GetTextRenderingMode(text),-9} {TextOptions.GetTextHintingMode(text),-8} \"{content}\"");
        }

        return report.ToString();
    }

    private static byte[] Capture(IntPtr hwnd, int width, int height)
    {
        var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var memory = CreateCompatibleDC(IntPtr.Zero); // a memory surface of our own: the screen is never read
        var bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            Assert.True(PrintWindow(hwnd, memory, PwRenderFullContent), "PrintWindow failed");
            var data = new byte[width * height * 4];
            Marshal.Copy(bits, data, 0, data.Length);
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, data, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
