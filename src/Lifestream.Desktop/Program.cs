using System.Windows;
using Velopack;

namespace Lifestream.Desktop;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        // Must run first: handles install, update and uninstall hooks, then returns control when launched normally.
        VelopackApp.Build().Run();

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new SpikeWindow());
    }
}
