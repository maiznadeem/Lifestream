using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Updates;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;

namespace Lifestream.Client.Auth;

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
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public LoginViewModel(ILifestreamApiClient api, ISessionService session, IAppFlow flow, IServerStatus? serverStatus = null, UpdateService? updates = null)
        : base(LoadState.Ready)
    {
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
            IsServerWaking = serverStatus.State == ServerState.Waking;
        }

        SignInCommand = Command(SignInAsync, () => CanSignIn);
        DemoCommand = Command(DemoAsync, () => !IsBusy);
        RetryCommand = Command(RetryAsync, () => Problem is LoginProblem.ServerUnreachable or LoginProblem.ServerWaking);
        CancelCommand = Command(_ =>
        {
            SignInCommand.Cancel();
            DemoCommand.Cancel();
            return Task.CompletedTask;
        });
    }

    public override string PageTitle => "Lifestream";

    /// <summary>True from the third second of a slow or waking call until it ends: the server is starting from cold (SHELL-07).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool IsServerWaking { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
    public string WakingText => "Waking up the server, this can take up to a minute.";

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

    private void OnServerStatusChanged(object? sender, EventArgs e)
    {
        void Apply() => IsServerWaking = _serverStatus?.State == ServerState.Waking;
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

    /// <summary>The problem code of an answer that was successful but not a usable sign-in.</summary>
    public const string InvalidResponseCode = "invalid_sign_in_response";

    private static bool IsUsable(TokenResponse? tokens) =>
        tokens is { AccessToken.Length: > 0, RefreshToken.Length: > 0, User: { DisplayName: { Length: > 0 } } user } && Enum.IsDefined(user.Role);

    private void Fail(ApiProblem problem)
    {
        (Problem, Message) = problem.Code switch
        {
            "unauthorized" or "invalid_credentials" => (LoginProblem.WrongCredentials, "That username and password don't match. Check them and try again."),
            ProblemCodes.LockedOut => (LoginProblem.LockedOut, "Too many failed attempts. Wait a few minutes and try again."),
            ProblemCodes.RateLimited => (LoginProblem.LockedOut, "Too many attempts from this computer. Wait a minute and try again."),
            ProblemCodes.DatabaseResting => (LoginProblem.DemoResting, "The demo is resting until the start of next month. It will be back then."),
            ProblemCodes.DatabaseWaking => (LoginProblem.ServerWaking, "The server is still waking up. This can take up to a minute. Retry, or wait a moment."),
            ClientProblemCodes.Unreachable => (LoginProblem.ServerUnreachable, "Can't reach the server. Check your internet connection, then retry."),
            _ when problem.Status == 401 => (LoginProblem.WrongCredentials, "That username and password don't match. Check them and try again."),
            _ => (LoginProblem.Other, "Sign-in didn't work. Try again in a moment."),
        };
        ErrorDetail = $"{problem.Status} {problem.Code}" + (problem.CorrelationId is null ? string.Empty : $" (correlation {problem.CorrelationId})");
    }
}
