// The probe for the isolated desktop (tests/isolated/README.md). It does on purpose everything that took the person's keyboard on 2026-11-20
// and more: it shows WPF windows on the monitor area (of its own desktop), modal dialogs, popups, menus, drop-downs and tooltips, maximizes,
// calls Focus(), Activate(), SetForegroundWindow, SetActiveWindow, SetFocus and BringWindowToTop, and is driven through UI Automation by a
// second process (SetValue, Invoke, Toggle, ExpandCollapse, SetFocus). It refuses to create a single window unless it runs on a
// LifestreamIso-* desktop that is not the input desktop. It also says what PrintWindow returns there.
//
//   Lifestream.IsolationProbe session <seconds> <outDir> [--software]   (starts the driver itself)
//   Lifestream.IsolationProbe drive <pid> <seconds> <outDir>
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lifestream.IsolationProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var where = Desktops.Check();
        if (where is not null)
        {
            Console.Error.WriteLine("REFUSED: " + where);
            return 5;
        }

        return args switch
        {
            ["session", var seconds, var outDir, .. var rest] => new Session(int.Parse(seconds, CultureInfo.InvariantCulture), outDir, rest.Contains("--software")).Run(),
            ["drive", var pid, var seconds, var outDir] => Driver.Run(int.Parse(pid, CultureInfo.InvariantCulture), int.Parse(seconds, CultureInfo.InvariantCulture), outDir),
            ["feedback", var outDir] => Feedback.Run(outDir),
            ["noop", var ms] => Feedback.Noop(int.Parse(ms, CultureInfo.InvariantCulture)),
            _ => 2,
        };
    }
}

internal static class Desktops
{
    /// <summary>Why this process must not create windows here, or null when it is on an isolated desktop that does not receive input.</summary>
    public static string? Check()
    {
        var mine = Name(GetThreadDesktop(GetCurrentThreadId()));
        if (!mine.StartsWith("LifestreamIso-", StringComparison.Ordinal))
        {
            return $"this process is on the desktop '{mine}', not an isolated LifestreamIso-* desktop";
        }

        var input = OpenInputDesktop(0, false, 0x0001);
        if (input != IntPtr.Zero)
        {
            try
            {
                if (string.Equals(Name(input), mine, StringComparison.OrdinalIgnoreCase))
                {
                    return $"the desktop '{mine}' is the input desktop";
                }
            }
            finally
            {
                CloseDesktop(input);
            }
        }

        return null;
    }

    public static string Mine => Name(GetThreadDesktop(GetCurrentThreadId()));

    public static string InputName
    {
        get
        {
            var input = OpenInputDesktop(0, false, 0x0001);
            if (input == IntPtr.Zero)
            {
                return $"(cannot open: {Marshal.GetLastWin32Error()})";
            }

            try
            {
                return Name(input);
            }
            finally
            {
                CloseDesktop(input);
            }
        }
    }

    private static string Name(IntPtr handle)
    {
        var buffer = new byte[512];
        return GetUserObjectInformationW(handle, 2, buffer, buffer.Length, out var needed) ? Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2)) : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, byte[] buffer, int length, out int needed);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

internal sealed class Session(int seconds, string outDir, bool software)
{
    private readonly Dictionary<string, int> _done = [];
    private readonly List<string> _lines = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Window _main = null!;
    private TextBox _text = null!;
    private ComboBox _combo = null!;
    private Popup _popup = null!;
    private int _step;
    private int _captures;

    private void Note(string line)
    {
        _lines.Add($"{_clock.Elapsed.TotalSeconds,7:F2} {line}");
    }

    private void Count(string what) => _done[what] = _done.GetValueOrDefault(what) + 1;

