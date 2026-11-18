using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Updates;

namespace Lifestream.Client.Shell;

/// <summary>One sidebar button.</summary>
public sealed partial class NavItem(Destination destination) : ObservableObject
{
    public Destination Destination { get; } = destination;

    public string Title => Destination.Title;

    public string IconGlyph => Destination.IconGlyph;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

/// <summary>
/// The main window's view model (SHELL-17..23): sidebar, account menu, window title, toasts, the "update ready" prompt, and
/// the reaction to a demo reset. It owns no data of its own; the page in <see cref="Navigation"/> does.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly ISessionService _session;
    private readonly IAppFlow _flow;
    private readonly IServerStatus _serverStatus;
    private readonly IReadOnlyList<Destination> _catalog;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public ShellViewModel(
        INavigationService navigation,
        ISessionService session,
        IAppFlow flow,
        IToastService toasts,
        IServerStatus serverStatus,
        UpdateService updates)
        : this(navigation, session, flow, toasts, serverStatus, updates, NavigationCatalog.Default)
    {
    }

    public ShellViewModel(
        INavigationService navigation,
        ISessionService session,
        IAppFlow flow,
        IToastService toasts,
        IServerStatus serverStatus,
        UpdateService updates,
        IReadOnlyList<Destination> catalog)
        : base(LoadState.Ready)
    {
        Navigation = navigation;
        Toasts = toasts;
        Updates = updates;
        _session = session;
        _flow = flow;
        _serverStatus = serverStatus;
        _catalog = catalog;

        var allowed = catalog.Where(d => d.IsAllowedFor(session.User)).ToList();
        Groups =
        [
            .. allowed.GroupBy(d => d.Group).Select(g => new NavGroup(g.Key, [.. g.Select(d => new NavItem(d))])),
        ];

        NavigateCommand = new AsyncRelayCommand((parameter, _) => parameter is NavItem item ? OpenAsync(item.Destination) : Task.CompletedTask);
        ProfileCommand = new AsyncRelayCommand(_ => NavigateToTypeAsync(NavigationCatalog.ProfileViewModelType, "profile"));
        ChangePasswordCommand = new AsyncRelayCommand(_ => NavigateToTypeAsync(NavigationCatalog.ProfileViewModelType, "change-password"));
        SignOutCommand = new AsyncRelayCommand(_ => _flow.SignOutAsync());
        ApplyUpdateCommand = new AsyncRelayCommand(ct => Updates.ApplyAsync(ct));
        BackToDashboardCommand = new AsyncRelayCommand(_ => OpenAsync(_catalog[0]));
        SearchCommand = new AsyncRelayCommand(_ =>
        {
            Navigation.Current?.RequestFocusSearch();
            return Task.CompletedTask;
        });
        NewRecordCommand = new AsyncRelayCommand(async _ =>
        {
            if (Navigation.Current?.NewCommand is { } command)
            {
                await command.ExecuteAsync().ConfigureAwait(true);
            }
        });

        navigation.CurrentChanged += OnCurrentChanged;
        serverStatus.DemoResetObserved += OnDemoReset;
        updates.PropertyChanged += (_, _) => OnPropertyChanged(nameof(UpdatePrompt));
    }

    public INavigationService Navigation { get; }

    public IToastService Toasts { get; }

    public UpdateService Updates { get; }

    public IReadOnlyList<NavGroup> Groups { get; }

    public IEnumerable<NavItem> Items => Groups.SelectMany(g => g.Items);

    public AsyncRelayCommand NavigateCommand { get; }

    public AsyncRelayCommand ProfileCommand { get; }

    public AsyncRelayCommand ChangePasswordCommand { get; }

    public AsyncRelayCommand SignOutCommand { get; }

    public AsyncRelayCommand ApplyUpdateCommand { get; }

    public AsyncRelayCommand BackToDashboardCommand { get; }

    /// <summary>Ctrl+F: focuses the current page's search box.</summary>
    public AsyncRelayCommand SearchCommand { get; }

    /// <summary>Ctrl+N: runs the current page's "new record" action.</summary>
    public AsyncRelayCommand NewRecordCommand { get; }

    /// <summary>The page being shown; the content area binds to it and the view locator picks the view.</summary>
    public ViewModelBase? CurrentPage => Navigation.Current;

    public override string PageTitle => CurrentPage?.PageTitle ?? string.Empty;

    /// <summary>"Lifestream - Donors": updated on every navigation (SHELL-23).</summary>
    public string WindowTitle => string.IsNullOrEmpty(PageTitle) ? "Lifestream" : $"Lifestream - {PageTitle}";

    public string DisplayName => _session.User?.DisplayName ?? string.Empty;

    public string Initials
    {
        get
        {
            var parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
            };
        }
    }

    public bool IsDemo => _session.User?.IsDemo == true;

    public string? UpdatePrompt => Updates.IsUpdateReady ? Updates.Message : null;

    /// <summary>Opens the first destination and checks for an update in the background.</summary>
    public async Task StartAsync()
    {
        _ = Updates.CheckAsync();
        await OpenAsync(_catalog[0]).ConfigureAwait(true);
    }

    /// <summary>Opens a destination, or the access-denied page when the signed-in role cannot use it.</summary>
    public async Task OpenAsync(Destination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.IsAllowedFor(_session.User))
        {
            await Navigation.NavigateTo<AccessDeniedViewModel>(BackToDashboardCommand).ConfigureAwait(true);
            return;
        }

        await Navigation.NavigateTo(destination.ViewModelType).ConfigureAwait(true);
        foreach (var item in Items)
        {
            item.IsSelected = item.Destination == destination;
        }
    }

    private async Task NavigateToTypeAsync(Type type, object parameter)
    {
        if (await Navigation.NavigateTo(type, parameter).ConfigureAwait(true))
        {
            foreach (var item in Items)
            {
                item.IsSelected = false;
            }
        }
    }

    private void OnCurrentChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void OnDemoReset(object? sender, EventArgs e)
    {
        void Handle()
        {
            Toasts.Show(ToastKind.Info, "Demo data was reset. This screen has been refreshed.");
            _ = Navigation.ReloadCurrentAsync();
        }

        if (_context is null || SynchronizationContext.Current == _context)
        {
            Handle();
        }
        else
        {
            _context.Post(_ => Handle(), null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Navigation.CurrentChanged -= OnCurrentChanged;
            _serverStatus.DemoResetObserved -= OnDemoReset;
            Navigation.Reset();
        }

        base.Dispose(disposing);
    }
}
