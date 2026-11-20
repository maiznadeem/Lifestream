using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Shell;

/// <summary>
/// The main window (minimum 1024 x 640 DIP). The window chrome is the shared <see cref="ChromeWindow"/>; the shell draws the title bar. The taskbar
/// button says what matters while the window is minimized: a red overlay badge with the number of blood groups below their minimum, indeterminate
/// progress while the server wakes, and thumbnail buttons for Record donation and Book appointment. The window title names the screen
/// ("Lifestream, Donors"), so the thumbnail and Alt+Tab are useful.
/// </summary>
public partial class MainWindow : ChromeWindow
{
    private bool _closeConfirmed;
    private ShellViewModel? _model;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ShellViewModel);
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    /// <summary>Closes without asking: the app itself is closing this window (sign-out has already asked).</summary>
    public void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    /// <summary>The taskbar overlay for a count: a red disc with the number in white (null for zero). Public for tests.</summary>
    public static ImageSource? CreateBadge(int count, double pixelsPerDip = 1)
    {
        if (count <= 0)
        {
            return null;
        }

        var red = ThemeService.ColorOf("Color.CloseHover");
        var white = ThemeService.ColorOf("Color.OnCloseHover");
        var text = new FormattedText(count > 9 ? "9+" : count.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)ThemeLoader.Resources["Font.Family"], FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), count > 9 ? 9 : 11, new SolidColorBrush(white), pixelsPerDip);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawEllipse(new SolidColorBrush(red), null, new Point(8, 8), 8, 8);
            dc.DrawText(text, new Point(8 - (text.Width / 2), 8 - (text.Height / 2)));
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>A white outline icon for a thumbnail button.</summary>
    private static DrawingImage ThumbIcon(IconKind kind)
    {
        var pen = new Pen(Brushes.White, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var image = new DrawingImage(new GeometryDrawing(null, pen, Lifestream.Desktop.Controls.Icon.GeometryOf(kind)));
        image.Freeze();
        return image;
    }

    private void Attach(ShellViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        Taskbar.ThumbButtonInfos.Clear();
        if (model is null)
        {
            return;
        }

        model.PropertyChanged += OnModelChanged;
        Taskbar.ThumbButtonInfos.Add(new ThumbButtonInfo { Description = "Record donation", Command = model.RecordDonationCommand, ImageSource = ThumbIcon(IconKind.Drop), DismissWhenClicked = false });
        Taskbar.ThumbButtonInfos.Add(new ThumbButtonInfo { Description = "Book appointment", Command = model.BookAppointmentCommand, ImageSource = ThumbIcon(IconKind.Calplus), DismissWhenClicked = false });
        UpdateTaskbar();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.LowStockGroups) or nameof(ShellViewModel.IsServerWaking))
        {
            UpdateTaskbar();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTaskbar();

    private void UpdateTaskbar()
    {
        if (_model is null)
        {
            return;
        }

        var low = _model.LowStockGroups;
        Taskbar.Overlay = CreateBadge(low, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Taskbar.Description = low > 0 ? $"{low} blood groups below their minimum" : string.Empty;
        Taskbar.ProgressState = _model.IsServerWaking ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.None;
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
