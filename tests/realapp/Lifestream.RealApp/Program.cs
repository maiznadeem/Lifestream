// Drives the real Lifestream desktop app off the owner's screen (run/briefs/real-window-capture.md):
//   launch  starts Lifestream.exe with LIFESTREAM_OFFSCREEN=1 against a given API, on this process's isolated desktop (both refuse to run
//           anywhere else: tools/realapp-isolated.ps1 sets it up and watches the person's desktop meanwhile);
//   run     executes a script of UI Automation commands (Invoke, Toggle, Value, SelectionItem, ExpandCollapse, ScrollItem, Transform,
//           Window) against that process and captures its windows with PrintWindow(PW_RENDERFULLCONTENT).
// Nothing here moves the pointer, sends keys or activates a window. UIA patterns call the controls directly.
//
// Usage:
//   Lifestream.RealApp launch <exe> <apiBase> <workDir> [--light|--dark|--system]  -> prints the pid
//   Lifestream.RealApp run <pid> <captureDir> <scriptFile|->
// Script commands (one per line, '#' comments, quotes group words):
//   windows | tree [SEL] [depth] | texts [SEL] | props SEL | invoke SEL | toggle SEL | select SEL | expand SEL | collapse SEL
//   set SEL VALUE | scroll SEL | wait SEL [ms] | gone SEL [ms] | sleep ms | capture NAME | size W H (in-app agent; never UIA resize)
//   click SEL | key GESTURE [SEL] | focus SEL | focused   (in-app agent, src/Lifestream.Desktop/Services/OffscreenAgent.cs)
//   close (WindowPattern.Close on the topmost dialog or the main window) | assert SEL | echo TEXT
// Selector: comma-separated terms, all must match: id=AutomationId, name=Exact, name~=substring, type=ControlType, class=ClassName,
//   win~=window-title-substring; '#n' picks the n-th match (0-based) in document order; 'a >> b' finds b inside the first a.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lifestream.RealApp;

