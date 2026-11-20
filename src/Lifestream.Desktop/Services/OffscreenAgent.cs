using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lifestream.Desktop.Services;

/// <summary>
/// Test-only (with <see cref="OffscreenMode"/>): what UI Automation cannot do without real input, done inside the process and never through the
/// desktop. With <c>LIFESTREAM_OFFSCREEN_PIPE=1</c> the driver (tests/realapp) sends one line per command over the pipe <c>lifestream-offscreen-&lt;pid&gt;</c>:
/// <list type="bullet">
/// <item><c>click SELECTOR</c>: the button's own click (ButtonBase.OnClick: Click event and Command), what a mouse click on it ends in;</item>
/// <item><c>key GESTURE [SELECTOR]</c>: PreviewKeyDown/KeyDown/PreviewKeyUp/KeyUp routed to the element (or to the focused element, or the
/// window), with the modifiers set in this UI thread's own key-state table (SetKeyboardState is per thread; the desktop and other programs see
/// nothing), so key bindings, Esc, Enter and arrow handling run their real code;</item>
/// <item><c>focus SELECTOR</c>: Keyboard.Focus on the element (logical focus inside a window that is never activated).</item>
/// </list>
/// Not the same as real input: no hit testing, no mouse capture or hover, no IsKeyboardFocusVisible from a real keyboard. Say so where it matters.
/// </summary>
internal static partial class OffscreenAgent
{
    public static void Start()
    {
        if (!OffscreenMode.Enabled || Environment.GetEnvironmentVariable("LIFESTREAM_OFFSCREEN_PIPE") != "1")
        {
            return;
        }

        var pipe = PipeName(Environment.ProcessId);

        var dispatcher = Application.Current.Dispatcher;
        var thread = new Thread(() => Serve(pipe, dispatcher)) { IsBackground = true, Name = "offscreen-agent" };
        thread.Start();
    }

    /// <summary>The pipe of the app process with this id.</summary>
    public static string PipeName(int processId) => "lifestream-offscreen-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void Serve(string name, System.Windows.Threading.Dispatcher dispatcher)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };
                var line = reader.ReadLine() ?? string.Empty;
                string reply;
                try
                {
                    reply = dispatcher.Invoke(() => Execute(line));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    reply = "ERR " + ex.GetBaseException().Message;
                }

