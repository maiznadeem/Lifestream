using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lifestream.Client.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Auth;

/// <summary>
/// The sign-in screen (direction A). All behaviour is in LoginViewModel; this view binds to it, asks once whether the server is ready (the footer
/// pill), warns when Caps Lock is on in the password field, and folds the compatibility side away when the window is narrow.
/// </summary>
public partial class LoginView : UserControl
{
    /// <summary>Below this width the compatibility side is hidden and the form takes the whole window.</summary>
    internal const double WideLayoutMinWidth = 900;

    private LoginViewModel? _model;

    public LoginView()
    {
        InitializeComponent();
        VersionText.Text = VersionLabel(Assembly.GetEntryAssembly() ?? typeof(LoginView).Assembly);
        DataContextChanged += (_, e) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
            }

            _model = e.NewValue as LoginViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
            }

            UpdateServerPill();
        };
    }

    /// <summary>"Lifestream 2.1.0": the three-part version of the running app.</summary>
    internal static string VersionLabel(Assembly assembly) => assembly.GetName().Version is { } v ? $"Lifestream {v.Major}.{v.Minor}.{v.Build}" : "Lifestream";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_model is { Server: ServerReadiness.Checking })
        {
            _ = _model.CheckServerAsync();
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.Server))
        {
            UpdateServerPill();
        }
    }

    private void UpdateServerPill() => ServerPill.Kind = _model?.Server switch
    {
        ServerReadiness.Ready => BadgeKind.Success,
        ServerReadiness.Waking => BadgeKind.Info,
        ServerReadiness.Unreachable => BadgeKind.Critical,
        _ => BadgeKind.Neutral,
    };

    private void OnPasswordKey(object sender, KeyEventArgs e) => UpdateCapsLock();

    private void OnPasswordFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCapsLock();

    private void UpdateCapsLock() =>
        CapsLockWarning.Visibility = PasswordBox.IsKeyboardFocusWithin && Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= WideLayoutMinWidth;
        Hero.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        HeroColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FormColumn.Width = wide ? new GridLength(560) : new GridLength(1, GridUnitType.Star);
        FormSide.BorderThickness = wide ? ThemeTokens.Inset("Inset.R1") : new Thickness(0);
        // The explanation keeps generous margins on a large screen and tightens them in a smaller window (it scales down inside a Viewbox).
        Hero.Margin = e.NewSize.Width >= 1280 ? ThemeTokens.Inset("Inset.H96V72") : ThemeTokens.Inset("Inset.L48T56R40B56");
    }
}
