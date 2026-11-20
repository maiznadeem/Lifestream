using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lifestream.Client.Common;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests;

[Collection("Wpf")]
public sealed class InputTests
{
    private static Grid Stage(UIElement child, double width = 500, double height = 200)
    {
        var grid = new Grid { Width = width, Height = height };
        grid.SetResourceReference(Panel.BackgroundProperty, "Brush.SurfaceCanvas");
        child.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        grid.Children.Add(child);
        return grid;
    }

    // Dates (SHELL-36)

    [Fact]
    public void DateField_ShowsAndParsesTheOneAppFormat()
    {
        var (shown, parsed, errorOnAmbiguous, kept, cleared) = StaHost.Run(() =>
        {
            var field = new DateField { SelectedDate = new DateOnly(2022, 3, 5) };
            using var host = Hosted.Create(Stage(field), 500, 200);
            host.Settle(100);
            host.Capture("DateField.png");
            var shownText = field.TextBox.Text;

            field.TextBox.Text = "17 Nov 2026";
            var ok = field.CommitText();
            var date = field.SelectedDate;

            field.TextBox.Text = "03/05/2022"; // ambiguous: day first or month first
            var accepted = field.CommitText();
            var error = field.ParseError;
            var keptDate = field.SelectedDate;

            field.TextBox.Text = string.Empty;
            field.CommitText();
            return (shownText, (ok, date), (accepted, error), keptDate, field.SelectedDate);
        });

        Assert.Equal("05 Mar 2022", shown);
        Assert.Equal((true, new DateOnly(2026, 11, 17)), parsed);
        Assert.False(errorOnAmbiguous.accepted);
        Assert.Equal("Use the format 05 Mar 2022.", errorOnAmbiguous.error);
        Assert.Equal(new DateOnly(2026, 11, 17), kept); // a refused text never replaces the date
        Assert.Null(cleared);
    }

    [Fact]
    public void DateField_EnforcesMinAndMax_AndBindsTwoWay()
    {
        var (futureError, bound, inRange) = StaHost.Run(() =>
        {
            var model = new DateModel();
            var field = new DateField { MaxDate = new DateOnly(2026, 11, 19), DataContext = model };
            field.SetBinding(DateField.SelectedDateProperty, new Binding(nameof(DateModel.Dob)) { Mode = BindingMode.TwoWay });
            using var host = Hosted.Create(Stage(field), 500, 200);
            host.Settle(100);

            field.TextBox.Text = "20 Nov 2026"; // tomorrow
            field.CommitText();
            var error = field.ParseError;

            field.TextBox.Text = "02 Jan 1990";
            var ok = field.CommitText();
            return (error, model.Dob, ok);
        });

        Assert.Equal("The date can't be after 19 Nov 2026.", futureError);
        Assert.Equal(new DateOnly(1990, 1, 2), bound);
        Assert.True(inRange);
    }

    private sealed class DateModel : System.ComponentModel.INotifyPropertyChanged
    {
        private DateOnly? _dob;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public DateOnly? Dob
        {
            get => _dob;
            set
            {
                _dob = value;
                PropertyChanged?.Invoke(this, new(nameof(Dob)));
            }
        }
    }

    [Fact]
    public void DateField_HasAnAccessibleCalendarButton_ReachableByKeyboard()
    {
        var (name, focusable, hint) = StaHost.Run(() =>
        {
            var field = new DateField();
            using var host = Hosted.Create(Stage(field), 500, 200);
            host.Settle(100);
            var open = host.Find<Button>("OpenButton");
            return (AutomationProperties.GetName(open), open.Focusable && open.IsTabStop, AutomationProperties.GetHelpText(field.TextBox));
        });

        Assert.Equal("Open calendar", name);
        Assert.True(focusable);
        Assert.Contains("05 Mar 2022", hint);
    }

