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
        var error = Read(Box.Text, out var date);
        Report(error);
        if (error is not null)
        {
            return false;
        }

        SelectedDate = date;
        ShowDate();
        return true;
    }

    /// <summary>What the text means: a date (or none, for empty text), or the reason it is refused.</summary>
    private string? Read(string typed, out DateOnly? date)
    {
        date = null;
        var text = typed.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!Formatting.TryParseDate(text, out var parsed))
        {
            // Text in the right format that names a day that does not exist (31 Feb) is told apart from text in another format.
            return InDateFormat().IsMatch(text) ? $"There is no {text}." : $"Use the format {Formatting.Date(new DateOnly(2022, 3, 5))}.";
        }

        if (MinDate is { } min && parsed < min)
        {
            return $"The date can't be before {Formatting.Date(min)}.";
        }

        if (MaxDate is { } max && parsed > max)
        {
            return $"The date can't be after {Formatting.Date(max)}.";
        }

        date = parsed;
        return null;
    }

    /// <summary>
    /// While the person types, the date follows the text: text that is a date sets it at once, and text that is not is reported to the form
    /// without a message yet. So a save started with the caret still in the box (Ctrl+S, the default button, a screen reader's Invoke: none
    /// of them moves the focus) saves the date that is on screen, or is refused with the reason; it can never save the date from before.
    /// </summary>
    private void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var error = Read(Box.Text, out var date);
        if (error is not null)
        {
            Report(error, show: false);
            return;
        }

        Report(null);
        _syncing = true; // the text stays as typed: it is put into the app format on blur or Enter, not under the caret
        try
        {
            SelectedDate = date;
            ShowInCalendar();
        }
        finally
        {
            _syncing = false;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,2} [A-Za-z]{3} \d{4}$")]
    private static partial System.Text.RegularExpressions.Regex InDateFormat();

    /// <summary>
    /// Sets <see cref="ParseError"/> and tells the form the date is bound to (a <see cref="Lifestream.Client.Validation.FormViewModelBase"/>):
    /// while the text is refused the bound date is still the previous one, so the form must not save. The form blocks its save on it and shows
    /// the message; null takes it back.
    /// </summary>
    /// <param name="error">Why the text is refused, or null when it is a date (or empty).</param>
    /// <param name="show">False while the person is still typing: the form holds the reason back until the field is validated.</param>
    private void Report(string? error, bool show = true)
    {
        if (show || error is null)
        {
            ParseError = error;
        }

        if (GetBindingExpression(SelectedDateProperty) is { ResolvedSource: Lifestream.Client.Validation.FormViewModelBase form, ResolvedSourcePropertyName: { } field })
        {
            form.SetInputError(field, error, show);
        }
    }

    /// <summary>
    /// Limits the calendar itself, not just the typed text (SHELL-36). A date outside <see cref="MinDate"/> and <see cref="MaxDate"/> is blacked out and
    /// cannot be clicked. The date that is already chosen stays selectable even when it is outside the limits (a reschedule of an appointment whose
    /// day has passed keeps showing its own day), and the calendar never lets that one date widen the limits: the display range is coerced to include
    /// the chosen date, so the days around it are blacked out explicitly.
    /// </summary>
    private void ApplyLimits()
    {
        Picker.BlackoutDates.Clear();
        Picker.DisplayDateStart = MinDate is { } min ? min.ToDateTime(TimeOnly.MinValue) : null;
        Picker.DisplayDateEnd = MaxDate is { } max ? max.ToDateTime(TimeOnly.MinValue) : null;
        var keep = Picker.SelectedDate?.Date;
        if (MinDate is { } earliest)
        {
            Block(DateTime.MinValue.Date, earliest.ToDateTime(TimeOnly.MinValue).AddDays(-1), keep);
        }

        if (MaxDate is { } latest)
        {
            Block(latest.ToDateTime(TimeOnly.MinValue).AddDays(1), DateTime.MaxValue.Date, keep);
        }
    }

    private void Block(DateTime from, DateTime to, DateTime? keep)
    {
        if (to < from)
        {
            return;
        }

        if (keep is { } chosen && chosen >= from && chosen <= to)
        {
            if (chosen > from)
            {
                Picker.BlackoutDates.Add(new CalendarDateRange(from, chosen.AddDays(-1)));
            }

            if (chosen < to)
            {
                Picker.BlackoutDates.Add(new CalendarDateRange(chosen.AddDays(1), to));
            }

            return;
        }

        Picker.BlackoutDates.Add(new CalendarDateRange(from, to));
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
            ShowInCalendar();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowInCalendar()
    {
        Picker.BlackoutDates.Clear(); // a chosen date may not be inside a blackout range, so the ranges are rebuilt around it
        Picker.SelectedDate = SelectedDate?.ToDateTime(TimeOnly.MinValue);
        ApplyLimits();
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

        var date = DateOnly.FromDateTime(chosen);
        if ((MinDate is { } min && date < min) || (MaxDate is { } max && date > max))
        {
            ShowDate(); // outside the limits (the calendar already blacks these out): the previous date stays
            return;
        }

        Report(null);
        SelectedDate = date;
        ShowDate();
        CalendarPopup.IsOpen = false;
    }
}
