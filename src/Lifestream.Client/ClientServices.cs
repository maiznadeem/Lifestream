using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Shell;
using Lifestream.Client.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lifestream.Client;

public static class ClientServices
{
    /// <summary>Registers everything in the client library. The Desktop project adds the window host, dialog presenter and update source.</summary>
    public static IServiceCollection AddLifestreamClient(this IServiceCollection services, Uri apiBaseAddress, Version appVersion, WakePolicy? wakePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentNullException.ThrowIfNull(appVersion);

        services.AddSingleton(wakePolicy ?? new WakePolicy());
        services.AddSingleton<ServerStatus>();
        services.AddSingleton<IServerStatus>(p => p.GetRequiredService<ServerStatus>());
        services.AddSingleton<ISessionService, SessionService>();

        // One HttpClient for the life of the app: one HTTP/2 connection for every call (SHELL-08).
        services.AddSingleton(p => ApiHttpClientFactory.Create(apiBaseAddress, p.GetRequiredService<WakePolicy>(), p.GetRequiredService<ServerStatus>()));
        // Screens get the client through the operation scope wrapper: every call is cancellable by the operation it runs in (SHELL-07).
        services.AddSingleton<LifestreamApiClient>();
        services.AddSingleton<ILifestreamApiClient>(p => ScopedApiClient.Wrap(p.GetRequiredService<LifestreamApiClient>(), p.GetRequiredService<WakePolicy>()));

        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(p => p.GetRequiredService<ToastService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(p => p.GetRequiredService<DialogService>());
        services.AddSingleton<FormDialogService>();
        services.AddSingleton<IFormDialogService>(p => p.GetRequiredService<FormDialogService>());
        services.AddSingleton<IErrorReporter, ErrorReporter>();
        services.AddSingleton<IListViewStateStore, ListViewStateStore>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAppFlow, AppFlow>();
        // Display preferences (theme, density, the collapsed sidebar, the remembered user name); the Desktop registers the file store first.
        services.TryAddSingleton<IPreferencesStore>(_ => new MemoryPreferencesStore());
        services.AddSingleton<UiPreferences>();
        services.AddSingleton(p => new UpdateService(p.GetRequiredService<IUpdateSource>(), appVersion, p.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UpdateService>>()));

        services.AddTransient<LoginViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<AccessDeniedViewModel>();

        // Feature screens (part 1).
        services.AddTransient<Features.Dashboard.DashboardViewModel>();
        services.AddTransient<Features.Donors.DonorsViewModel>();
        services.AddTransient<Features.Patients.PatientsViewModel>();
        services.AddTransient<Features.Stock.StockViewModel>();

        // Feature screens (part 2).
        services.AddTransient<Features.Appointments.AppointmentsViewModel>();
        services.AddTransient<Features.Requests.RequestsViewModel>();
        services.AddTransient<Features.Receipts.ReceiptsViewModel>();
        services.AddTransient<Features.Staff.StaffViewModel>();
        services.AddTransient<Features.Profile.ProfileViewModel>();
        services.AddSingleton<Features.Receipts.IReceiptViewer, Features.Receipts.ReceiptViewerService>();
        services.AddSingleton<Features.Profile.IChangePasswordPrompt, Features.Profile.ChangePasswordPrompt>();

        // The window-system parts of printing and photo choosing; the Desktop project registers the real ones after this call, which win.
        services.TryAddSingleton<Features.Receipts.IReceiptPrinter, Features.Receipts.NoReceiptPrinter>();
        services.TryAddSingleton<Features.Profile.IPhotoPicker, Features.Profile.NoPhotoPicker>();
        return services;
    }
}
