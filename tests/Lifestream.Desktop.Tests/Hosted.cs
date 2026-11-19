using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// An element hosted off-screen with the app theme: a real presentation source (so Loaded fires and templates apply) whose parent is
/// a message-only window, so nothing is ever shown on any desktop. Use inside <see cref="StaHost.Run{T}"/>.
/// </summary>
public sealed class Hosted : IDisposable
{
    private readonly HwndSource _source;

    private Hosted(FrameworkElement element, HwndSource source)
    {
        Element = element;
        _source = source;
    }

    public FrameworkElement Element { get; }

    public static string CaptureDirectory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "captures");

    public static Hosted Create(FrameworkElement element, double width, double height, bool withTheme = true, bool animations = false)
    {
        MotionSettings.Override = animations;
        if (withTheme)
        {
            element.Resources.MergedDictionaries.Add(ThemeLoader.Resources);
        }

        element.UseLayoutRounding = true; // as every window of the app (Chrome.xaml): edges land on whole device pixels
        var parameters = new HwndSourceParameters("LifestreamHosted", (int)Math.Ceiling(width), (int)Math.Ceiling(height))
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
        };
        var source = new HwndSource(parameters) { RootVisual = element, SizeToContent = SizeToContent.Manual };
        var hosted = new Hosted(element, source);
        hosted.Layout(width, height);
        return hosted;
    }

    public void Layout(double width, double height)
    {
        Element.Measure(new Size(width, height));
        Element.Arrange(new Rect(0, 0, width, height));
        Element.UpdateLayout();
    }

    /// <summary>Lets bindings, templates, Loaded handlers and storyboards run.</summary>
    public void Settle(int milliseconds = 150)
    {
        StaHost.Settle(milliseconds);
        Element.UpdateLayout();
    }

    public byte[] Capture(string? fileName = null, double? width = null, double? height = null)
    {
        var w = (int)Math.Ceiling(width ?? Element.ActualWidth);
        var h = (int)Math.Ceiling(height ?? Element.ActualHeight);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var png = stream.ToArray();
        if (fileName is not null)
        {
            Directory.CreateDirectory(CaptureDirectory);
            File.WriteAllBytes(Path.Combine(CaptureDirectory, fileName), png);
        }

        return png;
    }

    /// <summary>Raw BGRA pixels of the element.</summary>
    public byte[] Pixels()
    {
        var png = Capture();
        using var stream = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var data = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(data, frame.PixelWidth * 4, 0);
        return data;
    }

    /// <summary>Finds a named element in the template or logical tree.</summary>
    public T Find<T>(string name)
        where T : FrameworkElement => Descendants(Element).OfType<T>().FirstOrDefault(e => e.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    public IEnumerable<T> All<T>()
        where T : DependencyObject => Descendants(Element).OfType<T>();

    /// <summary>Every visual descendant, also looking inside popups' logical content when asked.</summary>
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var inner in Descendants(child))
            {
                yield return inner;
            }
        }
    }

    public static Rect BoundsOf(FrameworkElement element, Visual relativeTo) =>
        element.TransformToAncestor(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    public void Dispose() => _source.Dispose();

    /// <summary>Pumps the dispatcher until the condition is true or the time runs out; returns whether it became true.</summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            StaHost.Settle(16);
        }

        return condition();
    }

    /// <summary>The number of pixels that differ between two captures of the same size.</summary>
    public static int Differences(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return int.MaxValue;
        }

        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Convenience: a Border-less grid the size of the hosted area for placing a control under test.</summary>
    public static Grid Stage(UIElement child, Brush? background = null)
    {
        var grid = new Grid { Background = background };
        grid.Children.Add(child);
        return grid;
    }

    public static void RunDispatcherFor(int milliseconds) => StaHost.Settle(milliseconds);

    /// <summary>
    /// Starts async work from the UI thread (so the dispatcher's synchronization context is in place, as it is for a click) and pumps
    /// the dispatcher without blocking it until the work finishes.
    /// </summary>
    public static void Await(Func<Task> work, int timeoutMilliseconds = 5000)
    {
        Task? task = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(() => task = work());
        var frame = new DispatcherFrame();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var check = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        check.Tick += (_, _) =>
        {
            if (task is { IsCompleted: true } || started.ElapsedMilliseconds > timeoutMilliseconds)
            {
                check.Stop();
                frame.Continue = false;
            }
        };
        check.Start();
        Dispatcher.PushFrame(frame);

        // Past the timeout the work is still pumped (never blocked on: its continuations need this thread), up to a minute, then the test fails instead of hanging.
        if (task is { IsCompleted: false } && !PumpUntil(() => task.IsCompleted, 60_000))
        {
            throw new TimeoutException($"The awaited work did not finish within {timeoutMilliseconds} ms and a minute more.");
        }

        task!.GetAwaiter().GetResult();
    }

    public static Dispatcher CurrentDispatcher => Dispatcher.CurrentDispatcher;
}
