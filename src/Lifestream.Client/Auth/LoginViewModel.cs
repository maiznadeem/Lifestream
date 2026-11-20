using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Updates;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;

namespace Lifestream.Client.Auth;

/// <summary>What the sign-in screen knows about the server before anyone signs in (the pill in its footer).</summary>
public enum ServerReadiness
{
    Checking,
    Ready,
    Waking,
    Unreachable,

    /// <summary>The demo database has used up its free allowance and is paused until next month: waiting does not help.</summary>
    Resting,
}

/// <summary>One cell of the sign-in screen's red-cell compatibility matrix: can the row's donor give to the column's patient.</summary>
public sealed record CompatibilityCell(BloodType Donor, BloodType Patient, bool CanGive)
{
    /// <summary>The universal donor's row (O\u2212) and the universal recipient's column (AB+) are drawn at full strength.</summary>
    public bool IsUniversal => Donor == BloodType.ONeg || Patient == BloodType.ABPos;

    public string AutomationName => $"{Donor.ToTypographic()} to {Patient.ToTypographic()}: {(CanGive ? "compatible" : "not compatible")}";
}

/// <summary>One donor row of the compatibility matrix.</summary>
public sealed record CompatibilityRow(BloodType Donor, IReadOnlyList<CompatibilityCell> Cells)
{
    public string Label => Donor.ToTypographic();

    /// <summary>What a screen reader calls the row.</summary>
    public string AutomationName => $"Donor {Label}";
}

/// <summary>What the login screen is telling the user about the last attempt.</summary>
public enum LoginProblem
{
    None,
    WrongCredentials,
    LockedOut,
    ServerUnreachable,
    ServerWaking,
    DemoResting,
    Other,
}