                writer.WriteLine(reply);
            }
            catch (IOException)
            {
                // the driver went away mid-command; wait for the next one
            }
        }
    }

    private static string Execute(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var verb = space < 0 ? line : line[..space];
        var rest = space < 0 ? string.Empty : line[(space + 1)..].Trim();
        switch (verb)
        {
            case "click":
                {
                    var element = Find(rest) ?? throw new InvalidOperationException("not found: " + rest);
                    if (element is not ButtonBase button)
                    {
                        throw new InvalidOperationException($"{element.GetType().Name} is not a button");
                    }

                    if (!button.IsEnabled)
                    {
                        throw new InvalidOperationException("the button is disabled");
                    }

                    // Queued, not run here: a click that opens a modal window would otherwise hold this command until the window closes.
                    var onClick = typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    button.Dispatcher.BeginInvoke(() => onClick.Invoke(button, null));
                    return "ok clicked " + Describe(button);
                }

            case "focus":
                {
                    var element = Find(rest) ?? throw new InvalidOperationException("not found: " + rest);
                    return "ok focus=" + Keyboard.Focus((IInputElement)element);
                }

            case "key":
                {
                    var parts = rest.Split(' ', 2);
                    var gesture = parts[0];
                    var target = parts.Length > 1 ? Find(parts[1]) ?? throw new InvalidOperationException("not found: " + parts[1]) : null;
                    return Press(gesture, target);
                }

            case "size":
                {
                    // Sizes the main window from inside the app (WPF sets the size with SWP_NOMOVE), never through the desktop.
                    var size = rest.Split(' ');
                    var window = MainWindow();
                    window.WindowState = WindowState.Normal;
                    window.Width = double.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture);
                    window.Height = double.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture);
                    return $"ok size {window.ActualWidth}x{window.ActualHeight} at {window.Left},{window.Top}";
                }

            case "debug":
                {
                    var fields = new List<string>();
                    for (var t = Keyboard.PrimaryDevice.GetType(); t is not null; t = t.BaseType)
                    {
                        fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => $"{t.Name}.{f.Name}:{f.FieldType.Name}={f.GetValue(Keyboard.PrimaryDevice)}"));
                    }

                    return "ok " + string.Join(" | ", fields);
                }

            case "focused":
                return "ok " + (Keyboard.FocusedElement is DependencyObject d ? Describe(d) : "(none)");
            default:
                throw new InvalidOperationException("unknown verb " + verb);
        }
    }

    private static string Press(string gesture, DependencyObject? target)
    {
        var tokens = gesture.Split('+');
        var key = ParseKey(tokens[^1]);
        var modifiers = ModifierKeys.None;
        foreach (var m in tokens[..^1])
        {
            modifiers |= m.ToUpperInvariant() switch
            {
                "CTRL" => ModifierKeys.Control,
                "ALT" => ModifierKeys.Alt,
                "SHIFT" => ModifierKeys.Shift,
                _ => throw new InvalidOperationException("unknown modifier " + m),
            };
        }

        var element = target as UIElement ?? Keyboard.FocusedElement as UIElement ?? MainWindow();
        var source = PresentationSource.FromVisual(element) ?? throw new InvalidOperationException("the element is not shown");
        if (modifiers != ModifierKeys.None)
        {
            // A shortcut: run the key binding a real key press would match, from the element up (WPF reads modifier keys from the system only
            // while one of its windows is active, which these never are).
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            {
                if (node is not UIElement ui)
                {
                    continue;
                }

                foreach (var binding in ui.InputBindings.OfType<KeyBinding>())
                {
                    if (binding.Gesture is KeyGesture gesture1 && gesture1.Key == key && gesture1.Modifiers == modifiers && binding.Command is { } command)
                    {
                        var target1 = binding.CommandTarget ?? ui;
                        if (command is RoutedCommand routed)
                        {
                            if (!routed.CanExecute(binding.CommandParameter, target1))
                            {
                                return $"ok key {gesture} matched on {Describe(ui)} but cannot execute now";
                            }

                            ui.Dispatcher.BeginInvoke(() => routed.Execute(binding.CommandParameter, target1));
                        }
                        else
                        {
                            if (!command.CanExecute(binding.CommandParameter))
                            {
                                return $"ok key {gesture} matched on {Describe(ui)} but cannot execute now";
                            }

                            ui.Dispatcher.BeginInvoke(() => command.Execute(binding.CommandParameter));
                        }

                        return $"ok key {gesture} ran the binding on {Describe(ui)}";
                    }
                }
            }

            return $"ok key {gesture}: no key binding matched from {Describe(element)} up";
        }

        var state = new byte[256];
        _ = GetKeyboardState(state);
        var saved = (byte[])state.Clone();
        try
        {
            SetModifier(state, 0x11, modifiers.HasFlag(ModifierKeys.Control));
            SetModifier(state, 0x12, modifiers.HasFlag(ModifierKeys.Alt));
            SetModifier(state, 0x10, modifiers.HasFlag(ModifierKeys.Shift));
            _ = SetKeyboardState(state);
            // WPF reads the key states from the system only while one of its windows is the keyboard's active source, which a window that is
            // never activated is not; point the keyboard at this window for the duration of the key press.
            var restore = SetActiveSource(source);
            try
            {
                var modifiersSeen = Keyboard.Modifiers;
                var handled = Raise(element, source, key, Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent);
                Raise(element, source, key, Keyboard.PreviewKeyUpEvent, Keyboard.KeyUpEvent);
                return $"ok key {gesture} to {Describe(element)} handled={handled} modifiers={modifiersSeen}";
            }
            finally
            {
                restore();
            }
        }
        finally
        {
            _ = SetKeyboardState(saved);
        }
    }

    private static Action SetActiveSource(PresentationSource source)
    {
        var device = Keyboard.PrimaryDevice;
        for (var t = device.GetType(); t is not null; t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(PresentationSource))
                {
                    var old = field.GetValue(device);
                    field.SetValue(device, source);
                    return () => field.SetValue(device, old);
                }

                if (field.FieldType.IsGenericType && field.FieldType.GetGenericArguments() is [var arg] && arg == typeof(PresentationSource))
                {
                    var wrapper = field.GetValue(device);
                    var created = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [source], null);
                    field.SetValue(device, created);
                    return () => field.SetValue(device, wrapper);
                }
            }
        }

        return () => { };
    }

    private static bool Raise(UIElement element, PresentationSource source, Key key, RoutedEvent preview, RoutedEvent bubble)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = preview };
        element.RaiseEvent(args);
        if (args.Handled)
        {
            return true;
        }

        args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = bubble };
        element.RaiseEvent(args);
        return args.Handled;
    }

    private static void SetModifier(byte[] state, int vk, bool down)
    {
        state[vk] = down ? (byte)0x80 : (byte)0;
        var left = vk switch { 0x11 => 0xA2, 0x12 => 0xA4, _ => 0xA0 };
        state[left] = down ? (byte)0x80 : (byte)0;
    }

    private static Key ParseKey(string text) => text.ToUpperInvariant() switch
    {
        "ESC" => Key.Escape,
        "ENTER" => Key.Enter,
        "UP" => Key.Up,
        "DOWN" => Key.Down,
        "TAB" => Key.Tab,
        "SPACE" => Key.Space,
        _ when text.Length == 1 && char.IsDigit(text[0]) => Key.D0 + (text[0] - '0'),
        _ => Enum.Parse<Key>(text, ignoreCase: true),
    };

    private static Window MainWindow() =>
        Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).OrderByDescending(w => w.ActualWidth * w.ActualHeight).FirstOrDefault()
        ?? throw new InvalidOperationException("no window");

    /// <summary>id=AutomationId or x:Name, name=automation name, name~=part of it, type=WPF type name; '#n' the n-th match; newest window first.</summary>
    private static DependencyObject? Find(string selector)
    {
        var index = 0;
        var hash = selector.LastIndexOf('#');
        if (hash > 0 && int.TryParse(selector[(hash + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            index = n;
            selector = selector[..hash];
        }

        var terms = selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => (Key: t[..t.IndexOf('=', StringComparison.Ordinal)], Value: t[(t.IndexOf('=', StringComparison.Ordinal) + 1)..])).ToList();
        var roots = PresentationSource.CurrentSources.OfType<PresentationSource>().Select(s => s.RootVisual).OfType<Visual>().Reverse().ToList();
        var matches = new List<DependencyObject>();
        foreach (var root in roots)
        {
            Walk(root, d =>
            {
                if (d is UIElement { IsVisible: true } && terms.All(t => Matches(d, t.Key, t.Value)))
                {
                    matches.Add(d);
                }
            });
            if (matches.Count > index)
            {
                break;
            }
        }

        return matches.Count > index ? matches[index] : null;
    }

    private static bool Matches(DependencyObject d, string key, string value) => key switch
    {
        "id" => System.Windows.Automation.AutomationProperties.GetAutomationId(d) == value || (d is FrameworkElement f && f.Name == value),
        "name" => NameOf(d) == value,
        "name~" => NameOf(d)?.Contains(value, StringComparison.OrdinalIgnoreCase) == true,
        "type" => d.GetType().Name == value,
        _ => false,
    };

    private static string? NameOf(DependencyObject d)
    {
        if (d is not UIElement e)
        {
            return null;
        }

        var explicitName = System.Windows.Automation.AutomationProperties.GetName(e);
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        try
        {
            return UIElementAutomationPeer.CreatePeerForElement(e)?.GetName();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void Walk(DependencyObject node, Action<DependencyObject> visit)
    {
        visit(node);
        var count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), visit);
        }
    }

    private static string Describe(DependencyObject d) =>
        $"{d.GetType().Name} id={System.Windows.Automation.AutomationProperties.GetAutomationId(d)}{(d is FrameworkElement f && f.Name.Length > 0 ? "/" + f.Name : string.Empty)} \"{NameOf(d)}\"";

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState([Out] byte[] state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKeyboardState(byte[] state);
}