    public int Run()
    {
        Directory.CreateDirectory(outDir);
        if (software)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Note($"desktop '{Desktops.Mine}', input desktop '{Desktops.InputName}', render {(software ? "software" : "default")}, tier {RenderCapability.Tier >> 16}");
        _text = new TextBox { Name = "text", Width = 240, Text = "hello" };
        AutomationProperties.SetAutomationId(_text, "text");
        _text.TextChanged += (_, _) => Note($"TextChanged to '{_text.Text}' at {DateTime.Now:HH:mm:ss.fff}");
        var button = new Button { Content = "Press", Width = 120 };
        AutomationProperties.SetAutomationId(button, "button");
        button.Click += (_, _) => Count("button clicked");
        var dialogButton = new Button { Content = "Dialog", Width = 120 };
        AutomationProperties.SetAutomationId(dialogButton, "dialogButton");
        dialogButton.Click += (_, _) => ShowDialog("invoked");
        var check = new CheckBox { Content = "Check" };
        AutomationProperties.SetAutomationId(check, "check");
        _combo = new ComboBox { Width = 160, ItemsSource = new[] { "One", "Two", "Three" }, SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(_combo, "combo");
        var expander = new Expander { Header = "More", Content = new TextBlock { Text = "Inside the expander" } };
        AutomationProperties.SetAutomationId(expander, "expander");
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Copy" });
        menu.Items.Add(new MenuItem { Header = "Paste" });
        _text.ContextMenu = menu;
        button.ToolTip = new ToolTip { Content = "A tooltip" };
        _popup = new Popup { StaysOpen = false, Child = new Border { Background = Brushes.Gold, Padding = new Thickness(12), Child = new TextBlock { Text = "popup" } }, PlacementTarget = button };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Isolation probe", FontSize = 22, Foreground = Brushes.DarkRed });
        panel.Children.Add(new Border { Height = 40, Background = new LinearGradientBrush(Colors.SteelBlue, Colors.OrangeRed, 0) });
        foreach (UIElement child in new UIElement[] { _text, button, dialogButton, check, _combo, expander })
        {
            panel.Children.Add(child);
        }

        _main = new Window { Title = "Isolation probe", Width = 520, Height = 420, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _main.Show();
        _main.Activate();
        Note($"main shown at {_main.Left},{_main.Top}; IsActive {_main.IsActive}; foreground here {Foreground()}");

        using var driver = Process.GetProcessById(Feedback.Start($"\"{Environment.ProcessPath}\" drive {Environment.ProcessId} {seconds - 5} \"{outDir}\"", forceOff: true));
        Note($"driver pid {driver.Id} (started with STARTF_FORCEOFFFEEDBACK)");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => Step();
        timer.Start();
        var end = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        end.Tick += (_, _) =>
        {
            end.Stop();
            timer.Stop();
            app.Shutdown();
        };
        end.Start();
        app.Run();
        driver.WaitForExit(15_000);
        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture, $"seconds {seconds}, software {software}, desktop {Desktops.Mine}");
        foreach (var (what, count) in _done.OrderBy(d => d.Key))
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"{what}: {count}");
        }

