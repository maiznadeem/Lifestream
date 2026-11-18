using System.Windows;
using Lifestream.Desktop.Chrome;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The one dialog window: a confirmation (SHELL-31) or an error report. A confirmation names the record in its title and body,
/// puts initial focus on Cancel, closes on Esc (Cancel is the cancel button), and labels its danger button with the verb and
/// the object, for example "Delete donor".
/// </summary>
public partial class DialogWindow : ChromeWindow
{
    public DialogWindow()
    {
        InitializeComponent();
        // Initial focus is on Cancel, so Enter or Space never confirms a destructive action by accident.
        System.Windows.Input.FocusManager.SetFocusedElement(this, CancelButton);
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>True when the user chose the primary (danger) button.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The cancel button; initial keyboard focus is here.</summary>
    public System.Windows.Controls.Button Cancel => CancelButton;

    /// <summary>The danger button.</summary>
    public System.Windows.Controls.Button Primary => PrimaryButton;

    /// <summary>Configures a confirmation: the danger button carries the verb and object.</summary>
    public static DialogWindow CreateConfirm(string title, string body, string dangerVerb) => new DialogWindow().ConfigureConfirm(title, body, dangerVerb);

    /// <summary>Configures an error report with a "Details" control for the technical text.</summary>
    public static DialogWindow CreateError(string title, string message, string? details) => new DialogWindow().ConfigureError(title, message, details);

    public DialogWindow ConfigureConfirm(string title, string body, string dangerVerb)
    {
        Title = title;
        HeadingText.Text = title;
        BodyText.Text = body;
        PrimaryButton.Content = dangerVerb;
        PrimaryButton.Style = (Style)FindResource("Button.Danger");
        CancelButton.Content = "Cancel";
        System.Windows.Automation.AutomationProperties.SetName(this, title);
        return this;
    }

    public DialogWindow ConfigureError(string title, string message, string? details)
    {
        Title = "Lifestream";
        HeadingText.Text = title;
        BodyText.Text = message;
        PrimaryButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "Close";
        CancelButton.Style = (Style)FindResource("Button.Primary");
        if (!string.IsNullOrEmpty(details))
        {
            DetailsPanel.Visibility = Visibility.Visible;
            DetailsBox.Text = details;
        }

        System.Windows.Automation.AutomationProperties.SetName(this, title);
        return this;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }
}
