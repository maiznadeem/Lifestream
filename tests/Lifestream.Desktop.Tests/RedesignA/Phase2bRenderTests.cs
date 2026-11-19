using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Features.Stock;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Staff;
using Lifestream.Desktop.Features.Stock;
using Lifestream.Desktop.Tests.Features;
using Lifestream.Desktop.Theme;
using Lifestream.Domain;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// Renders of direction A's Stock, Receipts, Staff and Profile screens and the form dialogs, light and dark, through the real view models and views
/// from the fake API (off-screen; no window is shown). A screen is drawn at the size of the shell's page area in a 1440 x 900 window, maximized on a
/// 1920 x 1080 screen and in a 1024 x 700 window (the window less the sidebar, or the rail when compact, and the 44 DIP title bar), with the breakpoint
/// the shell gives that window. Each render is saved for review and checked for what a broken layout loses: the side panel inside the page, the theme's
/// canvas colour, and the compact column folding.
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class Phase2bRenderTests
{
    private const double TitleBar = 44;

    /// <summary>Where the renders go: LIFESTREAM_CAPTURE_DIR, else the run's phase 2 captures folder, else the test output.</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } env
        ? env
        : System.IO.Directory.Exists(@"G:\Claude\workspace\lifestream\run") ? @"G:\Claude\workspace\lifestream\run\captures\redesign-a\phase2" : Hosted.CaptureDirectory;

    public static TheoryData<string, string, string> Screens()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var screen in new[] { "stock", "receipts", "staff", "profile" })
        {
            foreach (var size in new[] { "1440", "max", "narrow" })
            {
                foreach (var theme in new[] { "light", "dark" })
                {
                    data.Add(screen, size, theme);
                }
            }
        }

        return data;
    }

    private static (double Width, double Height, Breakpoint Breakpoint) PageArea(string size) => size switch
    {
        "1440" => (1440 - 236, 900 - TitleBar, Breakpoint.Standard),
        "max" => (1920 - 236, 1080 - TitleBar, Breakpoint.Wide),
        _ => (1024 - 52, 700 - TitleBar, Breakpoint.Compact),
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void Screen_RendersDirectionA(string screen, string size, string theme)
    {
        var (width, height, breakpoint) = PageArea(size);
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            try
            {
                using var rig = Part2Harness.NewRig();
                var view = Build(screen, rig);
                Responsive.SetBreakpoint(view, breakpoint);
                using var host = Hosted.Create(Part2Harness.OnCanvas(view, view.DataContext), width, height);
                Load(screen, view, host);
                var panel = host.All<SidePanel>().SingleOrDefault();
                host.Settle(600);
                var png = host.Capture();
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(Path.Combine(Directory, $"{screen}-{size}-{theme}.png"), png);
                var panelBounds = panel is { IsVisible: true } ? Hosted.BoundsOf(panel, host.Element) : Rect.Empty;
                return (Png: png, Canvas: ThemeService.ColorOf("Color.Canvas"), PanelOpen: panel?.IsVisible == true, Panel: panelBounds, PanelWidth: panel?.ActualWidth ?? 0);
            }
            finally
            {
                ThemeService.Apply(ThemeMode.Light);
            }
        });

        Assert.True(StaHost.CountPixelsNear(facts.Png, facts.Canvas.R, facts.Canvas.G, facts.Canvas.B, 1) > 5000, "the theme's canvas colour is missing");
        if (screen != "profile")
        {
            Assert.True(facts.PanelOpen, "the selected record's side panel did not open");
            Assert.True(facts.Panel.Right <= width + 0.5 && facts.Panel.Bottom <= height + 0.5, $"the side panel is clipped: {facts.Panel}");
            Assert.Equal(breakpoint == Breakpoint.Compact ? 352 : 420, facts.PanelWidth, 0);
        }
    }

    public static TheoryData<string, string> Dialogs()
    {
        var data = new TheoryData<string, string>();
        foreach (var dialog in new[] { "changepassword", "resetpassword", "receipt" })
        {
            data.Add(dialog, "light");
            data.Add(dialog, "dark");
        }

        return data;
    }

    /// <summary>A form dialog as its window draws it: the DialogSurface with 32 DIP of shadow room around it, at its natural height, over the canvas.</summary>
    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Dialog_RendersTheDialogSurface(string dialog, string theme)
    {
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            try
            {
                using var rig = Part2Harness.NewRig();
                FrameworkElement view;
                Func<Task> load = () => Task.CompletedTask;
                switch (dialog)
                {
                    case "changepassword":
                        var change = new ChangePasswordDialogViewModel(rig.Api);
                        view = new ChangePasswordDialogView { DataContext = change };
                        break;
                    case "resetpassword":
                        var reset = new ResetPasswordDialogViewModel(rig.Api, 2, DemoData.Name(9));
                        view = new ResetPasswordDialogView { DataContext = reset };
                        load = () => reset.PrimaryCommand.ExecuteAsync(); // empty: shows the field messages, sends nothing
                        break;
                    default:
                        var viewer = new ReceiptViewerViewModel(rig.Api, new NoReceiptPrinter(), 1, isDuplicate: true);
                        viewer.Attach(null, TimeProvider.System);
                        view = new ReceiptViewerView { DataContext = viewer };
                        load = () => viewer.LoadAsync();
                        break;
                }

                var room = new Border { Child = view, VerticalAlignment = VerticalAlignment.Top };
                room.SetResourceReference(Border.PaddingProperty, "Pad.32");
                var canvas = new Grid();
                canvas.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
                canvas.Children.Add(room);
                using var host = Hosted.Create(canvas, 624, dialog == "receipt" ? 860 : 520);
                host.Settle(200);
                Hosted.Await(load);
                host.Settle(500);
                var png = host.Capture();
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(Path.Combine(Directory, $"dialog-{dialog}-{theme}.png"), png);
                var surface = host.All<DialogSurface>().Single();
                var layer = ThemeService.ColorOf("Color.Layer");
                return (Png: png, Layer: layer, Surface: Hosted.BoundsOf(surface, host.Element), Primary: host.All<Button>().Any(b => b.IsVisible && b.Name == "PrimaryButton"));
            }
            finally
            {
                ThemeService.Apply(ThemeMode.Light);
            }
        });

        Assert.Equal(32, facts.Surface.Left, 0); // the shadow room
        Assert.True(StaHost.CountPixelsNear(facts.Png, facts.Layer.R, facts.Layer.G, facts.Layer.B, 1) > 20000, "the dialog's layer colour is missing");
        Assert.Equal(dialog != "receipt", facts.Primary); // the receipt dialog has Print and Save as PDF in its body and Close in the footer
    }

    /// <summary>The empty and error states of the stock and receipts pages (LoadSurface), light, at 1440 x 900.</summary>
    [Theory]
    [InlineData("stock-empty")]
    [InlineData("receipts-error")]
    public void State_RendersThroughTheLoadSurface(string state)
    {
        var (width, height, _) = PageArea("1440");
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            UserControl view;
            Func<Task> load;
            if (state == "stock-empty")
            {
                rig.Api.On("GetStockAsync", Client.Api.ApiResult<IReadOnlyList<Contracts.Stock.StockItem>>.Ok([]));
                var stock = Part2Harness.Create<StockViewModel>(rig);
                view = new StockView { DataContext = stock };
                load = () => stock.LoadAsync();
            }
            else
            {
                rig.Api.On("GetReceiptsAsync", Client.Api.ApiResult<Contracts.PagedResult<Contracts.Receipts.ReceiptListItem>>.Fail(new Client.Api.ApiProblem(500, "server_error", "The server failed.", "corr-7")));
                var receipts = Part2Harness.Create<ReceiptsViewModel>(rig);
                view = new ReceiptsView { DataContext = receipts };
                load = () => receipts.LoadAsync();
            }

            using var host = Hosted.Create(Part2Harness.OnCanvas(view, view.DataContext), width, height);
            Hosted.Await(load);
            host.Settle(500);
            var png = host.Capture();
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Path.Combine(Directory, $"{state}-1440-light.png"), png);
            return (Empty: host.All<EmptyState>().Any(e => e.IsVisible), Error: host.All<ErrorState>().Any(e => e.IsVisible));
        });

        Assert.Equal(state == "stock-empty", facts.Empty);
        Assert.Equal(state == "receipts-error", facts.Error);
    }

    private static UserControl Build(string screen, Client.Tests.Fakes.Rig rig) => screen switch
    {
        "stock" => new StockView { DataContext = Part2Harness.Create<StockViewModel>(rig) },
        "receipts" => new ReceiptsView { DataContext = Part2Harness.Create<ReceiptsViewModel>(rig) },
        "staff" => new StaffView { DataContext = Part2Harness.Create<StaffViewModel>(rig) },
        _ => new ProfileView { DataContext = Part2Harness.Create<ProfileViewModel>(rig) },
    };

    private static void Load(string screen, UserControl view, Hosted host)
    {
        switch (view.DataContext)
        {
            case StockViewModel stock:
                Hosted.Await(() => stock.LoadAsync());
                host.Settle(200);
                stock.SelectedTile = stock.Tiles.Single(t => t.BloodType == BloodType.ANeg); // a group below its minimum
                break;
            case ReceiptsViewModel receipts:
                Hosted.Await(() => receipts.LoadAsync());
                host.Settle(200);
                Hosted.Await(() => { receipts.SelectedItem = receipts.Items[0]; return receipts.PendingDetail; });
                break;
            case StaffViewModel staff:
                Hosted.Await(() => staff.LoadAsync());
                host.Settle(400);
                Hosted.Await(() => { staff.SelectedItem = staff.Items[1]; return staff.PendingDetail; });
                host.Settle(300);
                break;
            case ProfileViewModel profile:
                Hosted.Await(() => profile.LoadAsync());
                break;
            default:
                throw new InvalidOperationException(screen);
        }
    }
}
