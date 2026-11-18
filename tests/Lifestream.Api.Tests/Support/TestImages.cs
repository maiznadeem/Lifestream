using SkiaSharp;

namespace Lifestream.Api.Tests.Support;

/// <summary>Pictures generated in memory (never a real person's photo). Noise makes them large when encoded, so the 30 KB cap really has to work.</summary>
public static class TestImages
{
    public static byte[] Png(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png, 100);

    public static byte[] Jpeg(int width, int height, int quality = 95) => Encode(width, height, SKEncodedImageFormat.Jpeg, quality);

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, int quality)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(7);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }
}
