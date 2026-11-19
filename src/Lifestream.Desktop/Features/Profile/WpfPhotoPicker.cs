using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Profile;
using Microsoft.Win32;

namespace Lifestream.Desktop.Features.Profile;

/// <summary>
/// Asks for a picture file and returns it as a small JPEG (at most 256 pixels on its longest side, which is all the profile shows and keeps the upload far below
/// the API's 1 MB limit). A file that is not a readable picture is reported in a dialog; cancelling returns null.
/// </summary>
public sealed class WpfPhotoPicker(IDialogService dialogs) : IPhotoPicker
{
    public const int MaxSide = 256;

    private const long MaxFileBytes = 25 * 1024 * 1024;

    public async Task<byte[]?> PickAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a photo",
            Filter = "Pictures (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
        {
            return null;
        }

        var jpeg = await Task.Run(() => TryConvert(dialog.FileName)).ConfigureAwait(true);
        if (jpeg is null)
        {
            await dialogs.ShowErrorAsync("That file can't be used", "Choose a JPEG, PNG or BMP picture that is not damaged.").ConfigureAwait(true);
        }

        return jpeg;
    }

    /// <summary>The JPEG bytes of a picture file scaled to <see cref="MaxSide"/>, or null when the file is not a readable picture.</summary>
    public static byte[]? TryConvert(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return null;
            }

            using var input = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            var scale = Math.Min(1.0, MaxSide / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
            if (scale < 1.0)
            {
                frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
