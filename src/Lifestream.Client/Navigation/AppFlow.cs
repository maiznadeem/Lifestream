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

    public async Task SignOutAsync(string? notice = null)
    {
        if (_shell is null || Interlocked.Exchange(ref _signingOut, 1) == 1)
        {
            return; // logout runs once: a second call, or a call with nothing to sign out of, does nothing
        }

        try
        {
            if (session.IsAuthenticated)
            {
                try
                {
                    await api.LogoutAsync().ConfigureAwait(true); // best effort: the server revokes the refresh token
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Whatever went wrong, signing out never depends on the server: it is logged and the local sign-out goes on.
                    LogLogoutFailed(logger, ex);
                }
            }
        }
        finally
        {
            // Fail closed: every step runs even if an earlier one throws, so the session never survives a sign-out.
            Guarded(session.Clear);
            Guarded(listState.Clear);
            Guarded(windows.CloseMain);
            Guarded(() => _shell?.Dispose());
            _shell = null;
            Guarded(() => Start(notice));
            Volatile.Write(ref _signingOut, 0);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server could not be told about the sign-out; signing out locally")]
    private static partial void LogLogoutFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A sign-out step failed; the remaining steps still ran")]
    private static partial void LogSignOutStepFailed(ILogger logger, Exception exception);
}
