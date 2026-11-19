using System.Reflection;
using System.Windows;
using Lifestream.Client;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Lookups;
using Lifestream.Client.Navigation;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Dialogs;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Services;
using Lifestream.Desktop.Theme;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lifestream.Desktop;

/// <summary>
/// The application: builds the generic host (configuration, logging, DI), installs the global exception handlers, loads the
/// theme, wires the one shared <see cref="System.Net.Http.HttpClient"/> through the client library, and shows the login window. Windows are
/// owned by <see cref="IAppFlow"/>; the application shuts down only when the last window is closed by the user.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Application cannot be disposed; the host and the exception handler are disposed in OnExit.")]
public sealed class App : Application
{
    private IHost? _host;
    private GlobalExceptionHandler? _exceptions;

    public App() => ShutdownMode = ShutdownMode.OnExplicitShutdown;

    /// <summary>The release feed checked at startup.</summary>
    public const string ReleaseRepositoryUrl = "https://github.com/maiznadeem/lifestream";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LaunchProbe.Configure(e.Args);

        // Order matters. The host (configuration, logging, DI) comes first, then the global exception handlers, and only then the theme and the
        // fonts: anything that goes wrong while loading them is logged and shown as a friendly dialog instead of ending the process unseen (SHELL-01).
        _host = Host.CreateDefaultBuilder(e.Args).UseContentRoot(AppContext.BaseDirectory).ConfigureServices((context, services) => Configure(context.Configuration, services)).Build();
        var provider = _host.Services;

        var windows = (WpfWindowHost)provider.GetRequiredService<IWindowHost>();
        provider.GetRequiredService<DialogService>().Presenter = new WpfDialogPresenter(() => windows.ActiveWindow);
        provider.GetRequiredService<FormDialogService>().Presenter = new WpfFormDialogPresenter(() => windows.ActiveWindow);

        _exceptions = new GlobalExceptionHandler(provider.GetRequiredService<IErrorReporter>(), Dispatcher);
        _exceptions.Install();

        var startupLog = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Lifestream.Startup");
        FontSetup.Warn = message => StartupLog.Warning(startupLog, message);
        Resources.MergedDictionaries.Add(ThemeLoader.Resources);
        _ = Features.Dashboard.ChartWarmUp.Start(Dispatcher); // the first dashboard's one-time cost is paid while the sign-in window is idle, before the dashboard is shown (FEAT-27)

        RecordPicker.ViewModelFactory = kind => new RecordPickerViewModel(provider.GetRequiredService<ILifestreamApiClient>(), kind, serverStatus: provider.GetRequiredService<IServerStatus>());
        provider.GetRequiredService<ISessionExpiredWatcher>().Start();
        provider.GetRequiredService<IAppFlow>().Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exceptions?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }

    private static void Configure(IConfiguration configuration, IServiceCollection services)
    {
        var address = configuration["Api:BaseAddress"] ?? throw new InvalidOperationException("Api:BaseAddress is not configured.");
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(2, 0, 2);
        services.AddLifestreamClient(new Uri(address), version);
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Current.Dispatcher));
        services.AddSingleton<IWindowHost, WpfWindowHost>();
        services.AddSingleton<Lifestream.Client.Updates.IUpdateSource>(_ => new StoreAwareUpdateSource(new WindowsPackageIdentityProbe(), () => new VelopackFeed(ReleaseRepositoryUrl)));
        services.AddSingleton<ISessionExpiredWatcher, SessionExpiredWatcher>();
        services.AddSingleton<IReceiptPrinter, WpfReceiptPrinter>();
        services.AddSingleton<IPhotoPicker, WpfPhotoPicker>();
    }
}

/// <summary>Signs the user out (back to the login window with a notice) when the API refuses to refresh the session.</summary>
public interface ISessionExpiredWatcher
{
    void Start();
}

public sealed class SessionExpiredWatcher(Lifestream.Client.Session.ISessionService session, IAppFlow flow) : ISessionExpiredWatcher
{
    public void Start() =>
        session.Expired += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => flow.SignOutAsync("Your session expired. Please sign in again.", force: true));
}