    [Fact]
    public void NoView_UsesTheBuiltInDatePicker_SoEveryDatePickerUsesTheSameFormat()
    {
        foreach (var file in Directory.EnumerateFiles(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop"), "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            // The element itself, not "<DatePickerTextBox", which the theme's DatePicker template (Controls/Calendar.xaml) uses for the built-in picker's own text part.
            Assert.DoesNotMatch(@"<DatePicker[\s/>]", File.ReadAllText(file));
        }
    }

    // Combo boxes (SHELL-33)

    [Fact]
    public void ComboBox_IsKeyboardFocusable_BoundToTypedItems_AndOffersAnExplicitAll()
    {
        var (focusable, tab, items, selectedIsAll, afterPick, accessibleName) = StaHost.Run(() =>
        {
            var options = FilterOptions.WithAll(BloodTypeExtensions.All, b => b.ToDisplay());
            var model = new FilterModel { Selected = options[0] };
            var combo = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(FilterOption<BloodType>.Display), DataContext = model };
            AutomationProperties.SetName(combo, "Blood type");
            combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(FilterModel.Selected)) { Mode = BindingMode.TwoWay });
            var field = new FormField { Label = "Blood type", Content = combo };
            using var host = Hosted.Create(Stage(field), 500, 200);
            host.Settle(150);
            host.Capture("ComboBox_WithAll.png");
            var itemTexts = combo.Items.OfType<FilterOption<BloodType>>().Select(o => o.Display).ToList();
            var all = ((FilterOption<BloodType>)combo.SelectedItem!).IsAll;
            combo.SelectedIndex = 8;
            return (combo.Focusable, combo.IsTabStop, itemTexts, all, model.Selected!.Value, AutomationProperties.GetName(combo));
        });

        Assert.True(focusable);
        Assert.True(tab);
        Assert.Equal("All", items[0]);
        Assert.Equal(9, items.Count);
        Assert.True(selectedIsAll); // the filter starts at "All", an explicit choice
        Assert.Equal(BloodType.ONeg, afterPick); // the bound value is typed, not a string
        Assert.Equal("Blood type", accessibleName);
    }

    private sealed class FilterModel : System.ComponentModel.INotifyPropertyChanged
    {
        private FilterOption<BloodType>? _selected;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public FilterOption<BloodType>? Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new(nameof(Selected)));
            }
        }
    }

    // Charts (SHELL-35)

    [Fact]
    public void ChartFrame_ShowsAnEquivalentTable_BehindAToggle_WithTheSameNumbers()
    {
        var (name, toggleName, rows, columns, shown, hidden, afterToggleName, cells) = StaHost.Run(() =>
        {
            var table = new ChartTable(["Month", "Donated", "Issued"], [["Jan 2026", "12", "9"], ["Feb 2026", "0", "4"], ["Mar 2026", "7", "7"]]);
            var frame = new ChartFrame { Title = "Donations by month", Table = table, Content = new Border { Height = 160, Background = Brushes.Transparent, Child = new TextBlock { Text = "chart" } }, Height = 360 };
            using var host = Hosted.Create(Stage(frame, 700, 400), 700, 400);
            host.Settle(200);
            host.Capture("ChartFrame_Chart.png");
            var toggle = host.Find<System.Windows.Controls.Primitives.ToggleButton>("TableToggle");
            var tableGrid = frame.TableGrid!;
            var initiallyHidden = !tableGrid.IsVisible;
            var nameBefore = AutomationProperties.GetName(toggle);

            toggle.IsChecked = true;
            host.Settle(250);
            host.Capture("ChartFrame_Table.png");
            var cellTexts = host.All<TextBlock>().Where(t => tableGrid.IsAncestorOf(t) && t.IsVisible).Select(t => t.Text).ToList();
            return (AutomationProperties.GetName(frame), nameBefore, tableGrid.Items.Count, tableGrid.Columns.Count, tableGrid.IsVisible, initiallyHidden, AutomationProperties.GetName(toggle), cellTexts);
        });

        Assert.Equal("Donations by month chart", name);
        Assert.Equal("View as table", toggleName);
        Assert.Equal(3, rows);
        Assert.Equal(3, columns);
        Assert.True(shown);
        Assert.True(hidden);
        Assert.Equal("View as chart", afterToggleName);
        Assert.Contains("Feb 2026", cells);
        Assert.Contains("12", cells);
    }

}
