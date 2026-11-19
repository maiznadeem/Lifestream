using System.Windows.Input;
using Lifestream.Client.Common;
using Lifestream.Desktop.Chrome;

namespace Lifestream.Desktop.Features.Dialogs;

/// <summary>A modal window for a <see cref="DialogViewModel"/>. It closes when the view model asks to, and Esc cancels (the Cancel button is the cancel button).</summary>
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
    }
}
