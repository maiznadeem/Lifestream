using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Shell;
using Lifestream.Client.Shell.Placeholders;
using Lifestream.Client.Updates;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<ILifestreamApiClient, LifestreamApiClient>();

        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(p => p.GetRequiredService<ToastService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(p => p.GetRequiredService<DialogService>());
        services.AddSingleton<IErrorReporter, ErrorReporter>();
        services.AddSingleton<IListViewStateStore, ListViewStateStore>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAppFlow, AppFlow>();
        services.AddSingleton(p => new UpdateService(p.GetRequiredService<IUpdateSource>(), appVersion, p.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UpdateService>>()));

        services.AddTransient<LoginViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<AccessDeniedViewModel>();

        // Feature screens (part 1).
        services.AddTransient<Features.Dashboard.DashboardViewModel>();
        services.AddTransient<Features.Donors.DonorsViewModel>();
        services.AddTransient<Features.Patients.PatientsViewModel>();
        services.AddTransient<Features.Stock.StockViewModel>();

        // Placeholder screens; the feature tasks register the real view models in their place.
        services.AddTransient<AppointmentsPlaceholderViewModel>();
        services.AddTransient<RequestsPlaceholderViewModel>();
        services.AddTransient<ReceiptsPlaceholderViewModel>();
        services.AddTransient<StaffPlaceholderViewModel>();
        services.AddTransient<ProfilePlaceholderViewModel>();
        return services;
    }
}
