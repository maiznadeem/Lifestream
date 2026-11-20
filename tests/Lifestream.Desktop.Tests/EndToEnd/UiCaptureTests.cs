using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Services;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// Visual review captures (sign-in, its error state, dashboard, a list, a form dialog, and every button and input state) over the fake API, plus a report of the
/// font every text of the sign-in screen resolved to. Runs only when <c>LIFESTREAM_UI_CAPTURE_DIR</c> is set, so it can be pointed at a copy of the test output whose
/// <c>Lifestream.dll</c> is the published (ReadyToRun) one: what ships, not what <c>dotnet test</c> builds.
/// </summary>
[Collection("Wpf")]
public sealed class UiCaptureTests
{
    [Fact]
    [Trait("Category", "Captures")]
    public void Capture_EveryReviewSurface()
    {
        var dir = Environment.GetEnvironmentVariable("LIFESTREAM_UI_CAPTURE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "LIFESTREAM_UI_CAPTURE_DIR is not set");
        Directory.CreateDirectory(dir!);
        void Save(Hosted host, string name) => File.WriteAllBytes(Path.Combine(dir!, name + ".png"), host.Capture());

        StaHost.Run(() =>
        {
            var report = new StringBuilder();
            report.AppendLine(CultureInfo.InvariantCulture, $"Lifestream.dll: {typeof(LoginView).Assembly.Location}");

            using (var rig = new Rig(dispatcher: WpfTestUi.Dispatcher()))
            {
                var vm = rig.Get<LoginViewModel>();
                using var host = Hosted.Create(new LoginView { DataContext = vm }, 1100, 720);
                host.Settle(300);
                Save(host, "login");
                foreach (var text in host.All<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)))
                {
                    report.AppendLine(Describe(text));
                }

                rig.Api.On("LoginAsync", FakeApiClient.Fail<TokenResponse>(401, "invalid_credentials"));
                vm.Username = "nadeem";
                vm.Password = TestSecrets.Pw;
                Hosted.Await(() => vm.SignInCommand.ExecuteAsync(null));
                host.Settle(300);
                Save(host, "login-error");
            }

            using (var rig = FeatureHarness.NewRig())
            {
                rig.SignIn(Role.Admin);
                var shell = rig.Get<ShellViewModel>();
                using var host = Hosted.Create(new ShellView { DataContext = shell }, 1366, 768);
                Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == "Dashboard").Destination));
                host.Settle(1500);
                Save(host, "dashboard");
                Hosted.Await(() => shell.OpenAsync(shell.Items.Single(i => i.Destination.Title == "Donors").Destination));
                host.Settle(800);
                var list = (DonorsViewModel)shell.CurrentPage!;
                list.SelectedItem = list.Items[0];
                host.Settle(800);
                Save(host, "list");
                Hosted.Await(() => list.AddCommand.ExecuteAsync(null));
                host.Settle(600);
                Save(host, "form");
            }

            using (var rig = new Rig(dispatcher: WpfTestUi.Dispatcher()))
            {
                using var vm = new ChangePasswordDialogViewModel(rig.Get<ILifestreamApiClient>());
                vm.CurrentPassword = TestSecrets.Pw;
                var frame = new ContentControl { Content = vm, ContentTemplateSelector = ViewLocator.Instance, Width = 560 };
                frame.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceRaised");
                var border = new Border { Child = frame };
                border.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
                using var host = Hosted.Create(border, 560, 460);
                host.Settle(400);
                Save(host, "dialog");
            }

            using (var host = Hosted.Create(Gallery(), 1000, 480))
            {
                host.Settle(200);
                foreach (var b in host.All<Button>())
                {
                    switch (b.Tag as string)
                    {
                        case "hover": VisualStateManager.GoToState(b, "MouseOver", false); break;
                        case "pressed": VisualStateManager.GoToState(b, "Pressed", false); break;
                        case "focus": FocusAssist.SetShowRing(b, true); break;
                    }
                }

                foreach (var t in host.All<TextBox>().Where(t => t.Tag as string == "focus"))
                {
                    t.Focus();
                    FocusAssist.SetShowRing(t, true);
                }

                host.Settle(300);
                Save(host, "controls");
            }

            File.WriteAllText(Path.Combine(dir!, "fonts.txt"), report.ToString());
            return 0;
        });
    }

    /// <summary>
    /// Corner and edge crops of a button, an input and a card rendered at 100, 125, 150 and 175 % display scaling (layout rounding on, as in a
    /// window), each 16x16 device pixels blown up 8x with nearest-neighbour sampling, so soft (half-pixel) strokes are visible. Writes
    /// <c>corners.png</c> into <c>LIFESTREAM_UI_CAPTURE_DIR</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "Captures")]
    public void Capture_CornersAtEveryScale()
    {
        var dir = Environment.GetEnvironmentVariable("LIFESTREAM_UI_CAPTURE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "LIFESTREAM_UI_CAPTURE_DIR is not set");
        Directory.CreateDirectory(dir!);
        StaHost.Run(() =>
        {
            double[] scales = [1.0, 1.25, 1.5, 1.75];
            const int Crop = 16, Zoom = 8, Gap = 8;
            var columns = 6; // button corner, button edge, input corner, input edge, card corner, card edge
            var sheet = new byte[(columns * ((Crop * Zoom) + Gap)) * (scales.Length * ((Crop * Zoom) + Gap)) * 4];
            var sheetWidth = columns * ((Crop * Zoom) + Gap);
            var row = 0;
            foreach (var scale in scales)
            {
                var canvas = new Canvas { Width = 320, Height = 160, UseLayoutRounding = true };
                canvas.SetResourceReference(Panel.BackgroundProperty, "Brush.SurfaceCanvas");
                var button = new Button { Content = "Cancel", Width = 140 };
                button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
                var box = new TextBox { Text = "Ayşe", Width = 140 };
                var card = new Card { Width = 120, Height = 110, BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderCard");
                card.SetResourceReference(Border.CornerRadiusProperty, "Radius.Card");
                Canvas.SetLeft(button, 21); Canvas.SetTop(button, 21);
                Canvas.SetLeft(box, 21); Canvas.SetTop(box, 91);
                Canvas.SetLeft(card, 181); Canvas.SetTop(card, 21);
                canvas.Children.Add(button); canvas.Children.Add(box); canvas.Children.Add(card);
                // A standalone tree whose root DPI is the scale, as a window on a monitor at that scale has: layout rounding then snaps to that
                // monitor's device pixels (an HwndSource here would lay out at the system DPI and only the bitmap would be scaled).
                canvas.Resources.MergedDictionaries.Add(Lifestream.Desktop.Theme.ThemeLoader.Resources);
                VisualTreeHelper.SetRootDpi(canvas, new DpiScale(scale, scale));
                canvas.Measure(new Size(320, 160));
                canvas.Arrange(new Rect(0, 0, 320, 160));
                canvas.UpdateLayout();
                StaHost.Settle(100);
                var w = (int)Math.Round(320 * scale);
                var h = (int)Math.Round(160 * scale);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(canvas);
                var pixels = new byte[w * h * 4];
                bitmap.CopyPixels(pixels, w * 4, 0);
                var col = 0;
                foreach (var element in new FrameworkElement[] { button, box, card })
                {
                    var origin = element.TranslatePoint(new Point(0, 0), canvas);
                    var px = (int)Math.Floor(origin.X * scale) - 3;
                    var py = (int)Math.Floor(origin.Y * scale) - 3;
                    var mid = (int)Math.Floor((origin.Y + (element.ActualHeight / 2)) * scale) - (Crop / 2);
                    foreach (var (cx, cy) in new[] { (px, py), (px, mid) })
                    {
                        for (var y = 0; y < Crop * Zoom; y++)
                        {
                            for (var x = 0; x < Crop * Zoom; x++)
                            {
                                var sx = Math.Clamp(cx + (x / Zoom), 0, w - 1);
                                var sy = Math.Clamp(cy + (y / Zoom), 0, h - 1);
                                var dx = (col * ((Crop * Zoom) + Gap)) + x;
                                var dy = (row * ((Crop * Zoom) + Gap)) + y;
                                Array.Copy(pixels, ((sy * w) + sx) * 4, sheet, ((dy * sheetWidth) + dx) * 4, 4);
                            }
                        }

                        col++;
                    }
                }

                row++;
            }

            var sheetHeight = scales.Length * ((Crop * Zoom) + Gap);
            var image = System.Windows.Media.Imaging.BitmapSource.Create(sheetWidth, sheetHeight, 96, 96, PixelFormats.Pbgra32, null, sheet, sheetWidth * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(dir!, "corners.png"));
            encoder.Save(file);
            return 0;
        });
    }

    private static string Describe(TextBlock text)
    {
        var face = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var resolved = face.TryGetGlyphTypeface(out var glyphs)
            ? $"{glyphs.FamilyNames.Values.FirstOrDefault()} {glyphs.FaceNames.Values.FirstOrDefault()} weight={glyphs.Weight} simulations={glyphs.StyleSimulations} uri={glyphs.FontUri}"
            : "NOT RESOLVED";
        return $"'{Trim(text.Text)}' family='{text.FontFamily.Source}' requested={text.FontWeight} size={text.FontSize} mode={TextOptions.GetTextFormattingMode(text)} -> {resolved}";
    }

    private static string Trim(string s) => s.Length > 32 ? s[..32] : s;

    private static Grid Gallery()
    {
        var grid = new Grid();
        grid.SetResourceReference(Panel.BackgroundProperty, "Brush.SurfaceCanvas");
        string[] states = ["rest", "hover", "pressed", "focus", "disabled"];
        string[] styles = ["Button.Primary", "Button.Secondary", "Button.Tonal", "Button.Subtle", "Button.Danger"];
        var root = new StackPanel { Margin = new Thickness(24) };
        root.SetResourceReference(Panel.BackgroundProperty, "Brush.SurfaceCanvas");
        foreach (var style in styles)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            foreach (var state in states)
            {
                var button = new Button { Content = $"{style[7..]} {state}", Tag = state, Margin = new Thickness(0, 0, 16, 0), IsEnabled = state != "disabled", Width = 160 };
                button.SetResourceReference(FrameworkElement.StyleProperty, style);
                row.Children.Add(button);
            }

            root.Children.Add(row);
        }

        var inputs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (state, value) in new[] { ("rest", ""), ("filled", "Ayşe Öztürk"), ("focus", "Typing"), ("disabled", "Read only"), ("error", "") })
        {
            var box = new TextBox { Text = value, Tag = state, Width = 170, Margin = new Thickness(0, 0, 16, 0), IsEnabled = state != "disabled" };
            InputAssistPlaceholder(box, state == "rest" || state == "error" ? "Search donors" : null);
            if (state == "error")
            {
                var binding = new System.Windows.Data.Binding("Text") { Source = box };
                System.Windows.Data.BindingOperations.SetBinding(box, FrameworkElement.TagProperty, binding);
                System.Windows.Controls.Validation.MarkInvalid(System.Windows.Data.BindingOperations.GetBindingExpression(box, FrameworkElement.TagProperty)!, new ValidationError(new ExceptionValidationRule(), binding, "Required", null));
            }

            inputs.Children.Add(box);
        }

        root.Children.Add(inputs);
        grid.Children.Add(root);
        return grid;
    }

    /// <summary>Sets the placeholder through the app's attached property when this build has one (2.0.1 and later), by name so the same test runs against 2.0.0.</summary>
    private static void InputAssistPlaceholder(TextBox box, string? text)
    {
        if (text is null)
        {
            return;
        }

        var field = typeof(LoginView).Assembly.GetType("Lifestream.Desktop.Controls.InputAssist")?.GetField("PlaceholderProperty");
        if (field?.GetValue(null) is DependencyProperty property)
        {
            box.SetValue(property, text);
        }
    }
}