        summary.AppendLine("--- log");
        summary.AppendJoin(Environment.NewLine, _lines);
        File.WriteAllText(Path.Combine(outDir, $"probe-{(software ? "software" : "default")}.txt"), summary.ToString());
        Console.WriteLine(summary.ToString());
        return 0;
    }

    private string Foreground()
    {
        var fg = GetForegroundWindow();
        var ours = fg == new WindowInteropHelper(_main).Handle;
        return fg == IntPtr.Zero ? "none" : ours ? "the probe's main window" : $"0x{fg:X}";
    }

    private void ShowDialog(string why)
    {
        var dialog = new Window { Title = "Probe dialog", Width = 300, Height = 160, Owner = _main, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80 };
        AutomationProperties.SetAutomationId(ok, "ok");
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Children = { new TextBox { Text = "in the dialog" }, ok } };
        var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            dialog.Close();
        };
        dialog.Loaded += (_, _) =>
        {
            Count("dialog shown (" + why + ")");
            close.Start();
        };
        dialog.ShowDialog();
    }

    private void Step()
    {
        var hwnd = new WindowInteropHelper(_main).Handle;
        switch (_step++ % 12)
        {
            case 0:
                var extra = new Window { Title = "Extra", Width = 200, Height = 120, Left = 40, Top = 40, Content = new TextBox() };
                extra.Show();
                extra.Activate();
                ((TextBox)extra.Content).Focus();
                Count("extra window shown and activated");
                var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                close.Tick += (_, _) =>
                {
                    close.Stop();
                    extra.Close();
                };
                close.Start();
                break;
            case 1:
                Dispatcher.CurrentDispatcher.BeginInvoke(() => ShowDialog("timer"));
                break;
            case 2:
                _popup.IsOpen = true;
                Count("popup opened");
                break;
            case 3:
                _combo.IsDropDownOpen = true;
                Count("combo drop-down opened");
                break;
            case 4:
                _text.ContextMenu.PlacementTarget = _text;
                _text.ContextMenu.IsOpen = true;
                Count("context menu opened");
                break;
            case 5:
                _main.Activate();
                _text.Focus();
                Keyboard.Focus(_text);
                Count("Activate + Focus");
                Note($"after Activate+Focus: IsActive {_main.IsActive}, keyboard focus on text {_text.IsKeyboardFocused}, foreground here {Foreground()}");
                break;
            case 6:
                var fg = SetForegroundWindow(hwnd);
                SetActiveWindow(hwnd);
                SetFocus(hwnd);
                BringWindowToTop(hwnd);
                Count("SetForegroundWindow + SetActiveWindow + SetFocus + BringWindowToTop");
                Note($"SetForegroundWindow -> {fg}; foreground here {Foreground()}");
                break;
            case 7:
                _main.WindowState = WindowState.Maximized;
                Count("maximized");
                break;
            case 8:
                _main.WindowState = WindowState.Normal;
                if (_main.ToolTip is null && ((StackPanel)_main.Content).Children[3] is Button b && b.ToolTip is ToolTip tip)
                {
                    tip.PlacementTarget = b;
                    tip.IsOpen = true;
                    Count("tooltip opened");
                }

                break;
            case 9:
                Capture(hwnd);
                break;
            case 10:
                _combo.IsDropDownOpen = false;
                _popup.IsOpen = false;
                _text.ContextMenu.IsOpen = false;
                _text.SelectAll();
                Count("closed popups");
                break;
            default:
                Note($"state: IsActive {_main.IsActive}, focus on text {_text.IsKeyboardFocused}, foreground here {Foreground()}, input desktop '{Desktops.InputName}'");
                break;
        }
    }

    private void Capture(IntPtr hwnd)
    {
        foreach (var flags in new uint[] { 2, 0 })
        {
            var (colours, file) = Print(hwnd, flags, _captures < 4);
            Count($"PrintWindow flags {flags}");
            Note($"PrintWindow(flags {flags}): {colours} colours{(file is null ? string.Empty : " -> " + file)}");
        }

        _captures++;
    }

    private (int Colours, string? File) Print(IntPtr hwnd, uint flags, bool save)
    {
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var info = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        var ok = PrintWindow(hwnd, dc, flags);
        GdiFlush();
        SelectObject(dc, old);
        var pixels = new byte[w * h * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);
        DeleteObject(bitmap);
        DeleteDC(dc);
        var colours = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            colours.Add(BitConverter.ToInt32(pixels, i) & 0xFFFFFF);
        }

        string? file = null;
        if (save && ok)
        {
            file = Path.Combine(outDir, $"capture-{(software ? "software" : "default")}-{_captures}-flags{flags}.png");
            var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
            using var stream = File.Create(file);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
        }

        return (ok ? colours.Count : -1, file);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

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

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}

internal static class Driver
{
    public static int Run(int pid, int seconds, string outDir)
    {
        var done = new Dictionary<string, int>();
        var errors = new Dictionary<string, int>();
        var spent = new Dictionary<string, double>();
        void Try(string what, Action action)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                action();
                done[what] = done.GetValueOrDefault(what) + 1;
            }
            catch (Exception e)
            {
                var key = what + ": " + e.GetType().Name + " " + e.Message;
                errors[key] = errors.GetValueOrDefault(key) + 1;
            }