/// <summary>
/// The login screen (SHELL-02..06). It starts empty, "Sign in" is disabled until both fields have text, a failed attempt
/// shows an inline message with Retry where it helps and re-enables the button, and nothing here can end the process.
/// There is deliberately no "Forgot password": the API has no endpoint that could make it work (SHELL-06).
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly ILifestreamApiClient _api;
    private readonly ISessionService _session;
    private readonly IAppFlow _flow;
    private readonly IServerStatus? _serverStatus;
    private readonly UpdateService? _updates;
    private readonly UiPreferences? _preferences;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public LoginViewModel(ILifestreamApiClient api, ISessionService session, IAppFlow flow, IServerStatus? serverStatus = null, UpdateService? updates = null, UiPreferences? preferences = null)
        : base(LoadState.Ready)
    {
        _preferences = preferences;
        _api = api;
        _session = session;
        _flow = flow;
        _serverStatus = serverStatus;
        _updates = updates;
        if (updates is not null)
        {
            updates.PropertyChanged += OnUpdatesChanged;
        }

        ApplyUpdateCommand = Command(ct => _updates?.ApplyAsync(ct) ?? Task.CompletedTask);
        if (serverStatus is not null)
        {
            serverStatus.Changed += OnServerStatusChanged;
            _phase = serverStatus.Phase;
            IsServerWaking = serverStatus.State == ServerState.Waking;
        }

        SignInCommand = Command(SignInAsync, () => CanSignIn);
        DemoCommand = Command(DemoAsync, () => !IsBusy);
        RetryCommand = Command(RetryAsync, () => Problem is LoginProblem.ServerUnreachable or LoginProblem.ServerWaking);
        CancelCommand = Command(_ =>
        {
            // One cancel for every sign-in path (Sign in, Demo and Retry each run on their own command): whichever one is running is stopped,
            // down to the HTTP request (SHELL-07). The update download of the same screen is not a sign-in and is left alone.
            SignInCommand.Cancel();
            DemoCommand.Cancel();
            RetryCommand.Cancel();
            return Task.CompletedTask;
        });
        if (preferences is { RememberUsername: true, Username: { Length: > 0 } remembered })
        {
            Username = remembered;
        }
    }

    public override string PageTitle => "Lifestream";

    /// <summary>The ABO/RhD red-cell compatibility of every donor group (rows) with every patient group (columns), from the domain rule.</summary>
    public static IReadOnlyList<CompatibilityRow> Compatibility { get; } =
    [
        .. BloodTypeExtensions.All.Select(donor => new CompatibilityRow(donor, [.. BloodTypeExtensions.All.Select(patient => new CompatibilityCell(donor, patient, donor.CanGiveTo(patient)))])),
    ];

    /// <summary>The column headings of the matrix (patient groups).</summary>
    public static IReadOnlyList<string> PatientGroups { get; } = [.. BloodTypeExtensions.All.Select(t => t.ToTypographic())];

    /// <summary>Fill in the user name next time (saved on this PC; never the password).</summary>
    public bool RememberUsername
    {
        get => _preferences?.RememberUsername ?? false;
        set
        {
            if (_preferences is not null && _preferences.RememberUsername != value)
            {
                _preferences.RememberUsername = value;
                if (!value)
                {
                    _preferences.Username = null;
                }

                OnPropertyChanged();
            }
        }
    }

    /// <summary>True when the screen can remember the user name (a preferences store exists).</summary>
    public bool CanRememberUsername => _preferences is not null;

    /// <summary>The footer pill: checking, ready, waking or unreachable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerText))]
    public partial ServerReadiness Server { get; set; } = ServerReadiness.Checking;

    /// <summary>"Server ready" and its siblings, for the footer pill.</summary>
    public string ServerText => Server switch
    {
        ServerReadiness.Ready => "Server ready",
        // While a call waits, the pill names the part of the wake that is going on; a server that answered "still waking" means its database.
        ServerReadiness.Waking when IsServerWaking && _phase == WakePhase.ServerStarting => "Starting the server",
        ServerReadiness.Waking when IsServerWaking && _phase == WakePhase.ServerWorking => "Waiting for the server",
        ServerReadiness.Waking => "Waking the database",
        ServerReadiness.Unreachable => "Can't reach the server",
        ServerReadiness.Resting => "Demo database paused until the 1st",
        _ => "Checking the server",
    };

    /// <summary>
    /// Asks the server whether it is ready, for the footer pill (the screen calls it once when it appears). The call also starts the server's
    /// own warm-up for the first sign-in. A cold demo server answers "waking" until its database is back, about a minute and a half at the
    /// longest measured, which the status line says; the pill turns to "Server ready" within about two seconds of the database answering.
    /// </summary>
    public async Task CheckServerAsync(CancellationToken ct = default)
    {
        try
        {
            // A server that still says "waking" when the two-minute wait ends is asked once more, so a wake that runs long ends in
            // "Server ready" by itself. Then the pill is left on "waking": asking for ever would knock on a database that is not coming back.
            for (var round = 1; ; round++)
            {
                Server = await Operations.RunAsync(ReadReadinessAsync, ct).ConfigureAwait(true);
                if (Server != ServerReadiness.Waking || round >= ReadinessRounds)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Server = ServerReadiness.Unreachable;
        }
    }

    /// <summary>How many two-minute waits the readiness check of the sign-in window makes while the server keeps answering "still waking".</summary>
    public const int ReadinessRounds = 2;

    /// <summary>True from the third second of a slow or waking call until it ends: the server is starting from cold (SHELL-07).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool IsServerWaking { get; set; }

    /// <summary>
    /// What the waking notice says: how long a wake takes, and, only once this one has lasted longer than that
    /// (<see cref="WakingTexts.LongerThanUsualAfter"/>), that it is taking longer than usual. It never reads as a failure: the attempt is still running.
    /// </summary>
    public string WakingText =>
        _wakingLongerThanUsual ? WakingTexts.SignInLate
        : _phase == WakePhase.DatabaseWaking ? WakingTexts.SignInDatabase
        : _phase == WakePhase.ServerWorking ? WakingTexts.SignInWorking
        : WakingTexts.SignIn;

    /// <summary>The part of the wake the server is in, as last heard (<see cref="IServerStatus.Phase"/>).</summary>
    private WakePhase _phase;

    private bool _wakingLongerThanUsual;
    private ITimer? _wakingClock;

    partial void OnIsServerWakingChanged(bool value)
    {
        OnPropertyChanged(nameof(ServerText));
        _wakingClock?.Dispose();
        _wakingClock = null;
        if (_wakingLongerThanUsual)
        {
            _wakingLongerThanUsual = false;
            OnPropertyChanged(nameof(WakingText));
        }

        var now = Clock.GetTimestamp();
        if (!value)
        {
            _wakingEnded = now;
            return;
        }

        // One wake can be several waits in a row (the readiness check, then a sign-in): the time counts from the first of them.
        if (_wakingBegan is null || _wakingEnded is not { } ended || Clock.GetElapsedTime(ended, now) > SameWakeWithin)
        {
            _wakingBegan = now;
        }

        var left = WakingTexts.LongerThanUsualAfter - Clock.GetElapsedTime(_wakingBegan.Value, now);
        if (left <= TimeSpan.Zero)
        {
            _wakingLongerThanUsual = true;
            OnPropertyChanged(nameof(WakingText));
            return;
        }

        _wakingClock = Clock.CreateTimer(
            _ => Post(() =>
            {
                if (IsServerWaking && !_wakingLongerThanUsual)
                {
                    _wakingLongerThanUsual = true;
                    OnPropertyChanged(nameof(WakingText));
                }
            }),
            null,
            left,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>A wait that starts this soon after the last one ended belongs to the same wake.</summary>
    private static readonly TimeSpan SameWakeWithin = TimeSpan.FromSeconds(10);

    private long? _wakingBegan;
    private long? _wakingEnded;

    /// <summary>Cancel is offered while an attempt is waiting on a waking server.</summary>
    public bool CanCancel => IsBusy && IsServerWaking;

    public AsyncRelayCommand SignInCommand { get; }

    public AsyncRelayCommand DemoCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    /// <summary>Restarts into the downloaded update (the "update ready" prompt on the login window, SHELL-44).</summary>
    public AsyncRelayCommand ApplyUpdateCommand { get; }

    /// <summary>"Version 2.1.0 is ready. Restart to update." once the startup check found a newer version; otherwise null.</summary>
    public string? UpdatePrompt => _updates is { IsUpdateReady: true } ? _updates.Message : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(AreFieldsReadOnly))]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(AreFieldsReadOnly))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(CanRetry))]
    public partial LoginProblem Problem { get; set; }

    /// <summary>"Sign in" when idle, "Signing in..." while busy (SHELL-05).</summary>
    public string SignInText => IsBusy && _signingInWithPassword ? "Signing in..." : "Sign in";

    public string DemoText => IsBusy && !_signingInWithPassword ? "Opening the demo..." : "Try the demo";

    public bool CanSignIn => !IsBusy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);

    /// <summary>The fields turn read-only while an attempt runs, so their text cannot change under the request.</summary>
    public bool AreFieldsReadOnly => IsBusy;

    public bool HasProblem => Problem != LoginProblem.None;

    public bool CanRetry => Problem is LoginProblem.ServerUnreachable or LoginProblem.ServerWaking;

    private bool _signingInWithPassword;

    protected override void OnBusyChanged(bool isBusy)
    {
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(AreFieldsReadOnly));
        OnPropertyChanged(nameof(SignInText));
        OnPropertyChanged(nameof(DemoText));
        OnPropertyChanged(nameof(CanCancel));
        SignInCommand.RaiseCanExecuteChanged();
        DemoCommand.RaiseCanExecuteChanged();
    }

    partial void OnProblemChanged(LoginProblem value) => RetryCommand.RaiseCanExecuteChanged();

    partial void OnUsernameChanged(string value) => SignInCommand.RaiseCanExecuteChanged();

    partial void OnPasswordChanged(string value) => SignInCommand.RaiseCanExecuteChanged();

    private void OnUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            OnPropertyChanged(nameof(UpdatePrompt));
        }
        else
        {
            _context.Post(_ => OnPropertyChanged(nameof(UpdatePrompt)), null);
        }
    }

    /// <summary>True while the pill shows a wake (starting the server, waking the database, waiting) because a call of this screen is slow (not because the readiness check said so).</summary>
    private bool _pillWakingFromACall;
    private ServerReadiness _pillBeforeWaking = ServerReadiness.Checking;

    private void OnServerStatusChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            var phase = _serverStatus?.Phase ?? WakePhase.ServerStarting;
            var phaseChanged = phase != _phase;
            _phase = phase;
            IsServerWaking = _serverStatus?.State == ServerState.Waking;
            if (phaseChanged)
            {
                OnPropertyChanged(nameof(WakingText));
                OnPropertyChanged(nameof(ServerText));
            }

            if (IsServerWaking)
            {
                if (!_pillWakingFromACall)
                {
                    _pillBeforeWaking = Server;
                    _pillWakingFromACall = true;
                }

                Server = ServerReadiness.Waking;
            }
            else if (_pillWakingFromACall)
            {
                // The slow call is over: the pill goes back to what it said before it, and the call's own answer (Fail, or a sign-in) then says more.
                _pillWakingFromACall = false;
                if (Server == ServerReadiness.Waking)
                {
                    Server = _pillBeforeWaking;
                }
            }
        }

        if (_context is null || SynchronizationContext.Current == _context)
        {
            Apply();
        }
        else
        {
            _context.Post(_ => Apply(), null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _wakingClock?.Dispose();
            _wakingClock = null;
            if (_serverStatus is not null)
            {
                _serverStatus.Changed -= OnServerStatusChanged;
            }

            if (_updates is not null)
            {
                _updates.PropertyChanged -= OnUpdatesChanged;
            }
        }

        base.Dispose(disposing);
    }

    private Task SignInAsync(CancellationToken ct)
    {
        _signingInWithPassword = true;
        var request = new LoginRequest(Username.Trim(), Password);
        return AuthenticateAsync(token => _api.LoginAsync(request, token), ct);
    }

    private Task DemoAsync(CancellationToken ct)
    {
        _signingInWithPassword = false;
        return AuthenticateAsync(token => _api.DemoLoginAsync(token), ct);
    }

    private Task RetryAsync(CancellationToken ct) => _signingInWithPassword ? SignInAsync(ct) : DemoAsync(ct);

    private async Task AuthenticateAsync(Func<CancellationToken, Task<ApiResult<TokenResponse>>> call, CancellationToken ct)
    {
        // The busy state is set synchronously, before anything is awaited: it shows within the same frame as the click.
        IsBusy = true;
        Problem = LoginProblem.None;
        Message = null;
        try
        {
            var result = await call(ct).ConfigureAwait(true);
            if (result.IsOk && !IsUsable(result.Value))
            {
                // The server said 200 but the body is no sign-in (an empty object, a missing user or token): never open the main window on it.
                Fail(new ApiProblem(200, InvalidResponseCode, "The sign-in response had no usable tokens."));
                return;
            }

            if (result.IsOk)
            {
                Password = string.Empty;
                Server = ServerReadiness.Ready;
                if (_preferences is { RememberUsername: true } && _signingInWithPassword)
                {
                    _preferences.Username = Username.Trim();
                }

                _session.Establish(result.Value);
                await _flow.SignedInAsync().ConfigureAwait(true);
                return;
            }

            Fail(result.Problem!);
        }
        catch (OperationCanceledException)
        {
            Message = "Sign-in cancelled.";
            Problem = LoginProblem.Other;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or System.Text.Json.JsonException)
        {
            Fail(new ApiProblem(0, ClientProblemCodes.Unreachable, ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RunsInOperation("CheckServerAsync runs it inside Operations.RunAsync.")]
    private async Task<ServerReadiness> ReadReadinessAsync(CancellationToken ct)
    {
        var result = await _api.GetReadinessAsync(ct).ConfigureAwait(true);
        if (result.Problem is { } problem)
        {
            // A server that answered "still waking" (after the whole wait) or "resting" was reached: only no answer at all is "can't reach".
            return problem.IsWaking ? ServerReadiness.Waking
                : problem.IsResting ? ServerReadiness.Resting
                : ServerReadiness.Unreachable;
        }

        return result.Value.Database == Contracts.Health.DatabaseStates.Ready ? ServerReadiness.Ready : ServerReadiness.Waking;
    }

    /// <summary>The problem code of an answer that was successful but not a usable sign-in.</summary>
    public const string InvalidResponseCode = "invalid_sign_in_response";

    private static bool IsUsable(TokenResponse? tokens) =>
        tokens is { User: { } user }
        && !string.IsNullOrWhiteSpace(tokens.AccessToken)
        && !string.IsNullOrWhiteSpace(tokens.RefreshToken)
        && !string.IsNullOrWhiteSpace(user.DisplayName)
        && Enum.IsDefined(user.Role);

    private void Fail(ApiProblem problem)
    {
        (Problem, Message) = problem.Code switch
        {
            "unauthorized" or "invalid_credentials" => (LoginProblem.WrongCredentials, "That username and password don't match. Check them and try again."),
            ProblemCodes.LockedOut => (LoginProblem.LockedOut, "Too many failed attempts. Wait a few minutes and try again."),
            ProblemCodes.RateLimited => (LoginProblem.LockedOut, "Too many attempts from this computer. Wait a minute and try again."),
            ProblemCodes.DatabaseResting => (LoginProblem.DemoResting, WakingTexts.Resting),
            // Shown only after the attempt has waited its whole two minutes, which is longer than any wake measured: by then it is longer than usual.
            ProblemCodes.DatabaseWaking => (LoginProblem.ServerWaking, "The server is still waking up, which is taking longer than usual. Retry to keep waiting."),
            ClientProblemCodes.Unreachable => (LoginProblem.ServerUnreachable, "Can't reach the server. Check your internet connection, then retry."),
            _ when problem.Status == 401 => (LoginProblem.WrongCredentials, "That username and password don't match. Check them and try again."),
            _ => (LoginProblem.Other, "Sign-in didn't work. Try again in a moment."),
        };
        ErrorDetail = $"{problem.Status} {problem.Code}" + (problem.CorrelationId is null ? string.Empty : $" (correlation {problem.CorrelationId})");

        // The footer pill follows what this attempt learned about the server: any answer of its own (a wrong password, a lockout) means it is up.
        Server = Problem switch
        {
            LoginProblem.ServerUnreachable => ServerReadiness.Unreachable,
            LoginProblem.ServerWaking => ServerReadiness.Waking,
            LoginProblem.DemoResting => ServerReadiness.Resting,
            LoginProblem.WrongCredentials or LoginProblem.LockedOut => ServerReadiness.Ready,
            _ => Server,
        };
    }
}
