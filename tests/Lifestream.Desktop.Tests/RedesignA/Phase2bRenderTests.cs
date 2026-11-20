using System.Windows;
using System.Windows.Automation;
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
/// from the fake API (off-screen; no window is shown). The fake API also answers the stock age bands, a group's bags and the receipt counts
/// (<see cref="Dress"/>), so the stock-by-age matrix, the panel's bag list and the tab counts render with figures. A screen is drawn at the size of the shell's page area in a 1440 x 900 window, maximized on a
/// 1920 x 1080 screen and in a 1024 x 700 window (the window less the sidebar, or the rail when compact, and the 44 DIP title bar), with the breakpoint
/// the shell gives that window. Each render is saved for review and checked for what a broken layout loses: the side panel inside the page, the theme's
/// canvas colour, and the compact column folding.
/// </summary>
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class Phase2bRenderTests
{
    private const double TitleBar = 44;

    /// <summary>Where the renders go: LIFESTREAM_CAPTURE_DIR, else the run's fix-round captures folder, else the test output.</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } env
        ? env
        : System.IO.Directory.Exists(@"G:\Claude\workspace\lifestream\run") ? @"G:\Claude\workspace\lifestream\run\captures\redesign-a\fix" : Hosted.CaptureDirectory;

    /// <summary>How the demo stock splits over the six age bands: the expiring units in the last band, the rest spread over the first five, newest first.</summary>
    private static int[] Bands(Contracts.Stock.StockItem item)
    {
        var rest = item.Available - item.ExpiringSoon;
        int[] weights = [3, 3, 2, 1, 1];
        var bands = weights.Select(w => rest * w / 10).ToArray();
        bands[0] += rest - bands.Sum();
        return [.. bands, item.ExpiringSoon];
    }

    /// <summary>The extra answers of the fix round: stock with its age bands, the bags of a group in use-first order, and the receipt tab counts.</summary>
    /// <param name="rig">The rig whose fake API answers.</param>
    /// <param name="bagsOf">The group whose bags the fake API lists.</param>
    /// <param name="source">The stock to dress with age bands; the part 2 demo stock when null.</param>
    internal static void Dress(Client.Tests.Fakes.Rig rig, BloodType bagsOf = BloodType.ANeg, IEnumerable<Contracts.Stock.StockItem>? source = null)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var stock = (source ?? DemoData.Stock()).Select(s => s with { AgeBands = Bands(s) }).ToList();
        rig.Api.On("GetStockAsync", Client.Api.ApiResult<IReadOnlyList<Contracts.Stock.StockItem>>.Ok(stock));
        var group = stock.Single(s => s.BloodType == bagsOf);
        var today = DateTime.UtcNow.Date;
        var bags = new List<Contracts.Stock.StockBag>();
        for (var band = Contracts.Stock.StockAges.BandCount - 1; band >= 0; band--)
        {
            for (var k = 0; k < group.AgeBands![band]; k++)
            {
                var age = Contracts.Stock.StockAges.Bands[band].ToDay - k;
                var collected = today.AddDays(-age).AddHours(9 + k);
                bags.Add(new(4100 + bags.Count * 7, DateTime.SpecifyKind(collected, DateTimeKind.Utc), DateOnly.FromDateTime(collected).AddDays(BloodRules.ShelfLifeDays)));
            }
        }

        rig.Api.On("GetStockBagsAsync", Client.Api.ApiResult<Contracts.Stock.StockBagList>.Ok(new(bagsOf, bags, false)));
        rig.Api.On("GetReceiptCountsAsync", Client.Api.ApiResult<Contracts.Receipts.ReceiptCounts>.Ok(new(120, 84, 36)));

        // Nobody in the design clinic has uploaded a photo, so every person shows their initials, as the app does without one (design-qa-2 N8: the test's drawn
        // stand-in portrait read as a cartoon figure in the release renders).
        rig.Api.On("GetStaffAsync", Client.Api.ApiResult<Contracts.PagedResult<Contracts.Staff.StaffListItem>>.Ok(new([.. Enumerable.Range(1, 50).Select(i => Features.DemoData2.StaffRow(i) with { HasPhoto = false })], 110, 1, 50)));
        rig.Api.On("GetStaffMemberAsync", Client.Api.ApiResult<Contracts.Staff.StaffDetail>.Ok(Features.DemoData2.StaffDetail() with { HasPhoto = false }));
        rig.Api.On("GetStaffPhotoAsync", Client.Tests.Fakes.FakeApiClient.Fail<byte[]>(404, "not_found"));
        rig.Api.On("GetMyPhotoAsync", Client.Tests.Fakes.FakeApiClient.Fail<byte[]>(404, "not_found"));
    }

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

    /// <summary>
    /// The page as the shell hosts it at this size: the canvas, the shell's page padding and the breakpoint of the window (the side panel bleeds through the
    /// padding to the window's edges), the same stage the list-screen renders use.
    /// </summary>
    private static FrameworkElement Stage(UserControl view, string size)
    {
        var (width, height) = size switch
        {
            "1440" => (1440, 900),
            "max" => (1920, 1080),
            _ => (1024, 700),
        };
        return ListScreenRenderTests.PageArea(view, width, height, out _);
    }

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
                Dress(rig);
                var view = Build(screen, rig);
                using var host = Hosted.Create(Stage(view, size), width, height);
                Load(screen, view, host);
                var panel = host.All<SidePanel>().SingleOrDefault();
                host.Settle(600);
                var png = host.Capture();
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(Path.Combine(Directory, $"{screen}-{size}-{theme}.png"), png);
                RenderChecks.Check(host, $"{screen}-{size}-{theme}");
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
                var footer = host.All<Button>().Where(b => b.IsVisible && Hosted.BoundsOf(b, host.Element).Top > Hosted.BoundsOf(surface, host.Element).Bottom - 80)
                    .OrderBy(b => Hosted.BoundsOf(b, host.Element).Left).Select(b => AutomationProperties.GetName(b)).ToList();
                return (Png: png, Layer: layer, Surface: Hosted.BoundsOf(surface, host.Element), Primary: host.All<Button>().Any(b => b.IsVisible && b.Name == "PrimaryButton"), Footer: footer);
            }
            finally
            {
                ThemeService.Apply(ThemeMode.Light);
            }
        });

        Assert.Equal(32, facts.Surface.Left, 0); // the shadow room
        Assert.True(StaHost.CountPixelsNear(facts.Png, facts.Layer.R, facts.Layer.G, facts.Layer.B, 1) > 20000, "the dialog's layer colour is missing");
        Assert.Equal(dialog != "receipt", facts.Primary); // the receipt dialog has no form primary: its own Print is the primary, last in the footer
        if (dialog == "receipt")
        {
            Assert.Equal(["Save as PDF", "Close", "Print"], facts.Footer);
        }
    }

    /// <summary>
    /// The confirmation of a destructive menu action (there is no inline confirm: "Delete staff member" and "Cancel appointment" ask in this dialog), as
    /// its window draws it: direction A's dialog surface with 32 DIP of shadow room, the record named in the body, and the full action on the danger button
    /// with the button that keeps the record before it ("Keep appointment", never a bare "Cancel" beside "Cancel appointment").
    /// </summary>
    [Theory]
    [InlineData("delete", "light")]
    [InlineData("delete", "dark")]
    [InlineData("cancel", "light")]
    [InlineData("cancel", "dark")]
    public void ConfirmDialog_IsOnTheDialogSurface_NamesTheRecord_AndItsButtonCarriesTheFullAction(string action, string theme)
    {
        var (title, body, verb, keep) = action == "delete"
            ? ("Delete staff member?", "Sofia Sharma will be removed from the list. You can undo this for a few seconds afterwards.", "Delete staff member", "Keep staff member")
            : ("Cancel appointment?", "Asher Devereux's appointment on Thu 19 Nov at 11:00 will be cancelled, and the slot becomes free.", "Cancel appointment", "Keep appointment");
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            try
            {
                // The dialog is a window; its content is drawn in the off-screen host (no window is shown).
                var dialog = DialogWindow.CreateConfirm(title, body, verb, keep);
                var content = (FrameworkElement)dialog.Content;
                dialog.Content = null;
                var canvas = new Grid();
                canvas.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
                canvas.Children.Add(new Border { Child = content, VerticalAlignment = VerticalAlignment.Top });
                using var host = Hosted.Create(canvas, dialog.Width, 300);
                host.Settle(300);
                var png = host.Capture();
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(Path.Combine(Directory, $"dialog-confirm-{action}-{theme}.png"), png);
                RenderChecks.Check(host, $"dialog-confirm-{action}-{theme}");
                var surface = host.All<DialogSurface>().Single();
                var buttons = host.All<Button>().Where(b => b.IsVisible).OrderBy(b => Hosted.BoundsOf(b, host.Element).Left).ToList();
                var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
                dialog.Close();
                return (
                    Surface: Hosted.BoundsOf(surface, host.Element),
                    surface.Title,
                    Texts: texts,
                    Buttons: buttons.Select(b => b.Content as string).ToList(),
                    Danger: ReferenceEquals(buttons[^1].Style, buttons[^1].TryFindResource("Button.Danger")),
                    Layer: StaHost.CountPixelsNear(png, ThemeService.ColorOf("Color.Layer").R, ThemeService.ColorOf("Color.Layer").G, ThemeService.ColorOf("Color.Layer").B, 1));
            }
            finally
            {
                ThemeService.Apply(ThemeMode.Light);
            }
        });

        Assert.Equal(32, facts.Surface.Left, 0); // the shadow room around the surface
        Assert.Equal(480, facts.Surface.Width, 0);
        Assert.True(facts.Layer > 20000, "the dialog's layer colour is missing");
        Assert.Equal(title, facts.Title);
        Assert.Contains(body, facts.Texts); // names the record
        Assert.Equal([keep, verb], facts.Buttons); // the full action, never a bare "Delete" or "OK", and what the other button keeps, never a bare "Cancel"
        Assert.True(facts.Danger);
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

            using var host = Hosted.Create(Stage(view, "1440"), width, height);
            Hosted.Await(load);
            host.Settle(500);
            var png = host.Capture();
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Path.Combine(Directory, $"{state}-1440-light.png"), png);
            RenderChecks.Check(host, state);
            return (Empty: host.All<EmptyState>().Any(e => e.IsVisible), Error: host.All<ErrorState>().Any(e => e.IsVisible));
        });

        Assert.Equal(state == "stock-empty", facts.Empty);
        Assert.Equal(state == "receipts-error", facts.Error);
    }

    /// <summary>
    /// The new states of the fix round, at 1440 x 900 (and dark where the colour matters): the stock matrix and the receipts list without a panel (all
    /// columns), receipts maximized (Issued by in full), a salary range set on Staff and its popup's content, and Profile with a change (the save bar).
    /// </summary>
    [Theory]
    [InlineData("stock-matrix", "light")]
    [InlineData("stock-matrix", "dark")]
    [InlineData("receipts-list", "light")]
    [InlineData("receipts-list-max", "light")]
    [InlineData("staff-salary", "light")]
    [InlineData("staff-salary", "dark")]
    [InlineData("staff-editor", "light")]
    [InlineData("staff-editor", "dark")]
    [InlineData("profile-changed", "light")]
    [InlineData("profile-changed", "dark")]
    [InlineData("profile-changed-max", "light")]
    public void FixState_Renders(string state, string theme)
    {
        var (width, height, _) = PageArea(state.EndsWith("-max", StringComparison.Ordinal) ? "max" : "1440");
        var facts = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            try
            {
                using var rig = Part2Harness.NewRig();
                Dress(rig);
                var screen = state.Split('-')[0];
                var view = Build(screen, rig);
                using var host = Hosted.Create(Stage(view, state.EndsWith("-max", StringComparison.Ordinal) ? "max" : "1440"), width, height);
                var texts = new List<string>();
                switch (view.DataContext)
                {
                    case StockViewModel stock:
                        Hosted.Await(() => stock.LoadAsync());
                        break;
                    case ReceiptsViewModel receipts:
                        Hosted.Await(() => receipts.LoadAsync());
                        break;
                    case StaffViewModel staff when state == "staff-editor":
                        Hosted.Await(() => staff.LoadAsync());
                        Hosted.Await(() => staff.AddCommand.ExecuteAsync());
                        break;
                    case StaffViewModel staff:
                        Hosted.Await(() => staff.LoadAsync());

                        // As typing in the popup does it: on the UI's own context, so the reload the filter starts comes back to it. (Set from plain test
                        // code, the reload finished on a pool thread and the list showed "couldn't show this data": the fix round's capture had that.)
                        Hosted.Await(() =>
                        {
                            staff.SalaryFromText = "2000";
                            staff.SalaryToText = "3500";
                            return staff.PendingRefresh;
                        });
                        break;
                    case ProfileViewModel profile:
                        Hosted.Await(() => profile.LoadAsync());
                        host.Settle(200);
                        profile.DisplayName = "Test User Lindqvist";
                        break;
                }

                host.Settle(700);
                var png = host.Capture();
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(Path.Combine(Directory, $"{state}-{(state.EndsWith("-max", StringComparison.Ordinal) ? "max" : "1440")}-{theme}.png".Replace("-max-max", "-max", StringComparison.Ordinal)), png);
                RenderChecks.Check(host, state);
                texts.AddRange(host.All<TextBlock>().Where(t => t.IsVisible).Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text));
                var trimmed = host.All<TextBlock>().Where(t => t.IsVisible && TruncationTooltip.IsTrimmed(t) && !TruncationTooltip.GetIsEnabled(t))
                    .Select(t => t.Text).ToList();

                if (view is StaffView staffView && staffView.FindName("SalaryPopup") is System.Windows.Controls.Primitives.Popup popup && popup.Child is FrameworkElement child)
                {
                    // The popup's content, drawn in the page's own off-screen host (a popup would open a window of its own).
                    popup.Child = null;
                    child.DataContext = view.DataContext;
                    var stage = new Grid();
                    stage.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
                    stage.Children.Add(new Border { Child = child, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
                    using var popupHost = Hosted.Create(stage, 340, 240);
                    popupHost.Settle(300);
                    File.WriteAllBytes(Path.Combine(Directory, $"staff-salary-popup-{theme}.png"), popupHost.Capture());
                }

                var loadState = view.DataContext.GetType().GetProperty("State")?.GetValue(view.DataContext) as Client.Common.LoadState?;
                var rows = host.All<DataGridRow>().Count(r => r.IsVisible);
                return (Png: png, Canvas: ThemeService.ColorOf("Color.Canvas"), Texts: texts, Trimmed: trimmed, State: loadState, Rows: rows);
            }
            finally
            {
                ThemeService.Apply(ThemeMode.Light);
            }
        });

        Assert.True(StaHost.CountPixelsNear(facts.Png, facts.Canvas.R, facts.Canvas.G, facts.Canvas.B, 1) > 5000, "the theme's canvas colour is missing");
        Assert.True(facts.Trimmed.Count == 0, "cut off without a tooltip: " + string.Join(" | ", facts.Trimmed));
        Assert.True(facts.State is null or Client.Common.LoadState.Ready, $"the screen is {facts.State}, not loaded: the render shows a state nobody asked for");
        switch (state)
        {
            case "stock-matrix":
                Assert.Contains("35–42", facts.Texts); // the band headers come from the API's band definitions
                Assert.Contains("42 units", facts.Texts);
                break;
            case "receipts-list" or "receipts-list-max":
                Assert.Contains("All 120", facts.Texts); // tab counts from the API
                Assert.Contains("Donations 84", facts.Texts);
                break;
            case "staff-salary":
                Assert.Contains("Salary: 2,000–3,500", facts.Texts);
                Assert.True(facts.Rows > 0, "the filtered staff list shows no rows");
                break;
            case "staff-editor":
                Assert.Single(facts.Texts, t => t == "New staff member"); // the form's title, not repeated by the panel's overline
                break;
            default:
                Assert.Contains("You have unsaved changes.", facts.Texts);
                break;
        }
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
                Hosted.Await(() => stock.PendingBags);
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
