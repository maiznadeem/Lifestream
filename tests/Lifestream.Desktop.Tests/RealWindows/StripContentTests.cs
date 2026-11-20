namespace Lifestream.Desktop.Tests.RealWindows;

/// <summary>
/// The rule behind "nothing is drawn in the strip a maximized window pushes off the screen" (RealWindowTests.MaximizedLayout_...): a strip
/// pixel is content when it differs by more than the anti-aliasing tolerance from the same pixel of the window with its content hidden. No
/// window is needed: these are the captures' pixels.
/// </summary>
public sealed class StripContentTests
{
    [Fact]
    public void ABackgroundThatIsNotOneColour_IsNotContent()
    {
        // A gradient drawn in software is dithered: neighbouring pixels differ by a little, and the same pixel is the same in both captures.
        var background = Capture(40, 8, (x, y) => ((byte)(240 + ((x + y) % 4)), 241, (byte)(244 - (x % 3))));

        Assert.Equal(0, ButtonArea.ContentOver(background, background).Count);
        Assert.Equal(0, ButtonArea.ContentOver(Capture(40, 8, (x, y) => ((byte)(241 + ((x + y) % 4)), 242, (byte)(243 - (x % 3)))), background).Count);
    }

    [Fact]
    public void AControlInTheStrip_IsContent_AndTheFirstPixelIsNamed()
    {
        var background = Capture(40, 8, (x, y) => ((byte)(240 + ((x + y) % 4)), 241, 244));
        var planted = Capture(40, 8, (x, y) => x is >= 10 and < 14 && y >= 5 ? ((byte)32, (byte)32, (byte)32) : background.At(x, y)); // the top of a dark button

        var (count, first) = ButtonArea.ContentOver(planted, background);

        Assert.Equal(12, count);
        Assert.StartsWith("10,5 = ", first, StringComparison.Ordinal);
    }

    [Fact]
    public void FaintContent_JustOverTheTolerance_IsContent()
    {
        var background = Capture(4, 2, (_, _) => (200, 200, 200));

        Assert.Equal(0, ButtonArea.ContentOver(Capture(4, 2, (_, _) => (224, 200, 200)), background).Count);
        Assert.Equal(8, ButtonArea.ContentOver(Capture(4, 2, (_, _) => (200, 175, 200)), background).Count);
    }

    private static WindowCapture Capture(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = colour(x, y);
                var i = ((y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        }

        return new WindowCapture(width, height, pixels);
    }
}