            spent[what] = spent.GetValueOrDefault(what) + watch.Elapsed.TotalMilliseconds;
        }

        var clock = Stopwatch.StartNew();
        AutomationElement? main = null;
        while (main is null && clock.Elapsed.TotalSeconds < 30)
        {
            main = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
                new PropertyCondition(AutomationElement.NameProperty, "Isolation probe")));
            Thread.Sleep(200);
        }

        if (main is null)
        {
            File.WriteAllText(Path.Combine(outDir, "driver.txt"), "the probe's window was not found through UI Automation");
            return 1;
        }

        var diag = new StringBuilder();
        void Time(string what, Func<object?> action)
        {
            var watch = Stopwatch.StartNew();
            string result;
            try
            {
                result = action()?.ToString() ?? "null";
            }
            catch (Exception e)
            {
                result = e.GetType().Name + " " + e.Message;
            }

            diag.AppendLine(CultureInfo.InvariantCulture, $"{DateTime.Now - watch.Elapsed:HH:mm:ss.fff} {watch.Elapsed.TotalMilliseconds,8:F0} ms {what}: {result}");
        }

        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
        {
            Time("top-level " + top.Current.ClassName + " pid " + top.Current.ProcessId, () => top.Current.Name);
        }

        AutomationElement? probeText = null;
        Time("FindFirst text", () => probeText = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "text")));
        Time("FindFirst text again", () => probeText = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "text")));
        Time("GetCurrentPattern", () => probeText!.GetCurrentPattern(ValuePattern.Pattern));
        Time("SetValue", () =>
        {
            ((ValuePattern)probeText!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("diag");
            return "done";
        });
        Time("Current.Name", () => probeText!.Current.Name);
        File.WriteAllText(Path.Combine(outDir, $"driver-diag-{pid}.txt"), diag.ToString());

        AutomationElement Find(string id) => main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
            ?? throw new InvalidOperationException("no " + id);

        var round = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            Try("SetValue", () => ((ValuePattern)Find("text").GetCurrentPattern(ValuePattern.Pattern)).SetValue($"round {round}"));
            Try("Invoke", () => ((InvokePattern)Find("button").GetCurrentPattern(InvokePattern.Pattern)).Invoke());
            Try("Toggle", () => ((TogglePattern)Find("check").GetCurrentPattern(TogglePattern.Pattern)).Toggle());
            Try("Expand combo", () => ((ExpandCollapsePattern)Find("combo").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand());
            Try("Collapse combo", () => ((ExpandCollapsePattern)Find("combo").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse());
            Try("Expand/collapse expander", () =>
            {
                var pattern = (ExpandCollapsePattern)Find("expander").GetCurrentPattern(ExpandCollapsePattern.Pattern);
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    pattern.Expand();
                }
                else
                {
                    pattern.Collapse();
                }
            });
            Try("UIA SetFocus", () => Find("text").SetFocus());
            if (round % 5 == 0)
            {
                Try("Invoke (opens a modal dialog)", () => ((InvokePattern)Find("dialogButton").GetCurrentPattern(InvokePattern.Pattern)).Invoke());
                Thread.Sleep(300);
                Try("Invoke OK in the dialog", () =>
                {
                    var dialog = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, pid), new PropertyCondition(AutomationElement.AutomationIdProperty, "ok")))
                        ?? throw new InvalidOperationException("no dialog");
                    ((InvokePattern)dialog.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                });
            }

            round++;
            Thread.Sleep(150);
        }

        var text = new StringBuilder($"rounds {round}{Environment.NewLine}");
        foreach (var (what, count) in done.OrderBy(d => d.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"ok {what}: {count} ({spent[what] / Math.Max(1, count):F0} ms each)");
        }

        foreach (var (what, count) in errors.OrderBy(d => d.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"error {what}: {count}");
        }

        File.WriteAllText(Path.Combine(outDir, $"driver-{pid}.txt"), text.ToString());
        Console.WriteLine(text);
        return 0;
    }
}

internal static class Feedback
{
    /// <summary>Starts GUI children at logged times, with and without STARTF_FORCEOFFFEEDBACK, to see whether the input desktop's cursor reacts.</summary>
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var lines = new List<string>();
        foreach (var forceOff in new[] { false, true, false, true })
        {
            for (var i = 0; i < 3; i++)
            {
                var at = DateTime.Now;
                var pid = Start($"\"{Environment.ProcessPath}\" noop 1500", forceOff);
                lines.Add($"{at:HH:mm:ss.fff} started pid {pid} forceOffFeedback={forceOff}");
                Thread.Sleep(3000);
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "feedback.txt"), lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return 0;
    }

    public static int Noop(int ms)
    {
        Thread.Sleep(ms);
        return 0;
    }

    public static int Start(string commandLine, bool forceOff)
    {
        var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Flags = forceOff ? 0x80 : 0 };
        if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref startup, out var info))
        {
            throw new InvalidOperationException($"CreateProcess failed ({Marshal.GetLastWin32Error()})");
        }

        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return info.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Count;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
