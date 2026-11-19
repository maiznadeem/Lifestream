using System.Windows.Media.Imaging;
using Lifestream.Client.Auth;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Auth;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>INFRA-19: the sign-in screen for the README and the Store listing, rendered off-screen (no window is ever shown) by tools/generate-screenshots.ps1. The other screens are captured by the feature tests.</summary>
[Collection("Wpf")]
public sealed class LoginScreenshotTests
{
    [Fact]
    [Trait("Category", "Screenshots")]
    public void Screenshot_Login_IsCapturedOffScreen_AtLeast1366x768()
    {
        var (width, height, fileName) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var viewModel = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(new LoginView { DataContext = viewModel }, 1366, 768);
            host.Settle(300);
            var png = host.Capture("login.png");
            var frame = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight, "login.png");
        });

        Assert.True(width >= 1366 && height >= 768, $"{fileName} is {width}x{height}");
        Assert.True(File.Exists(Path.Combine(Hosted.CaptureDirectory, fileName)));
    }
}
