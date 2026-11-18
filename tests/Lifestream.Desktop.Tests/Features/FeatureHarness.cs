using System.Windows;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Features.Dashboard;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Stock;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>Shared set-up of the feature view tests: a client rig with the WPF test dispatcher, the demo data on the fake API, and capture saving.</summary>
public static class FeatureHarness
{
    /// <summary>Where render captures go: <c>LIFESTREAM_CAPTURE_DIR</c> when set, otherwise the run's captures folder for this task.</summary>
    public static string CaptureDirectory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } fromEnvironment
        ? fromEnvironment
        : Directory.Exists(@"G:\Claude\workspace\lifestream\run") ? @"G:\Claude\workspace\lifestream\run\captures\b-05-features1" : Hosted.CaptureDirectory;

    public static Rig NewRig(bool demo = true)
    {
        var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
        if (demo)
        {
            DemoData.Configure(rig.Api);
        }

        return rig;
    }

    public static T Create<T>(Rig rig)
        where T : ViewModelBase
    {
        var vm = ActivatorUtilities.CreateInstance<T>(rig.Provider);
        vm.Attach(null, TimeProvider.System);
        return vm;
    }

    /// <summary>Gives a view the canvas colour the shell's page area has, so a capture of the bare view looks as it does in the app.</summary>
    private static T OnCanvas<T>(T view, object model)
        where T : System.Windows.Controls.UserControl
    {
        view.DataContext = model;
        view.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Brush.SurfaceCanvas");
        return view;
    }

    public static DonorsView DonorsView(DonorsViewModel vm) => OnCanvas(new DonorsView(), vm);

    public static PatientsView PatientsView(PatientsViewModel vm) => OnCanvas(new PatientsView(), vm);

    public static StockView StockView(StockViewModel vm) => OnCanvas(new StockView(), vm);

    public static DashboardView DashboardView(DashboardViewModel vm) => OnCanvas(new DashboardView(), vm);

    /// <summary>Saves the capture under the capture folder and returns the PNG bytes.</summary>
    public static byte[] Save(Hosted host, string fileName)
    {
        var png = host.Capture();
        Directory.CreateDirectory(CaptureDirectory);
        File.WriteAllBytes(Path.Combine(CaptureDirectory, fileName), png);
        return png;
    }

    public static Rect BoundsIn(FrameworkElement element, FrameworkElement root) => Hosted.BoundsOf(element, root);

    public static IEnumerable<T> Visible<T>(Hosted host)
        where T : FrameworkElement => host.All<T>().Where(e => e.IsVisible);
}
