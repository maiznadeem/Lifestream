using System.ComponentModel;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Chrome;

namespace Lifestream.Desktop.Shell;

/// <summary>The main window (minimum 1024 x 640 DIP). The window chrome is the shared <see cref="ChromeWindow"/>.</summary>
public partial class MainWindow : ChromeWindow
{
    private bool _closeConfirmed;

    public MainWindow() => InitializeComponent();

    /// <summary>Closes without asking: the app itself is closing this window (sign-out has already asked).</summary>
    public void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Closing with unsaved changes asks "Discard changes?" and stays open on Cancel (SHELL-24).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel || DataContext is not ShellViewModel { HasUnsavedChanges: true } shell)
        {
            return;
        }

        // Cancel this close now and ask afterwards: the answer may arrive synchronously (a modal dialog), and closing again from
        // inside the Closing event would be ignored.
        e.Cancel = true;
        _ = Dispatcher.InvokeAsync(() => ConfirmThenCloseAsync(shell));
    }

    private async Task ConfirmThenCloseAsync(ShellViewModel shell)
    {
        if (await shell.ConfirmCloseAsync().ConfigureAwait(true))
        {
            CloseWithoutPrompt();
        }
    }
}
