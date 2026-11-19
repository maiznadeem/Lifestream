using System.Globalization;
using System.Windows.Data;
using Lifestream.Desktop.Services;

namespace Lifestream.Desktop.Features.Staff;

/// <summary>
/// A person's photo bytes as an image for <see cref="Controls.Avatar"/>, or null while there is none (or the bytes are not an image), so the avatar shows
/// the person's initials instead. Decoded small (<see cref="AvatarConverter.DecodePixelWidth"/>) and frozen, like every avatar.
/// </summary>
public sealed class PhotoSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is byte[] { Length: > 0 } bytes ? PhotoDecoder.Decode(bytes, AvatarConverter.DecodePixelWidth) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
