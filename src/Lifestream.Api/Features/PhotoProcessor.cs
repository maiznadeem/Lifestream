using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Data.Entities;
using SkiaSharp;

namespace Lifestream.Api.Features;

public sealed record ProcessedPhoto(byte[] Content, int Width, int Height);

/// <summary>
/// Decodes an uploaded picture and re-encodes it server-side as a JPEG of at most 256x256 pixels and 30 KB, with all metadata removed.
/// Only JPEG and PNG are accepted, over at most 1 MB; whatever the client says, the stored and served type is always image/jpeg.
/// </summary>
public sealed class PhotoProcessor
{
    public const int MaxUploadBytes = 1024 * 1024;
    private const long MaxDecodedPixels = 25_000_000;
    private static readonly string[] AllowedTypes = ["image/jpeg", "image/png"];

    public async Task<ProcessedPhoto> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        var contentType = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (contentType is null || !AllowedTypes.Contains(contentType))
        {
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, ProblemCodes.Validation, "The photo must be a JPEG or PNG image.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            if (buffer.Length > MaxUploadBytes)
            {
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, ExtraProblemCodes.PayloadTooLarge, "The photo must be 1 MB or smaller.");
            }
        }

        return Process(buffer.ToArray());
    }

    public static ProcessedPhoto Process(byte[] upload)
    {
        if (upload.Length == 0)
        {
            throw ApiException.Validation("photo", "is empty");
        }

        // The format comes from the bytes themselves, never from the declared content type.
        using var stream = new SKMemoryStream(upload);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png)
            || (long)codec.Info.Width * codec.Info.Height > MaxDecodedPixels)
        {
            throw ApiException.Validation("photo", "must be a JPEG or PNG image of reasonable size");
        }

        using var decoded = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw ApiException.Validation("photo", "is not a readable JPEG or PNG image");

        var scale = Math.Min(1.0, (double)StaffPhoto.MaxPixels / Math.Max(decoded.Width, decoded.Height));
        var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));

        for (var attempt = 0; attempt < 8; attempt++)
        {
            // Draw onto a white background so transparency never turns black, and so no source metadata can survive.
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            surface.Canvas.Clear(SKColors.White);
            using var paint = new SKPaint { IsAntialias = true };
            using var source = SKImage.FromBitmap(decoded);
            surface.Canvas.DrawImage(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            using var image = surface.Snapshot();

            foreach (var quality in new[] { 85, 70, 55, 40, 25 })
            {
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (data is not null && data.Size <= StaffPhoto.MaxBytes)
                {
                    return new ProcessedPhoto(data.ToArray(), width, height);
                }
            }

            // Still too big at the lowest quality: make the picture smaller and try again.
            width = Math.Max(16, width * 3 / 4);
            height = Math.Max(16, height * 3 / 4);
        }

        throw ApiException.Validation("photo", "could not be reduced to 30 KB");
    }
}
