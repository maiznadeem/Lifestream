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
/// pill), warns when Caps Lock is on in the password field, and lays the two sides out for the window: the compatibility side is centred in its room and
/// is never scaled (text stays at its own size); it sheds its caption, then its overline, and folds away when the matrix itself does not fit.
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

    /// <summary>The width of the form side beside the matrix: 560, or 440 in a compact window (below 1280) so the matrix keeps its size.</summary>
    private const double FormWidth = 560;
    private const double CompactFormWidth = 440;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize);

    /// <summary>Lays out the form and the compatibility side for a view of this size.</summary>
    private void ApplyLayout(Size size)
    {
        var compact = size.Width < Responsive.StandardWidth;
        var formWidth = compact ? CompactFormWidth : FormWidth;
        var room = new Size(Math.Max(0, size.Width - formWidth - Hero.Margin.Left - Hero.Margin.Right), Math.Max(0, size.Height - Hero.Margin.Top - Hero.Margin.Bottom));

        // The whole explanation when there is room; without its caption, then without its overline, in a shorter window; not at all when the matrix
        // would not fit. Each piece is measured at its real size: nothing is scaled down.
        var wide = size.Width >= WideLayoutMinWidth;
        if (wide)
        {
            HeroCaption.Visibility = Visibility.Visible;
            HeroOverline.Visibility = Visibility.Visible;
            wide = HeroFits(room);
            if (!wide)
            {
                HeroCaption.Visibility = Visibility.Collapsed;
                wide = HeroFits(room);
            }

            if (!wide)
            {
                HeroOverline.Visibility = Visibility.Collapsed;
                wide = HeroFits(room);
            }
        }

        Hero.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        HeroColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FormColumn.Width = wide ? new GridLength(formWidth) : new GridLength(1, GridUnitType.Star);
        FormSide.BorderThickness = wide ? ThemeTokens.Inset("Inset.R1") : new Thickness(0);
        Form.Margin = ThemeTokens.Inset(wide && compact ? "Inset.L40T40R40B32" : "Inset.L72T48R72B40");
    }

    /// <summary>True when the explanation, as it is now, fits the room at its natural size.</summary>
    private bool HeroFits(Size room)
    {
        HeroBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return HeroBlock.DesiredSize.Width <= room.Width && HeroBlock.DesiredSize.Height <= room.Height;
    }
}