internal static partial class Program
{
    private static int _pid;
    private static string _captures = ".";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length >= 1 && args[0] is "launch" or "run" && Isolation.WhyNot() is { } why)
        {
            Console.Error.WriteLine($"REFUSED: the real-app driver runs only on an isolated desktop (tools/realapp-isolated.ps1): {why}.");
            return 5;
        }

        if (args.Length >= 4 && args[0] == "launch")
        {
            return Launch(args[1], args[2], args[3], args.Length > 4 ? args[4] : "--system");
        }

        if (args.Length >= 3 && args[0] == "derive")
        {
            Console.Write(Derive(args[1], args[2]));
            return 0;
        }

        if (args.Length >= 4 && args[0] == "run")
        {
            _pid = int.Parse(args[1], CultureInfo.InvariantCulture);
            _captures = args[2];
            Directory.CreateDirectory(_captures);
            var lines = args[3] == "-" ? Console.In.ReadToEnd().Split('\n') : File.ReadAllLines(args[3]);
            return RunScript(lines);
        }

        Console.Error.WriteLine("usage: launch <exe> <apiBase> <workDir> [--light|--dark|--system] | run <pid> <captureDir> <script|->");
        return 2;
    }

    private static int Launch(string exe, string api, string workDir, string theme)
    {
        Directory.CreateDirectory(workDir);
        var prefs = Path.Combine(workDir, "preferences.json");
        if (theme != "--keep")
        {
            var value = theme switch { "--light" => "Light", "--dark" => "Dark", _ => "System" };
            var existing = File.Exists(prefs) ? File.ReadAllText(prefs) : "{}";
            var node = System.Text.Json.Nodes.JsonNode.Parse(existing)!.AsObject();
            node["Theme"] = value;
            File.WriteAllText(prefs, node.ToJsonString());
        }

        // The app inherits this process's (isolated) desktop and job; the job sets its processors and priority.
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        environment["LIFESTREAM_OFFSCREEN"] = "1";
        environment["LIFESTREAM_OFFSCREEN_PIPE"] = "1";
        environment["LIFESTREAM_OFFSCREEN_LOG"] = Path.Combine(workDir, "offscreen.log");
        environment["LIFESTREAM_PREFERENCES_PATH"] = prefs;
        environment["Api__BaseAddress"] = api;
        environment["DOTNET_PROCESSOR_COUNT"] = "2";
        var pid = Isolation.StartWithoutFeedback(exe, Path.GetDirectoryName(exe)!, environment);
        File.WriteAllText(Path.Combine(workDir, "pid"), pid.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(pid);
        return 0;
    }

    private static int RunScript(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var words = Split(line).Select(Expand).ToList();
            try
            {
                Console.WriteLine($"> {line}");
                Execute(words);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"ERR {ex.GetType().Name}: {ex.Message}");
                try
                {
                    Capture("_failed");
                }
                catch (Exception inner) when (inner is not OutOfMemoryException)
                {
                    Console.WriteLine($"(capture failed: {inner.Message})");
                }

                return 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// A password for a local test account, derived from a random key kept DPAPI-protected (this Windows user only) in the work folder, so
    /// no password is ever printed, logged or written: scripts say <c>{{pw:label}}</c> and get the same value each time.
    /// </summary>
    private static string Derive(string keyFile, string label)
    {
        byte[] key;
        if (File.Exists(keyFile))
        {
            key = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(keyFile), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }
        else
        {
            key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyFile, System.Security.Cryptography.ProtectedData.Protect(key, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }

        var mac = System.Security.Cryptography.HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(label));
        return Convert.ToBase64String(mac)[..18].Replace('+', 'k').Replace('/', 'q') + "aZ1!";
    }

    private static string? _keyFile;

    /// <summary>Every derived password used in this run; masked wherever a value is printed.</summary>
    private static readonly HashSet<string> Secrets = [];

    private static string Mask(string text)
    {
        foreach (var secret in Secrets)
        {
            text = text.Replace(secret, "********", StringComparison.Ordinal);
        }

        return text;
    }

    private static string Expand(string word)
    {
        var start = word.IndexOf("{{pw:", StringComparison.Ordinal);
        if (start < 0)
        {
            return word;
        }

        var end = word.IndexOf("}}", start, StringComparison.Ordinal);
        var label = word[(start + 5)..end];
        _keyFile ??= Environment.GetEnvironmentVariable("LIFESTREAM_WALK_KEY") ?? throw new InvalidOperationException("LIFESTREAM_WALK_KEY is not set");
        var secret = Derive(_keyFile, label);
        Secrets.Add(secret);
        return word[..start] + secret + word[(end + 2)..];
    }

    private static void Execute(List<string> w)
    {
        switch (w[0])
        {
            case "echo":
                Console.WriteLine(string.Join(' ', w.Skip(1)));
                break;
            case "sleep":
                Thread.Sleep(int.Parse(w[1], CultureInfo.InvariantCulture));
                break;
            case "windows":
                foreach (var h in TopWindows(includeHidden: true))
                {
                    GetWindowRect(h, out var r);
                    Console.WriteLine($"  0x{h:X} visible={IsWindowVisible(h)} class={ClassOf(h)} title=\"{TitleOf(h)}\" rect={r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}");
                }

                break;
            case "tree":
                {
                    var depth = w.Count > 2 ? int.Parse(w[2], CultureInfo.InvariantCulture) : 40;
                    if (w.Count > 1 && w[1] != "*")
                    {
                        Dump(Find(w[1]), 0, depth);
                    }
                    else
                    {
                        foreach (var root in Roots())
                        {
                            Dump(root, 0, depth);
                        }
                    }

                    break;
                }

            case "texts":
                {
                    var roots = w.Count > 1 ? [Find(w[1])] : Roots();
                    foreach (var root in roots)
                    {
                        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                        {
                            var name = Safe(() => e.Current.Name);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                Console.WriteLine($"  {Mask(name)}");
                            }
                        }
                    }

                    break;
                }

            case "props":
                Props(Find(w[1]));
                break;
            case "invoke":
                Pattern<InvokePattern>(Find(w[1]), InvokePattern.Pattern).Invoke();
                break;
            case "toggle":
                Pattern<TogglePattern>(Find(w[1]), TogglePattern.Pattern).Toggle();
                break;
            case "select":
                Pattern<SelectionItemPattern>(Find(w[1]), SelectionItemPattern.Pattern).Select();
                break;
            case "expand":
                Pattern<ExpandCollapsePattern>(Find(w[1]), ExpandCollapsePattern.Pattern).Expand();
                break;
            case "collapse":
                Pattern<ExpandCollapsePattern>(Find(w[1]), ExpandCollapsePattern.Pattern).Collapse();
                break;
            case "scroll":
                Pattern<ScrollItemPattern>(Find(w[1]), ScrollItemPattern.Pattern).ScrollIntoView();
                break;
            case "set":
                Pattern<ValuePattern>(Find(w[1]), ValuePattern.Pattern).SetValue(w.Count > 2 ? w[2] : string.Empty);
                break;
            case "assert":
                Find(w[1]);
                Console.WriteLine("  found");
                break;
            case "wait":
                {
                    var ms = w.Count > 2 ? int.Parse(w[2], CultureInfo.InvariantCulture) : 15000;
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        if (TryFind(w[1]) is not null)
                        {
                            Console.WriteLine($"  appeared after {sw.ElapsedMilliseconds} ms");
                            break;
                        }

                        if (sw.ElapsedMilliseconds > ms)
                        {
                            throw new TimeoutException($"'{w[1]}' did not appear within {ms} ms");
                        }

                        Thread.Sleep(150);
                    }

                    break;
                }

            case "gone":
                {
                    var ms = w.Count > 2 ? int.Parse(w[2], CultureInfo.InvariantCulture) : 15000;
                    var sw = Stopwatch.StartNew();
                    while (TryFind(w[1]) is not null)
                    {
                        if (sw.ElapsedMilliseconds > ms)
                        {
                            throw new TimeoutException($"'{w[1]}' still there after {ms} ms");
                        }

                        Thread.Sleep(150);
                    }

                    Console.WriteLine($"  gone after {sw.ElapsedMilliseconds} ms");
                    break;
                }

            case "capture":
                Capture(w[1]);
                break;
            case "click":
            case "key":
            case "focus":
            case "focused":
            case "layout":
            case "size":
                {
                    var reply = Agent(string.Join(' ', w));
                    Console.WriteLine("  " + Mask(reply));
                    if (reply.StartsWith("ERR", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(reply);
                    }

                    break;
                }
            case "resize":
            case "state":
                // UIA TransformPattern.Resize put the window on the monitor (0,0) past the off-screen hook (2026-11-20); never again.
                throw new InvalidOperationException("resize/state through UI Automation can move the window onto the desktop; use 'size W H' (in-app)");
            case "close":
                {
                    var target = w.Count > 1 ? Find(w[1]) : Roots().Last();
                    Pattern<WindowPattern>(target, WindowPattern.Pattern).Close();
                    break;
                }

            default:
                throw new ArgumentException($"unknown command '{w[0]}'");
        }
    }

    /// <summary>One command to the app's in-process agent (src/Lifestream.Desktop/Services/OffscreenAgent.cs).</summary>
    private static string Agent(string line)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "lifestream-offscreen-" + _pid.ToString(CultureInfo.InvariantCulture), System.IO.Pipes.PipeDirection.InOut);
        pipe.Connect(10000);
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        writer.WriteLine(line);
        var reader = new StreamReader(pipe, leaveOpen: true);
        return reader.ReadLine() ?? "ERR no reply";
    }

    private static T Pattern<T>(AutomationElement e, AutomationPattern pattern)
        where T : BasePattern
    {
        if (e.TryGetCurrentPattern(pattern, out var p))
        {
            return (T)p;
        }

        throw new InvalidOperationException($"{Describe(e)} has no {pattern.ProgrammaticName}");
    }

    // ---- finding -----------------------------------------------------------------------------------------------------------

    private static List<AutomationElement> Roots()
    {
        var list = new List<AutomationElement>();
        foreach (var h in TopWindows(includeHidden: false))
        {
            try
            {
                list.Add(AutomationElement.FromHandle(h));
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return list;
    }

    private static AutomationElement Find(string selector) => TryFind(selector) ?? throw new InvalidOperationException($"not found: {selector}");

    private static AutomationElement? TryFind(string selector)
    {
        var parts = selector.Split(">>", StringSplitOptions.TrimEntries);
        IEnumerable<AutomationElement> scopes = Roots().AsEnumerable().Reverse(); // newest (dialogs, popups) first
        AutomationElement? found = null;
        foreach (var part in parts)
        {
            found = null;
            var (terms, index) = ParseSelector(part);
            var winTerm = terms.FirstOrDefault(t => t.Key == "win~");
            terms = terms.Where(t => t.Key != "win~").ToList();
            var matches = new List<AutomationElement>();
            foreach (var scope in scopes)
            {
                if (winTerm.Key is not null && !(Safe(() => scope.Current.Name) ?? string.Empty).Contains(winTerm.Value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AutomationElementCollection all;
                try
                {
                    all = scope.FindAll(TreeScope.Subtree, BuildCondition(terms));
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                foreach (AutomationElement e in all)
                {
                    if (Matches(e, terms))
                    {
                        matches.Add(e);
                    }
                }

                if (matches.Count > index)
                {
                    break;
                }
            }

            if (matches.Count <= index)
            {
                return null;
            }

            found = matches[index];
            scopes = [found];
        }

        return found;
    }

    private static (List<KeyValuePair<string, string>> Terms, int Index) ParseSelector(string part)
    {
        var index = 0;
        var hash = part.LastIndexOf('#');
        if (hash > 0 && int.TryParse(part[(hash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            index = n;
            part = part[..hash];
        }

        var terms = new List<KeyValuePair<string, string>>();
        foreach (var t in part.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = t.IndexOf('=');
            terms.Add(new KeyValuePair<string, string>(t[..eq], t[(eq + 1)..]));
        }

        return (terms, index);
    }

    private static Condition BuildCondition(List<KeyValuePair<string, string>> terms)
    {
        var conditions = new List<Condition>();
        foreach (var (key, value) in terms)
        {
            switch (key)
            {
                case "id":
                    conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, value));
                    break;
                case "name":
                    conditions.Add(new PropertyCondition(AutomationElement.NameProperty, value));
                    break;
                case "type":
                    conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlTypeOf(value)));
                    break;
                case "class":
                    conditions.Add(new PropertyCondition(AutomationElement.ClassNameProperty, value));
                    break;
                default:
                    break;
            }
        }

        return conditions.Count switch { 0 => Condition.TrueCondition, 1 => conditions[0], _ => new AndCondition([.. conditions]) };
    }

    private static bool Matches(AutomationElement e, List<KeyValuePair<string, string>> terms)
    {
        foreach (var (key, value) in terms)
        {
            if (key == "name~" && !(Safe(() => e.Current.Name) ?? string.Empty).Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (key == "help~" && !(Safe(() => e.Current.HelpText) ?? string.Empty).Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static ControlType ControlTypeOf(string name)
    {
        var field = typeof(ControlType).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
        return (ControlType?)field?.GetValue(null) ?? throw new ArgumentException($"unknown control type {name}");
    }

    // ---- describing ----------------------------------------------------------------------------------------------------------

    private static void Dump(AutomationElement e, int level, int depth)
    {
        Console.WriteLine(new string(' ', level * 2) + Describe(e));
        if (level >= depth)
        {
            return;
        }

        var walker = TreeWalker.ControlViewWalker;
        var child = Safe(() => walker.GetFirstChild(e));
        while (child is not null)
        {
            Dump(child, level + 1, depth);
            var c = child;
            child = Safe(() => walker.GetNextSibling(c));
        }
    }

    private static string Describe(AutomationElement e)
    {
        var sb = new StringBuilder();
        try
        {
            var c = e.Current;
            sb.Append(c.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(c.AutomationId))
            {
                sb.Append(" id=").Append(c.AutomationId);
            }

            if (!string.IsNullOrEmpty(c.Name))
            {
                sb.Append(" \"").Append(c.Name.Replace("\n", "\\n", StringComparison.Ordinal)).Append('"');
            }

            if (!string.IsNullOrEmpty(c.HelpText))
            {
                sb.Append(" help=\"").Append(c.HelpText).Append('"');
            }

            if (!c.IsEnabled)
            {
                sb.Append(" DISABLED");
            }

            if (c.HasKeyboardFocus)
            {
                sb.Append(" FOCUSED");
            }

            var r = c.BoundingRectangle;
            if (!r.IsEmpty)
            {
                sb.Append(CultureInfo.InvariantCulture, $" [{r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0}]");
            }

            if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var t))
            {
                sb.Append(" toggle=").Append(((TogglePattern)t).Current.ToggleState);
            }

            if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var s) && ((SelectionItemPattern)s).Current.IsSelected)
            {
                sb.Append(" SELECTED");
            }

            if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var x))
            {
                sb.Append(" expand=").Append(((ExpandCollapsePattern)x).Current.ExpandCollapseState);
            }

            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && !c.IsPassword)
            {
                var value = ((ValuePattern)v).Current.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    sb.Append(" value=\"").Append(value.Length > 80 ? value[..80] + "…" : value).Append('"');
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            sb.Append("(gone)");
        }

        return Mask(sb.ToString());
    }

    private static void Props(AutomationElement e)
    {
        Console.WriteLine("  " + Describe(e));
        foreach (var p in e.GetSupportedPatterns())
        {
            Console.WriteLine("  pattern " + p.ProgrammaticName);
        }

        var c = e.Current;
        Console.WriteLine($"  class={c.ClassName} offscreen={c.IsOffscreen} focusable={c.IsKeyboardFocusable} itemStatus=\"{c.ItemStatus}\" accessKey=\"{c.AccessKey}\" accel=\"{c.AcceleratorKey}\"");
    }

    private static T? Safe<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return default;
        }
    }

    private static List<string> Split(string line)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (sb.Length > 0)
                {
                    words.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 0)
        {
            words.Add(sb.ToString());
        }

        return words;
    }

    // ---- windows and capture ------------------------------------------------------------------------------------------------

    private static List<IntPtr> TopWindows(bool includeHidden)
    {
        var list = new List<IntPtr>();
        EnumWindows(
            (h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pid == _pid && (includeHidden || IsWindowVisible(h)))
                {
                    if (includeHidden || (GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top))
                    {
                        list.Add(h);
                    }
                }

                return true;
            },
            IntPtr.Zero);
        list.Reverse(); // EnumWindows is top of the z-order first; oldest-at-bottom first is the natural "main, then dialogs, then popups"
        return list;
    }

    private static void Capture(string name)
    {
        var windows = TopWindows(includeHidden: false);
        var k = 0;
        foreach (var h in windows)
        {
            if (!GetWindowRect(h, out var r))
            {
                continue;
            }

            var width = r.Right - r.Left;
            var height = r.Bottom - r.Top;
            if (width < 2 || height < 2)
            {
                continue;
            }

            var file = Path.Combine(_captures, k == 0 ? $"{name}.png" : $"{name}.{k}.png");
            SaveWindow(h, width, height, file);
            Console.WriteLine($"  captured {Path.GetFileName(file)} {width}x{height} {ClassOf(h)} \"{TitleOf(h)}\" at {r.Left},{r.Top}");
            k++;
        }

        if (k == 0)
        {
            Console.WriteLine("  (no visible window)");
        }
    }

    /// <summary>
    /// Saves what the compositor draws for the window. Off-screen and at idle priority the render thread can lag the UI thread by seconds, so the
    /// frame is taken again until two in a row are identical (at most 8 s); a frame that never settles is saved anyway and reported.
    /// </summary>
    private static void SaveWindow(IntPtr hwnd, int width, int height, string file)
    {
        var previous = Grab(hwnd, width, height);
        var settled = false;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            Thread.Sleep(350);
            var next = Grab(hwnd, width, height);
            var same = next.AsSpan().SequenceEqual(previous);
            previous = next;
            if (same)
            {
                settled = true;
                break;
            }
        }

        if (!settled)
        {
            Console.WriteLine("  (frame still changing after 8 s; saved the last one)");
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, previous, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private static byte[] Grab(IntPtr hwnd, int width, int height)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, dib);
        try
        {
            if (!PrintWindow(hwnd, dc, 2))
            {
                throw new InvalidOperationException("PrintWindow failed");
            }

            var buffer = new byte[width * 4 * height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            for (var i = 3; i < buffer.Length; i += 4)
            {
                buffer[i] = 255; // PrintWindow leaves alpha undefined
            }

            return buffer;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static unsafe string ClassOf(IntPtr h)
    {
        var sb = stackalloc char[256];
        var n = GetClassNameW(h, sb, 256);
        return new string(sb, 0, n);
    }

    private static unsafe string TitleOf(IntPtr h)
    {
        var sb = stackalloc char[512];
        var n = GetWindowTextW(h, sb, 512);
        return new string(sb, 0, n);
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumProc proc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassNameW(IntPtr hwnd, char* name, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowTextW(IntPtr hwnd, char* text, int max);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);
}
