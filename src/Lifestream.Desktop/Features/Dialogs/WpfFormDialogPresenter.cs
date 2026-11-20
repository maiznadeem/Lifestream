using System.Windows;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Features.Dialogs;

/// <summary>Shows a form dialog as a modal window over the active window, on the UI thread. It closes when the view model asks to close.</summary>
public sealed class WpfFormDialogPresenter(Func<Window?> ownerProvider) : IFormDialogPresenter
{
    public Task ShowAsync(DialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? Show(viewModel) : dispatcher.InvokeAsync(() => Show(viewModel)).Task;
    }

    private Task Show(DialogViewModel viewModel)
    {
        var window = new FormDialogWindow { DataContext = viewModel };
        void Close(object? sender, EventArgs e) => window.Close();
        viewModel.CloseRequested += Close;
        try
        {
            var owner = ownerProvider();
            if (owner is { IsVisible: true } && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            window.ShowDialog();
        }
        finally
        {
            viewModel.CloseRequested -= Close;
        }

        return Task.CompletedTask;
    }
}
