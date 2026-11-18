using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Session;
using Lifestream.Client.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lifestream.Client.Navigation;

/// <summary>The window side of <see cref="IAppFlow"/>; the Desktop project implements it with real windows.</summary>
public interface IWindowHost
{
    void ShowLogin(LoginViewModel viewModel);

    void CloseLogin();

    void ShowMain(ShellViewModel viewModel);

    void CloseMain();
}

/// <summary>
/// The single owner of window lifetime and sign-out (SHELL-17). Sign-in swaps the login window for the main window;
/// sign-out clears the session, closes the main window, disposes its view model and shows the login window again, once.
/// </summary>
public interface IAppFlow
{
    /// <summary>Shows the login window and warms the API up.</summary>
    void Start(string? notice = null);

    Task SignedInAsync();

    Task SignOutAsync(string? notice = null);
}

public sealed partial class AppFlow(IServiceProvider services, ISessionService session, ILifestreamApiClient api, IWindowHost windows, IListViewStateStore listState, ILogger<AppFlow> logger) : IAppFlow
{
    private LoginViewModel? _login;
    private ShellViewModel? _shell;
    private int _signingOut;

    public void Start(string? notice = null)
    {
        _login = services.GetRequiredService<LoginViewModel>();
        _login.Notice = notice;
        windows.ShowLogin(_login);
        _ = api.WarmUpAsync();
    }

    public async Task SignedInAsync()
    {
        _shell = services.GetRequiredService<ShellViewModel>();
        windows.ShowMain(_shell);
        windows.CloseLogin();
        _login?.Dispose();
        _login = null;
        await _shell.StartAsync().ConfigureAwait(true);
    }

    public Task SignOutAsync(string? notice = null)
    {
        if (_shell is null || Interlocked.Exchange(ref _signingOut, 1) == 1)
        {
            return Task.CompletedTask; // logout runs once: a second call, or a call with nothing to sign out of, does nothing
        }

        // The refresh token is taken before the session is cleared; the server is told afterwards, in the background (SHELL-17).
        var refreshToken = session.RefreshToken;
        try
        {
            // Fail closed: every step runs even if an earlier one throws, so the session never survives a sign-out,
            // and nothing here waits on the network (a waking server can take 120 s to answer).
            Guarded(session.Clear);
            Guarded(listState.Clear);
            Guarded(windows.CloseMain);
            Guarded(() => _shell?.Dispose());
            _shell = null;
            Guarded(() => Start(notice));
        }
        finally
        {
            Volatile.Write(ref _signingOut, 0);
        }

        if (refreshToken is not null)
        {
            _ = RevokeAsync(refreshToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>Fire and forget: one attempt, 5 s, no retries. A failure is logged and never shown; the local session is already gone.</summary>
    private async Task RevokeAsync(string refreshToken)
    {
        try
        {
            var result = await api.LogoutAsync(refreshToken).ConfigureAwait(false);
            if (!result.IsOk)
            {
                LogLogoutFailed(logger, null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogLogoutFailed(logger, ex);
        }
    }

    private void Guarded(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSignOutStepFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server could not be told about the sign-out; the local session was already cleared")]
    private static partial void LogLogoutFailed(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A sign-out step failed; the remaining steps still ran")]
    private static partial void LogSignOutStepFailed(ILogger logger, Exception exception);
}
