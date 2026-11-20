using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Updates;

namespace Lifestream.Client.Shell;

/// <summary>One sidebar button.</summary>
public sealed partial class NavItem(Destination destination, int position) : ObservableObject
{
    public NavItem(Destination destination)
        : this(destination, 0)
    {
    }

    public Destination Destination { get; } = destination;

    public string Title => Destination.Title;

    public string IconGlyph => Destination.IconGlyph;

    /// <summary>The vector icon (Controls/IconKind name in the Desktop).</summary>
    public string Icon => Destination.Icon;

    /// <summary>"Alt+1" to "Alt+8": the shortcut shown in the rail's tooltip and in the palette.</summary>
    public string Shortcut => position is >= 1 and <= 9 ? $"Alt+{position}" : string.Empty;

    /// <summary>The tooltip of the icon rail: the title and the shortcut.</summary>
    public string ToolTip => Shortcut.Length == 0 ? Title : $"{Title}  {Shortcut}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>A count shown at the right of the item (appointments still to come today, open requests, groups below minimum); null hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(RailBadge))]
    public partial string? Badge { get; set; }

    /// <summary>The count as the icon rail shows it, on a small badge at the icon's corner: the number, or "99+" for a count of three digits or more.</summary>
    public string? RailBadge => Badge is { Length: > 2 } ? "99+" : Badge;

    /// <summary>Something here needs attention (stock below its minimum): the count turns red.</summary>
    [ObservableProperty]
    public partial bool HasAlert { get; set; }

    /// <summary>"Requests, 40 open" for screen readers.</summary>
    public string AutomationName => Badge is null ? Title : $"{Title}, {Badge} {BadgeNoun}";

    /// <summary>What the badge counts, for <see cref="AutomationName"/>.</summary>
    public string BadgeNoun { get; init; } = string.Empty;
}

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

