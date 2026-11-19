using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Runs WPF work on a dedicated STA thread and renders elements off-screen. Elements are hosted in an
/// <see cref="HwndSource"/> whose parent is a message-only window, so they get a real presentation source (Loaded fires,
/// charts draw) but no window is ever shown on any desktop.
/// </summary>
public static class StaHost
{
    private static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA work failed: " + failure.Message, failure);
        }

        return result;
    }

    /// <summary>Hosts the element off-screen at the given size, lets it load and draw, and returns a PNG.</summary>
    public static byte[] RenderPng(FrameworkElement element, int width, int height, int settleMilliseconds = 1500)
    {
        var parameters = new HwndSourceParameters("LifestreamOffscreen", width, height)
        {
            ParentWindow = MessageOnlyParent,
            WindowStyle = 0,
        };
        using var source = new HwndSource(parameters) { RootVisual = element, SizeToContent = SizeToContent.Manual };

        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        Settle(settleMilliseconds);
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Pumps the dispatcher for the given time so Loaded handlers, bindings and chart drawing have run.</summary>
    public static void Settle(int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(16);
        }
    }

    public static int CountDistinctColours(byte[] png) => Pixels(png).Distinct().Count();

    /// <summary>Counts pixels within the given per-channel tolerance of an RGB colour.</summary>
    public static int CountPixelsNear(byte[] png, byte red, byte green, byte blue, int tolerance)
    {
        return Pixels(png).Count(p =>
            Math.Abs(((p >> 16) & 0xFF) - red) <= tolerance
            && Math.Abs(((p >> 8) & 0xFF) - green) <= tolerance
            && Math.Abs((p & 0xFF) - blue) <= tolerance);
    }

    private static List<int> Pixels(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var data = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(data, frame.PixelWidth * 4, 0);

        var pixels = new List<int>(data.Length / 4);
        for (var i = 0; i < data.Length; i += 4)
        {
            pixels.Add(BitConverter.ToInt32(data, i));
        }

        return pixels;
    }
}
