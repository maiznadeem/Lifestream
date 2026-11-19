using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Profile;
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
    private readonly IChangePasswordPrompt _passwords;
    private readonly IReadOnlyList<Destination> _catalog;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public ShellViewModel(
        INavigationService navigation,
        ISessionService session,
        IAppFlow flow,
        IToastService toasts,
        IServerStatus serverStatus,
        UpdateService updates,
        IChangePasswordPrompt passwords)
        : this(navigation, session, flow, toasts, serverStatus, updates, passwords, NavigationCatalog.Default)
    {
    }

    public ShellViewModel(
        INavigationService navigation,
        ISessionService session,
        IAppFlow flow,
        IToastService toasts,
        IServerStatus serverStatus,
        UpdateService updates,
        IChangePasswordPrompt passwords,
        IReadOnlyList<Destination> catalog)
        : base(LoadState.Ready)
    {
        Navigation = navigation;
        Toasts = toasts;
        Updates = updates;
        _session = session;
        _flow = flow;
        _serverStatus = serverStatus;
        _passwords = passwords;
        _catalog = catalog;

        var allowed = catalog.Where(d => d.IsAllowedFor(session.User)).ToList();
        Groups =
        [
            .. allowed.GroupBy(d => d.Group).Select(g => new NavGroup(g.Key, [.. g.Select(d => new NavItem(d))])),
        ];

        // Concurrent on purpose: the sidebar stays usable while a screen loads, and the newer click supersedes the older one (SHELL-07).
        NavigateCommand = Command((parameter, _) => parameter is NavItem item ? OpenAsync(item.Destination) : Task.CompletedTask, allowConcurrent: true);
        ProfileCommand = Command(_ => NavigateToTypeAsync(NavigationCatalog.ProfileViewModelType, "profile"));
        ChangePasswordCommand = Command(_ => NavigateToTypeAsync(NavigationCatalog.ProfileViewModelType, "change-password"));
        SignOutCommand = Command(_ => _flow.SignOutAsync());
        ApplyUpdateCommand = Command(ct => Updates.ApplyAsync(ct));
        BackToDashboardCommand = Command(_ => OpenAsync(_catalog[0]));
        SearchCommand = Command(_ =>
        {
            Navigation.Current?.RequestFocusSearch();
            return Task.CompletedTask;
        });
        NewRecordCommand = Command(async _ =>
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

    /// <summary>True when the page on screen has unsaved changes.</summary>
    public bool HasUnsavedChanges => Navigation.HasUnsavedChanges;

    /// <summary>Called when the main window is about to close: false means the user chose to stay (SHELL-24).</summary>
    public Task<bool> ConfirmCloseAsync() => Navigation.ConfirmDiscardAsync();

    /// <summary>
    /// Opens the first destination and checks for an update in the background. After an admin reset the person must choose a new
    /// password first: the required change-password dialog opens before any screen, and the only ways out of it are changing the
    /// password or signing out (a dismissed dialog signs out).
    /// </summary>
    public async Task StartAsync()
    {
        _ = Updates.CheckAsync();
        if (_session.User?.MustChangePassword == true && !await _passwords.PromptAsync(required: true).ConfigureAwait(true))
        {
            await _flow.SignOutAsync("Choose a new password to continue. Sign in with the temporary password you were given.", force: true).ConfigureAwait(true);
            return;
        }

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
    }

    private async Task NavigateToTypeAsync(Type type, object parameter) => await Navigation.NavigateTo(type, parameter).ConfigureAwait(true);

    private void OnCurrentChanged(object? sender, EventArgs e)
    {
        // The highlight follows the page on screen, whoever opened it: a sidebar click, a dashboard card or the account menu.
        var page = Navigation.Current?.GetType();
        foreach (var item in Items)
        {
            item.IsSelected = item.Destination.ViewModelType == page;
        }

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
