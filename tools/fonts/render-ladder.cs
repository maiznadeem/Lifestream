#:property TargetFramework=net11.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false

// Renders a size ladder of candidate fonts with WPF's own text stack, off-screen (no window), so a person can judge crispness at
// 12 to 16 px. Each row is one font file at one size, in Display and Ideal text formatting, on the light and the dark canvas.
// Usage: dotnet run tools/fonts/render-ladder.cs -- <out.png> <label=path/to/font.ttf#Family> [...]
// The PNG is drawn at 1:1 and then repeated at 3x (nearest neighbour) so stem snapping is visible.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var output = args[0];
var faces = args.Skip(1).Select(a => a.Split('=', 2)).Select(p => (Label: p[0], Spec: p[1])).ToList();
var sizes = new[] { 12.0, 13, 14, 15, 16, 20, 28 };
var weights = new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold };
const string sample = "Hana Greenleaf 0175 O− AB+ 11:30 Record donation";
var light = (Bg: Color.FromRgb(0xFF, 0xFF, 0xFF), Fg: Color.FromRgb(0x12, 0x18, 0x20));
var dark = (Bg: Color.FromRgb(0x19, 0x1D, 0x23), Fg: Color.FromRgb(0xED, 0xF0, 0xF4));
const int columnWidth = 560;

var thread = new Thread(() =>
{
    var rows = new List<(string Label, FontFamily Family, double Size)>();
    foreach (var (label, spec) in faces)
    {
        var hash = spec.LastIndexOf('#');
        var folder = new Uri(Path.GetFullPath(Path.GetDirectoryName(spec[..hash])!) + Path.DirectorySeparatorChar);
        var family = new FontFamily(folder, "./#" + spec[(hash + 1)..]);
        foreach (var size in sizes)
        {
            rows.Add((label, family, size));
        }
    }

    double RowHeight(double size) => Math.Ceiling(size * 1.45) + 6;
    var height = (int)rows.Sum(r => RowHeight(r.Size) * weights.Length + 8) + 40;
    var visual = new DrawingVisual();
    TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
    using (var dc = visual.RenderOpen())
    {
        var x = 0;
        foreach (var mode in new[] { TextFormattingMode.Display, TextFormattingMode.Ideal })
        {
            foreach (var scheme in new[] { light, dark })
            {
                dc.DrawRectangle(new SolidColorBrush(scheme.Bg), null, new Rect(x, 0, columnWidth, height));
                var caption = new FormattedText($"{mode} formatting", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, new SolidColorBrush(scheme.Fg), null, mode, 1.0);
                dc.DrawText(caption, new Point(x + 8, 10));
                double y = 36;
                foreach (var (label, family, size) in rows)
                {
                    foreach (var weight in weights)
                    {
                        var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
                        var text = new FormattedText($"{label} {size:0}/{weight.ToOpenTypeWeight()}  {sample}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(scheme.Fg), null, mode, 1.0);
                        dc.DrawText(text, new Point(x + 8, Math.Round(y)));
                        y += RowHeight(size);
                    }

                    y += 8;
                }

                x += columnWidth;
            }
        }
    }

    var width = columnWidth * 4;
    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    Save(bitmap, output);
    var zoom = new TransformedBitmap(new CroppedBitmap(bitmap, new Int32Rect(0, 0, columnWidth * 2, Math.Min(height, 900))), new ScaleTransform(3, 3));
    var nearest = new RenderTargetBitmap(columnWidth * 6, Math.Min(height, 900) * 3, 96, 96, PixelFormats.Pbgra32);
    var image = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
    using (var dc = image.RenderOpen())
    {
        dc.DrawImage(zoom, new Rect(0, 0, columnWidth * 6, Math.Min(height, 900) * 3));
    }

    nearest.Render(image);
    Save(nearest, Path.ChangeExtension(output, ".zoom3x.png"));
    Console.WriteLine($"{output} {width}x{height}");
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static void Save(BitmapSource bitmap, string path)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}
