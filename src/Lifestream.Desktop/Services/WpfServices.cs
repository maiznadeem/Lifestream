using System.Windows;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Shell;
using Velopack;
using Velopack.Sources;

namespace Lifestream.Desktop.Services;

/// <summary>Shows confirmation and error dialogs as modal windows on the UI thread. Only one error dialog is open at a time.</summary>
public sealed class WpfDialogPresenter(Func<Window?> ownerProvider) : IDialogPresenter
{
    private int _errorOpen;

    public Task<bool> ConfirmAsync(string title, string body, string dangerVerb) =>
        OnUi(() =>
        {
            var dialog = DialogWindow.CreateConfirm(title, body, dangerVerb);
            Show(dialog);
            return dialog.Confirmed;
        });

    public Task ShowErrorAsync(string title, string message, string? details)
    {
        if (Interlocked.Exchange(ref _errorOpen, 1) == 1)
        {
            return Task.CompletedTask; // a flood of errors shows one dialog, not a stack of them
        }

        return OnUi(() =>
        {
            try
            {
                Show(DialogWindow.CreateError(title, message, details));
            }
            finally
            {
                Volatile.Write(ref _errorOpen, 0);
            }

            return true;
        });
    }

    private void Show(DialogWindow dialog)
    {
        var owner = ownerProvider();
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
    }

    private static Task<bool> OnUi(Func<bool> action)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? Task.FromResult(action()) : dispatcher.InvokeAsync(action).Task;
    }
}

/// <summary>Opens and closes the login and main windows. The only code that creates a top-level window other than dialogs.</summary>
public sealed class WpfWindowHost : IWindowHost
{
    private LoginWindow? _login;
    private MainWindow? _main;
    private bool _switching;

    /// <summary>The window to use as the owner of dialogs: the topmost window owned down from the main (or sign-in) window, so a "Discard changes?" question appears over the form dialog that asked it.</summary>
    public Window? ActiveWindow
    {
        get
        {
            var top = (Window?)_main ?? _login;
            if (top is null || Application.Current is null)
            {
                return top;
            }

            while (Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && ReferenceEquals(w.Owner, top)) is { } child)
            {
                top = child;
            }

            return top;
        }
    }

    public void ShowLogin(LoginViewModel viewModel)
    {
        _login = new LoginWindow { DataContext = viewModel };
        _login.Closed += (_, _) =>
        {
            if (!_switching && _main is null)
            {
                Application.Current.Shutdown();
            }
        };
        if (LaunchProbe.Enabled)
        {
            LaunchProbe.Prepare(_login); // the launch measurement: off the desktop, never activated
            _login.Show();
            return;
        }

        _login.Show();
        _login.Activate();
    }

    public void CloseLogin()
    {
        var login = _login;
        _login = null;
        if (login is not null)
        {
            _switching = true;
            login.Close();
            _switching = false;
        }
    }

    public void ShowMain(ShellViewModel viewModel)
    {
        _main = new MainWindow { DataContext = viewModel };
        _main.Closed += (_, _) =>
        {
            if (!_switching)
            {
                Application.Current.Shutdown();
            }
        };
        _main.Show();
    }

    public void CloseMain()
    {
        var main = _main;
        _main = null;
        if (main is not null)
        {
            _switching = true;
            main.CloseWithoutPrompt();
            _switching = false;
        }
    }
}

/// <summary>Tells whether the process runs as an installed Windows package (an MSIX from the Microsoft Store).</summary>
public interface IPackageIdentityProbe
{
    bool HasPackageIdentity();
}

/// <summary>Asks Windows for the current package name: a process without package identity gets APPMODEL_ERROR_NO_PACKAGE.</summary>
public sealed partial class WindowsPackageIdentityProbe : IPackageIdentityProbe
{
    private const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE

    public bool HasPackageIdentity()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, 0) != NoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false; // Windows older than 8: no packages exist
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref int packageFullNameLength, nint packageFullName);
}

/// <summary>What the update source needs from the release feed; the real one is Velopack on GitHub Releases.</summary>
public interface IUpdateFeed
{
    /// <summary>The newest version, or null when the build was not installed by the updater or nothing newer exists.</summary>
    Task<Version?> CheckAsync(CancellationToken ct);

    Task ApplyAsync(CancellationToken ct);
}

/// <summary>GitHub Releases through Velopack. A build that was not installed by Velopack reports no update.</summary>
public sealed class VelopackFeed(string repositoryUrl) : IUpdateFeed
{
    private readonly UpdateManager _manager = new(new GithubSource(repositoryUrl, null, false));
    private Velopack.UpdateInfo? _pending;

    public async Task<Version?> CheckAsync(CancellationToken ct)
    {
        if (!_manager.IsInstalled)
        {
            return null;
        }

        _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        var target = _pending?.TargetFullRelease.Version;
        return target is null ? null : new Version(target.Major, target.Minor, target.Patch);
    }

    public async Task ApplyAsync(CancellationToken ct)
    {
        if (_pending is null)
        {
            return;
        }

        await _manager.DownloadUpdatesAsync(_pending, cancelToken: ct).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}

/// <summary>
/// The update source of the app (SHELL-44). A Microsoft Store (MSIX) install is updated by the Store, so the release feed is not
/// even created there; everywhere else the feed is checked and a newer version raises the "update ready" prompt.
/// </summary>
public sealed class StoreAwareUpdateSource(IPackageIdentityProbe packageIdentity, Func<IUpdateFeed> feedFactory) : Lifestream.Client.Updates.IUpdateSource
{
    private IUpdateFeed? _feed;

    public Task<Version?> GetLatestVersionAsync(CancellationToken ct)
    {
        if (packageIdentity.HasPackageIdentity())
        {
            return Task.FromResult<Version?>(null);
        }

        _feed ??= feedFactory();
        return _feed.CheckAsync(ct);
    }

    public Task ApplyAsync(CancellationToken ct) => _feed is null ? Task.CompletedTask : _feed.ApplyAsync(ct);
}
