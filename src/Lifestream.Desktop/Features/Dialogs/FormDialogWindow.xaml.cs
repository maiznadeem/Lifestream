using System.Windows.Input;
using Lifestream.Client.Common;
using Lifestream.Desktop.Chrome;

namespace Lifestream.Desktop.Features.Dialogs;

/// <summary>A modal window for a <see cref="DialogViewModel"/>. It closes when the view model asks to; Esc, the Cancel button and the X all go through the view model's cancel command.</summary>
public partial class FormDialogWindow : ChromeWindow
{
    public FormDialogWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DialogViewModel vm)
            {
                Title = vm.Title;
                System.Windows.Automation.AutomationProperties.SetName(this, vm.Title);
            }
        };
        Loaded += (_, _) => MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        Closing += OnClosing;
    }

    /// <summary>
    /// The window draws no caption of its own: the dialog's title row and close button (DialogFrame) stand in for it, so the window chrome's caption
    /// buttons are hidden. Esc, the frame's close button and Alt+F4 all reach <see cref="OnClosing"/>.
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("CaptionButtons") is System.Windows.UIElement buttons)
        {
            buttons.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The window's X (and Alt+F4) takes the same road as Esc and the Cancel button: it is held back and runs the dialog's cancel command, which
    /// cancels a request in flight, asks "Discard changes?" when there is unsaved input, and only then asks this window to close (SHELL-24).
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is DialogViewModel { IsClosing: false } vm)
        {
            e.Cancel = true;

            // A window cannot be closed from inside its own Closing event, so the cancel command runs right after this handler returns.
            _ = Dispatcher.BeginInvoke(() => _ = vm.CancelCommand.ExecuteAsync());
        }
    }
}