/// <summary>
/// The main window's view model (SHELL-17..23): sidebar, account menu, window title, toasts, the "update ready" prompt, and
/// the reaction to a demo reset. It owns no data of its own; the page in <see cref="Navigation"/> does.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly ILifestreamApiClient? _api;
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
        IChangePasswordPrompt passwords,
        ILifestreamApiClient? api = null,
        UiPreferences? preferences = null)
        : this(navigation, session, flow, toasts, serverStatus, updates, passwords, NavigationCatalog.Default, api, preferences)
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
        IReadOnlyList<Destination> catalog,
        ILifestreamApiClient? api = null,
        UiPreferences? preferences = null)
        : base(LoadState.Ready)
    {
        _api = api;
        Preferences = preferences ?? new UiPreferences(new MemoryPreferencesStore());
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
            .. allowed.GroupBy(d => d.Group).Select(g => new NavGroup(g.Key, [.. g.Select(d => new NavItem(d, allowed.IndexOf(d) + 1) { BadgeNoun = d.BadgeNoun })])),
        ];
        Palette = new CommandPaletteViewModel(this, api);
        Notifications = new NotificationsViewModel(api, item => OpenByTitleAsync(item.Destination, item.Prefilter), () => OpenAsync(_catalog[0]));
        Notifications.PropertyChanged += OnNotificationsChanged;

        // Concurrent on purpose: the sidebar stays usable while a screen loads, and the newer click supersedes the older one (SHELL-07).
        NavigateCommand = Command((parameter, _) => parameter is NavItem item ? OpenAsync(item.Destination) : Task.CompletedTask, allowConcurrent: true);
        ProfileCommand = Command(_ => NavigateToTypeAsync(NavigationCatalog.ProfileViewModelType, "profile"));
        ChangePasswordCommand = Command(_ => ChangePasswordAsync());
        SignOutCommand = Command(_ => _flow.SignOutAsync());
        ApplyUpdateCommand = Command(async ct =>
        {
            // Applying the update restarts the app at once, which would throw away a form with unsaved changes: ask first, like sign-out and closing the window (SHELL-24).
            if (await Navigation.ConfirmDiscardAsync().ConfigureAwait(true))
            {
                await Updates.ApplyAsync(ct).ConfigureAwait(true);
            }
        });
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

        ToggleNavCommand = new AsyncRelayCommand(_ =>
        {
            Preferences.IsNavCollapsed = !Preferences.IsNavCollapsed;
            return Task.CompletedTask;
        });
        OpenPaletteCommand = new AsyncRelayCommand(_ =>
        {
            Palette.Open();
            return Task.CompletedTask;
        });
        SetThemeCommand = new AsyncRelayCommand((parameter, _) =>
        {
            if (parameter is ThemeMode mode || Enum.TryParse(parameter as string, out mode))
            {
                Preferences.Theme = mode;
            }

            return Task.CompletedTask;
        });
        NavigateToIndexCommand = Command((parameter, _) =>
        {
            var index = parameter switch
            {
                int i => i,
                string text when int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var i) => i,
                _ => 0,
            };
            var items = Items.ToList();
            return index >= 1 && index <= items.Count ? OpenAsync(items[index - 1].Destination) : Task.CompletedTask;
        }, allowConcurrent: true);
        BookAppointmentCommand = Command(_ => BookAppointmentAsync());
        RecordDonationCommand = Command(_ => RecordDonationAsync());
        Preferences.PropertyChanged += OnPreferencesChanged;

        CancelToastActionCommand = new AsyncRelayCommand(_ =>
        {
            Toasts.Actions.Cancel();
            return Task.CompletedTask;
        });
        Toasts.Actions.Changed += OnToastActionsChanged;
        navigation.CurrentChanged += OnCurrentChanged;
        serverStatus.DemoResetObserved += OnDemoReset;
        serverStatus.Changed += OnServerStatusChanged;
        updates.PropertyChanged += (_, _) => OnPropertyChanged(nameof(UpdatePrompt));
    }

    public INavigationService Navigation { get; }

    /// <summary>
    /// True while the action of a toast (Undo, View, Reload) waits on a waking server for more than 3 s. The toast outlives the screen that showed it, so
    /// this notice lives in the shell and shows on whatever screen is open (SHELL-07).
    /// </summary>
    public bool IsToastActionWaking => Toasts.Actions.IsWaking;

    /// <summary>The "Cancel" of the shell-level waking notice: stops the toast action's HTTP request.</summary>
    [NoOperationRunner("It only cancels the toast actions' runner; it makes no API call.")]
    public AsyncRelayCommand CancelToastActionCommand { get; }

    public IToastService Toasts { get; }

    /// <summary>Theme, density, the collapsed sidebar: the person's display choices on this PC.</summary>
    public UiPreferences Preferences { get; }

    /// <summary>The command palette (Ctrl+K).</summary>
    public CommandPaletteViewModel Palette { get; }

    /// <summary>The bell's flyout: what needs attention now, each item with its action.</summary>
    public NotificationsViewModel Notifications { get; }

    /// <summary>
    /// True when the bell shows its dot: something needs attention. Until the list is known (the dashboard has loaded, or the flyout was opened) it
    /// goes by the one thing the shell counts itself, the blood groups below their minimum.
    /// </summary>
    public bool HasNotifications => Notifications.IsKnown ? Notifications.Count > 0 : LowStockGroups > 0;

    /// <summary>The bell's name and tooltip: "Notifications, 4 items need attention".</summary>
    public string NotificationsSummary => Notifications.IsKnown ? Notifications.Summary : LowStockGroups switch
    {
        0 => "Notifications",
        1 => "Notifications, 1 blood group is below its minimum",
        var low => $"Notifications, {low} blood groups are below their minimum",
    };

    partial void OnLowStockGroupsChanged(int value) => RaiseNotifications();

    private void OnNotificationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotificationsViewModel.Summary) or nameof(NotificationsViewModel.Count) or nameof(NotificationsViewModel.IsKnown))
        {
            RaiseNotifications();
        }
        else if (e.PropertyName == nameof(NotificationsViewModel.IsOpen) && Notifications.IsOpen)
        {
            AttachChild(Notifications); // the flyout's load shows the waking state and uses the app clock, like every screen's
        }
    }

    private void RaiseNotifications() =>
        Post(() =>
        {
            OnPropertyChanged(nameof(HasNotifications));
            OnPropertyChanged(nameof(NotificationsSummary));
        });

    private Features.Dashboard.DashboardViewModel? _dashboard;

    /// <summary>The dashboard on screen hands its "Needs attention" list to the bell each time it has loaded, so the dot is right with no extra calls.</summary>
    private void WatchDashboard()
    {
        if (_dashboard is not null)
        {
            _dashboard.PropertyChanged -= OnDashboardChanged;
        }

        _dashboard = Navigation.Current as Features.Dashboard.DashboardViewModel;
        if (_dashboard is not null)
        {
            _dashboard.PropertyChanged += OnDashboardChanged;
            PublishDashboardAttention();
        }
    }

    private void OnDashboardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(State))
        {
            PublishDashboardAttention();
        }
    }

    private void PublishDashboardAttention()
    {
        if (_dashboard is { State: LoadState.Ready or LoadState.Empty } dashboard && dashboard.AttentionIsComplete)
        {
            Notifications.Publish([.. dashboard.Attention]);
        }
    }

    /// <summary>Ctrl+B: the sidebar becomes an icon rail, or a full sidebar again (remembered).</summary>
    [NoOperationRunner("It flips a local preference; it makes no API call.")]
    public AsyncRelayCommand ToggleNavCommand { get; }

    /// <summary>Ctrl+K: opens the command palette.</summary>
    [NoOperationRunner("It opens the palette; the palette's own search runs its calls.")]
    public AsyncRelayCommand OpenPaletteCommand { get; }

    /// <summary>Light, Dark or System (the parameter is a <see cref="ThemeMode"/> or its name).</summary>
    [NoOperationRunner("It changes a local preference; it makes no API call.")]
    public AsyncRelayCommand SetThemeCommand { get; }

    /// <summary>Alt+1 to Alt+8: opens the n-th sidebar destination (1-based).</summary>
    public AsyncRelayCommand NavigateToIndexCommand { get; }

    /// <summary>The dashboard's and the palette's "Book appointment": the Appointments screen with its new-appointment form open.</summary>
    public AsyncRelayCommand BookAppointmentCommand { get; }

    /// <summary>
    /// Ctrl+D, the dashboard's and the palette's "Record donation": opens today's next pending appointment on the Appointments screen and starts recording
    /// the donation for it; when nobody is booked for the rest of today, it opens today's appointments and says so.
    /// </summary>
    public AsyncRelayCommand RecordDonationCommand { get; }

    /// <summary>True when the sidebar is an icon rail by the person's choice (a compact window shows the rail regardless).</summary>
    public bool IsNavCollapsed => Preferences.IsNavCollapsed;

    /// <summary>Groups of the sidebar below their minimum stock; the taskbar shows it as an overlay badge. Zero hides the badge.</summary>
    [ObservableProperty]
    public partial int LowStockGroups { get; set; }

    /// <summary>True while a call waits on a waking server: the taskbar button shows indeterminate progress.</summary>
    public bool IsServerWaking => _serverStatus.State == ServerState.Waking;

    /// <summary>First name for the dashboard greeting ("Good morning, Hana").</summary>
    public string FirstName => DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>"Doctor, morning shift" under the name at the foot of the sidebar (from the profile); the role until the profile has loaded.</summary>
    public string UserSubtitle => _profileSubtitle ?? (_session.User is { } user ? (IsDemo ? "Demo account" : user.Role.ToString()) : string.Empty);

    private string? _profileSubtitle;

    /// <summary>The shell is running (StartAsync has opened the first screen): only then does it read counts in the background.</summary>
    private bool _started;

    private DateTimeOffset _lastCounts = DateTimeOffset.MinValue;

    /// <summary>How often a navigation may refresh the sidebar counts.</summary>
    public static readonly TimeSpan CountsInterval = TimeSpan.FromSeconds(30);

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

    /// <summary>"Lifestream, Donors": updated on every navigation, so the taskbar thumbnail and Alt+Tab say which screen is open (SHELL-23).</summary>
    public string WindowTitle => string.IsNullOrEmpty(PageTitle) ? "Lifestream" : $"Lifestream, {PageTitle}";

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

    /// <summary>False for the demo account, whose password the server will not change: the account menu and the palette then do not offer "Change password".</summary>
    public bool CanChangePassword => !IsDemo;

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
        _started = true;
        _ = RefreshCountsAsync();
        _ = LoadProfileLineAsync();
    }

    /// <summary>Reads the profile once for the sidebar's "Doctor, morning shift" line; quiet on failure.</summary>
    private async Task LoadProfileLineAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await Operations.RunAsync(LoadProfileLineCoreAsync).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // The role stays under the name.
        }
    }

    [RunsInOperation("LoadProfileLineAsync runs it inside Operations.RunAsync.")]
    private async Task LoadProfileLineCoreAsync(CancellationToken ct)
    {
        var profile = await _api!.GetProfileAsync(ct).ConfigureAwait(true);
        if (profile.IsOk)
        {
            _profileSubtitle = $"{profile.Value.Designation}, " + profile.Value.Shift switch
            {
                Domain.Shift.Morning => "morning shift",
                Domain.Shift.Evening => "evening shift",
                _ => "full time",
            };
            Post(() => OnPropertyChanged(nameof(UserSubtitle)));
        }
    }

    /// <summary>
    /// Refreshes the sidebar counts and the taskbar badge, computed here by the shell (never by whichever page loaded last), on every screen alike:
    /// Appointments counts today's pending appointments (the whole local day: the ones of the Today view still to record), Requests the open requests (the
    /// Requests page's "Open" view: every pending request), and Stock shows its alert when any blood group is below its minimum.
    /// Best effort and quiet: a failed call leaves the last counts in place (the screens report their own errors).
    /// </summary>
    public async Task RefreshCountsAsync(CancellationToken ct = default)
    {
        if (_api is null || !_started)
        {
            return;
        }

        _lastCounts = Clock.GetUtcNow();

        try
        {
            await Operations.RunAsync(RefreshCountsCoreAsync, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Counts are a hint; the screen that owns the data shows any error.
        }
    }

    [RunsInOperation("RefreshCountsAsync runs it inside Operations.RunAsync.")]
    private async Task RefreshCountsCoreAsync(CancellationToken ct)
    {
        var api = _api!;
        var appointments = await api.GetAppointmentsAsync(Features.Appointments.LocalDay.Of(Clock).Query(Domain.AppointmentStatus.Pending, pageSize: 1), ct).ConfigureAwait(true);
        var requests = await api.GetRequestsAsync(new Contracts.Requests.RequestQuery(Status: Domain.RequestStatus.Pending, PageSize: 1), ct).ConfigureAwait(true);
        var stock = await api.GetStockAsync(ct).ConfigureAwait(true);
        Post(() =>
        {
            SetBadge("Appointments", appointments.IsOk ? appointments.Value.Total : null, alert: false);
            SetBadge("Requests", requests.IsOk ? requests.Value.Total : null, alert: false);
            if (stock.IsOk)
            {
                LowStockGroups = stock.Value.Count(i => i.BelowThreshold);
                SetBadge("Stock", LowStockGroups, alert: LowStockGroups > 0);
            }
        });
    }

    /// <summary>Runs an action on the UI thread the shell was created on (the palette's search results arrive from a background continuation).</summary>
    internal void RunOnUi(Action action) => Post(action);

    private void SetBadge(string title, int? count, bool alert)
    {
        if (Items.FirstOrDefault(i => i.Title == title) is { } item)
        {
            item.Badge = count is > 0 ? count.Value.ToString(System.Globalization.CultureInfo.CurrentCulture) : null;
            item.HasAlert = alert;
        }
    }

    /// <summary>
    /// Opens a sidebar destination by its title ("Donors"), with an optional parameter for the screen. False when the role cannot see it or the
    /// navigation did not happen (the person chose to keep unsaved changes).
    /// </summary>
    public async Task<bool> OpenByTitleAsync(string title, object? parameter = null)
    {
        var destination = Items.Select(i => i.Destination).FirstOrDefault(d => d.Title == title);
        if (destination is null)
        {
            return false;
        }

        if (parameter is null)
        {
            await OpenAsync(destination).ConfigureAwait(true);
            return Navigation.Current?.GetType() == destination.ViewModelType;
        }

        return await Navigation.NavigateTo(destination.ViewModelType, parameter).ConfigureAwait(true);
    }

    private async Task BookAppointmentAsync()
    {
        if (await OpenByTitleAsync("Appointments").ConfigureAwait(true) && Navigation.Current?.NewCommand is { } create)
        {
            await create.ExecuteAsync().ConfigureAwait(true);
        }
    }

    private async Task RecordDonationAsync()
    {
        var next = await FindNextAppointmentAsync().ConfigureAwait(true);
        if (next is null)
        {
            if (await OpenByTitleAsync("Appointments", new Features.ListScreens.ListPrefilter(Status: "Pending")).ConfigureAwait(true))
            {
                Toasts.Show(ToastKind.Info, "Nobody is booked for the rest of today. Choose an appointment to record a donation.");
            }

            return;
        }

        if (await OpenByTitleAsync("Appointments", new Features.ListScreens.ListPrefilter(RecordId: next.Id)).ConfigureAwait(true)
            && Navigation.Current is Features.Appointments.AppointmentsViewModel page)
        {
            await page.PendingDetail.ConfigureAwait(true);
            if (page.RecordDonationCommand.CanExecute(null))
            {
                await page.RecordDonationCommand.ExecuteAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>Today's earliest pending appointment that has not started more than an hour ago, or null.</summary>
    [RunsInOperation("RecordDonationCommand runs RecordDonationAsync, which calls it, through the shell's operation runner.")]
    private async Task<Contracts.Appointments.AppointmentListItem?> FindNextAppointmentAsync()
    {
        if (_api is null)
        {
            return null;
        }

        var day = Features.Appointments.LocalDay.Of(Clock);
        var result = await _api.GetAppointmentsAsync(day.Query(Domain.AppointmentStatus.Pending, sort: Contracts.SortKeys.Slot)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            return null;
        }

        var today = result.Value.Items.Where(day.Holds).ToList();
        var earliest = Clock.GetUtcNow().UtcDateTime.AddHours(-1);
        return today.Where(a => a.SlotStartUtc >= earliest).OrderBy(a => a.SlotStartUtc).FirstOrDefault()
            ?? today.OrderBy(a => a.SlotStartUtc).FirstOrDefault();
    }

    private void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UiPreferences.IsNavCollapsed))
        {
            OnPropertyChanged(nameof(IsNavCollapsed));
        }
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

    /// <summary>
    /// "Change password" in the account menu and the palette. On the Profile screen it opens the dialog over the screen as it is: opening the screen again
    /// would load the profile a second time and throw away what the person was doing there. From any other screen it opens the profile first, and the
    /// dialog once the profile is on screen.
    /// </summary>
    private async Task ChangePasswordAsync()
    {
        if (!CanChangePassword)
        {
            // The demo account is never offered this (the menu and the palette leave it out). Asked anyway, it gets the profile, whose Security
            // section says why the password cannot be changed, and no dialog.
            await NavigateToTypeAsync(NavigationCatalog.ProfileViewModelType, "profile").ConfigureAwait(true);
            return;
        }

        if (Navigation.Current is ProfileViewModel profile)
        {
            if (profile.ChangePasswordCommand.CanExecute(null))
            {
                await profile.ChangePasswordCommand.ExecuteAsync().ConfigureAwait(true);
            }

            return;
        }

        await NavigateToTypeAsync(NavigationCatalog.ProfileViewModelType, ProfileViewModel.ChangePasswordParameter).ConfigureAwait(true);
    }

    private void OnServerStatusChanged(object? sender, EventArgs e) => Post(() => OnPropertyChanged(nameof(IsServerWaking)));

    private void OnToastActionsChanged(object? sender, EventArgs e) => Post(() => OnPropertyChanged(nameof(IsToastActionWaking)));

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
        Notifications.Close(); // a flyout never stays open over a screen it was not opened on
        WatchDashboard();
        if (Clock.GetUtcNow() - _lastCounts >= CountsInterval)
        {
            _ = RefreshCountsAsync();
        }
    }

    private void OnDemoReset(object? sender, EventArgs e)
    {
        void Handle()
        {
            if (Navigation.HasUnsavedChanges)
            {
                // Typed input on Profile or an open editor is never replaced without asking: keep it, and let the user reload when they are ready (SHELL-24).
                Toasts.Show(
                    ToastKind.Info,
                    "Demo data was reset. You have unsaved changes on this screen, so it has not been refreshed.",
                    "Reload",
                    async () =>
                    {
                        if (await Navigation.ConfirmDiscardAsync().ConfigureAwait(true))
                        {
                            await Navigation.ReloadCurrentAsync().ConfigureAwait(true);
                        }
                    });
                return;
            }

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
            Notifications.PropertyChanged -= OnNotificationsChanged;
            if (_dashboard is not null)
            {
                _dashboard.PropertyChanged -= OnDashboardChanged;
                _dashboard = null;
            }

            Notifications.Dispose();
            Preferences.PropertyChanged -= OnPreferencesChanged;
            _serverStatus.Changed -= OnServerStatusChanged;
            Toasts.Actions.Changed -= OnToastActionsChanged;
            _serverStatus.DemoResetObserved -= OnDemoReset;
            Navigation.Reset();
        }

        base.Dispose(disposing);
    }
}
