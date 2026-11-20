using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lifestream.Desktop.Services;

/// <summary>
/// Finds the view of a view model by naming convention (<c>DonorsViewModel</c> to <c>DonorsView</c>, in any namespace of this
/// assembly), so a feature adds its screen without touching the shell. The view is created once per visit and becomes the content
/// of the page host; the host sets the DataContext, the view never does (SHELL-13).
/// </summary>
public sealed class ViewLocator : DataTemplateSelector
{
    private static readonly Dictionary<Type, Type?> Cache = [];

    public static ViewLocator Instance { get; } = new();

    /// <summary>The view type for a view model type, or null.</summary>
    public static Type? Resolve(Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        lock (Cache)
        {
            if (Cache.TryGetValue(viewModelType, out var cached))
            {
                return cached;
            }

            Type? found = null;
            for (var type = viewModelType; type is not null && found is null && type != typeof(object); type = type.BaseType)
            {
                var name = type.Name.EndsWith("ViewModel", StringComparison.Ordinal) ? type.Name[..^"ViewModel".Length] + "View" : null;
                if (name is null)
                {
                    continue;
                }

                found = typeof(ViewLocator).Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == name && typeof(FrameworkElement).IsAssignableFrom(t) && !t.IsAbstract);
            }

            Cache[viewModelType] = found;
            return found;
        }
    }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is null || Resolve(item.GetType()) is not { } viewType)
        {
            return null;
        }

        return new DataTemplate { VisualTree = new FrameworkElementFactory(viewType) };
    }
}

/// <summary>Turns a false boolean into Visible and a true one into Collapsed.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is the number zero (the "nothing here" line under an empty list), collapsed otherwise.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is a non-empty string.</summary>
public sealed class HasTextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Display defaults for avatars live in the view (SHELL-42): a missing photo shows one shared, frozen drawing from the theme
/// (<c>Image.DefaultAvatar</c>), loaded once. The default is never written into a view model or sent to the API.
/// </summary>
public static class DefaultAvatar
{
    /// <summary>The shared default avatar: the same frozen instance on every call.</summary>
    public static ImageSource Image => (ImageSource)Theme.ThemeLoader.Resources["Image.DefaultAvatar"];
}

/// <summary>Photo bytes to an image decoded at a limited size (SHELL-43); no bytes means the shared default avatar.</summary>
public sealed class AvatarConverter : IValueConverter
{
    /// <summary>The widest a photo is decoded, in pixels: shown at 28 to 96 DIP, so 192 is enough at 200 percent scale.</summary>
    public const int DecodePixelWidth = 192;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is byte[] { Length: > 0 } bytes ? PhotoDecoder.Decode(bytes, DecodePixelWidth) ?? DefaultAvatar.Image : DefaultAvatar.Image;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class PhotoDecoder
{
    /// <summary>Decodes at most <paramref name="decodePixelWidth"/> pixels wide (DecodePixelWidth), frozen; null when the bytes are not an image.</summary>
    public static BitmapSource? Decode(byte[] bytes, int decodePixelWidth)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
