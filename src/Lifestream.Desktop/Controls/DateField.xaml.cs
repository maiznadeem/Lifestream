using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The date input (SHELL-36): text in the one app format (<see cref="Formatting.DateFormat"/>, for example "05 Mar 2022") plus a calendar.
/// The built-in DatePicker cannot be given that format, so this control is used everywhere a date is entered. The date can be limited
/// with <see cref="MinDate"/> and <see cref="MaxDate"/> (a date of birth must be in the past). A text that does not parse is not
/// guessed: <see cref="ParseError"/> explains the format and the previous date stays.
/// </summary>
public partial class DateField : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateOnly?), typeof(DateField), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    public static readonly DependencyProperty MinDateProperty =
        DependencyProperty.Register(nameof(MinDate), typeof(DateOnly?), typeof(DateField), new FrameworkPropertyMetadata(null, (d, _) => ((DateField)d).ApplyLimits()));

    public static readonly DependencyProperty MaxDateProperty =
        DependencyProperty.Register(nameof(MaxDate), typeof(DateOnly?), typeof(DateField), new FrameworkPropertyMetadata(null, (d, _) => ((DateField)d).ApplyLimits()));

    public static readonly DependencyProperty ParseErrorProperty =
        DependencyProperty.Register(nameof(ParseError), typeof(string), typeof(DateField), new FrameworkPropertyMetadata(null));

    private bool _syncing;

    public DateField()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowDate();
    }

    public DateOnly? SelectedDate { get => (DateOnly?)GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }

    public DateOnly? MinDate { get => (DateOnly?)GetValue(MinDateProperty); set => SetValue(MinDateProperty, value); }

    public DateOnly? MaxDate { get => (DateOnly?)GetValue(MaxDateProperty); set => SetValue(MaxDateProperty, value); }

    /// <summary>Explains why the last typed text was refused (null when it was accepted); a FormField can show it under the input.</summary>
    public string? ParseError { get => (string?)GetValue(ParseErrorProperty); private set => SetValue(ParseErrorProperty, value); }

    /// <summary>The text box (for tests).</summary>
    public TextBox TextBox => Box;

    /// <summary>Applies what is typed, as on blur or Enter: valid text sets the date, anything else sets <see cref="ParseError"/>.</summary>
    public bool CommitText()
    {
        var text = Box.Text.Trim();
        if (text.Length == 0)
        {
            ParseError = null;
            SelectedDate = null;
            return true;
        }

        if (!Formatting.TryParseDate(text, out var date))
        {
            ParseError = $"Use the format {Formatting.Date(new DateOnly(2022, 3, 5))}.";
            return false;
        }

        if (MinDate is { } min && date < min)
        {
            ParseError = $"The date can't be before {Formatting.Date(min)}.";
            return false;
        }

        if (MaxDate is { } max && date > max)
        {
            ParseError = $"The date can't be after {Formatting.Date(max)}.";
            return false;
        }

        ParseError = null;
        SelectedDate = date;
        ShowDate();
        return true;
    }

    private void ApplyLimits()
    {
        Picker.DisplayDateStart = MinDate is { } min ? min.ToDateTime(TimeOnly.MinValue) : null;
        Picker.DisplayDateEnd = MaxDate is { } max ? max.ToDateTime(TimeOnly.MinValue) : null;
    }

    private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (DateField)d;
        if (!field._syncing)
        {
            field.ShowDate();
        }
    }

    private void ShowDate()
    {
        _syncing = true;
        try
        {
            Box.Text = Formatting.Date(SelectedDate);
            Picker.SelectedDate = SelectedDate?.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnBoxLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => CalendarPopup.IsOpen = true;

    private void OnCalendarChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Picker.SelectedDate is not { } chosen)
        {
            return;
        }

        ParseError = null;
        SelectedDate = DateOnly.FromDateTime(chosen);
        ShowDate();
        CalendarPopup.IsOpen = false;
    }
}
