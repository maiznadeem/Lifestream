using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Lookups;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Theme;
using Lifestream.Domain;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>Direction A's foundation: tokens in both themes, runtime theme switching, every control style, no effect on text, the title bar, the palette and the sidebar rail.</summary>
public sealed class FoundationTests
{
    private static ResourceDictionary Load(string file) => new() { Source = new Uri($"/Lifestream;component/Theme/{file}", UriKind.Relative) };

    [Fact]
    public void ColourTokens_LightAndDark_DefineTheSameKeys_AndEveryBrushResolvesInBothThemes()
    {
        var (light, dark, resolved) = StaHost.Run(() =>
        {
            _ = Application.Current;
            var l = Load("Colors.Light.xaml").Keys.OfType<string>().Order().ToList();
            var d = Load("Colors.Dark.xaml").Keys.OfType<string>().Order().ToList();
            var problems = new List<string>();
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeLoader.Reset();
                ThemeService.Apply(mode);
                foreach (var key in l)
                {
                    var value = ThemeLoader.Resources[key];
                    if (value is null || (key.StartsWith("Brush.", StringComparison.Ordinal) && value is not Brush) || (key.StartsWith("Color.", StringComparison.Ordinal) && value is not Color))
                    {
                        problems.Add($"{mode}: {key}");
                    }
                }
            }

            ThemeLoader.Reset();
            return (l, d, problems);
        });

        Assert.Equal(light, dark);
        Assert.True(light.Count > 100, $"only {light.Count} colour keys");
        Assert.Empty(resolved);
    }

    [Fact]
    public void DirectionA_Colours_AreExactlyTheDesignTokens()
    {
        var colours = StaHost.Run(() =>
        {
            _ = Application.Current;
            string Hex(ResourceDictionary d, string key) => ((Color)d["Color." + key]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var l = Load("Colors.Light.xaml");
            var d = Load("Colors.Dark.xaml");
            return new[] { "Canvas", "Layer", "Layer2", "Stroke", "Stroke2", "Ink", "Ink2", "Ink3", "Primary", "Blood", "BloodSoft", "Success", "Warning", "Info", "Hover", "Selected", "CloseHover" }
                .ToDictionary(k => k, k => (Hex(l, k), Hex(d, k)));
        });

        Assert.Equal(("#FFF1F3F6", "#FF111418"), colours["Canvas"]);
        Assert.Equal(("#FFFFFFFF", "#FF191D23"), colours["Layer"]);
        Assert.Equal(("#FFF7F8FA", "#FF1F242B"), colours["Layer2"]);
        Assert.Equal(("#FFE1E5EB", "#FF2A3038"), colours["Stroke"]);
        Assert.Equal(("#FFC8CFD9", "#FF3B434E"), colours["Stroke2"]);
        Assert.Equal(("#FF121820", "#FFEDF0F4"), colours["Ink"]);
        Assert.Equal(("#FF47515F", "#FFAEB7C3"), colours["Ink2"]);
        Assert.Equal(("#FF677382", "#FF8A95A3"), colours["Ink3"]);
        Assert.Equal(("#FF1B2533", "#FFE8ECF1"), colours["Primary"]);
        Assert.Equal(("#FFB4162E", "#FFF2596E"), colours["Blood"]);
        Assert.Equal(("#FFFBE8EB", "#FF3A1A21"), colours["BloodSoft"]);
        Assert.Equal(("#FF0F6E45", "#FF52C58E"), colours["Success"]);
        Assert.Equal(("#FF8F5200", "#FFEBAA4C"), colours["Warning"]);
        Assert.Equal(("#FF1D5BB8", "#FF82B2F5"), colours["Info"]);
        Assert.Equal(("#FFF2F4F7", "#FF1E232A"), colours["Hover"]);
        Assert.Equal(("#FFEAF0F7", "#FF232B35"), colours["Selected"]);
        Assert.Equal(("#FFC42B1C", "#FFC42B1C"), colours["CloseHover"]);
    }

    [Fact]
    public void Text_IsAtLeast4p5To1_InBothThemes_OnTheLayers()
    {
        var failures = StaHost.Run(() =>
        {
            _ = Application.Current;
            var problems = new List<string>();
            foreach (var file in new[] { "Colors.Light.xaml", "Colors.Dark.xaml" })
            {
                var d = Load(file);
                foreach (var ink in new[] { "Ink", "Ink2", "Ink3" })
                {
                    foreach (var surface in new[] { "Layer", "Layer2" })
                    {
                        var ratio = Contrast((Color)d["Color." + ink], (Color)d["Color." + surface]);
                        if (ratio < 4.5)
                        {
                            problems.Add($"{file} {ink} on {surface}: {ratio:F2}");
                        }
                    }
                }
            }

            return problems;
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void Theme_SwitchesAtRuntime_AndAnOpenViewRepaintsWithTheNewColours()
    {
        var (light, dark, back) = StaHost.Run(() =>
        {
            ThemeLoader.Reset();
            ThemeService.Apply(ThemeMode.Light);
            var border = new Border { Width = 40, Height = 40 };
            border.SetResourceReference(Border.BackgroundProperty, "Brush.Canvas");
            using var host = Hosted.Create(border, 40, 40);
            host.Settle(50);
            var a = ((SolidColorBrush)border.Background).Color;
            ThemeService.Apply(ThemeMode.Dark);
            host.Settle(50);
            var b = ((SolidColorBrush)border.Background).Color;
            ThemeService.Apply(ThemeMode.Light);
            host.Settle(50);
            var c = ((SolidColorBrush)border.Background).Color;
            ThemeLoader.Reset();
            return (a, b, c);
        });

        Assert.Equal(Color.FromRgb(0xF1, 0xF3, 0xF6), light);
        Assert.Equal(Color.FromRgb(0x11, 0x14, 0x18), dark);
        Assert.Equal(light, back);
    }

    [Fact]
    public void EveryKeyedControlStyle_AppliesAndBuildsItsTemplate_InBothThemes()
    {
        var problems = StaHost.Run(() =>
        {
            var failures = new List<string>();
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeLoader.Reset();
                ThemeService.Apply(mode);
                var theme = ThemeLoader.Resources;
                var styles = AllStyles(theme).Where(s => s.Style.TargetType is { IsAbstract: false } t && typeof(FrameworkElement).IsAssignableFrom(t) && !typeof(Window).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) is not null && !NeedsAParent.Contains(t)).ToList();
                foreach (var (key, style) in styles)
                {
                    try
                    {
                        var element = (FrameworkElement)Activator.CreateInstance(style.TargetType)!;
                        element.Style = style;
                        if (element is ContentControl content && content.Content is null && element is not ScrollViewer)
                        {
                            content.Content = "Sample";
                        }

                        var stage = new Grid();
                        stage.Children.Add(element);
                        using var host = Hosted.Create(stage, 200, 80);
                        host.Settle(10);
                        if (element is Control control && control.Template is not null && VisualTreeHelper.GetChildrenCount(control) == 0 && element.IsVisible)
                        {
                            failures.Add($"{mode} {key}: the template built no visual tree");
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        failures.Add($"{mode} {key}: {ex.GetType().Name} {ex.Message}");
                    }
                }

                Assert.True(styles.Count > 40, $"only {styles.Count} styles were applied");
            }

            ThemeLoader.Reset();
            return failures;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoElementThatContainsText_CarriesAnEffect_OnSignInDashboardOrComponents()
    {
        var offenders = StaHost.Run(() =>
        {
            var found = new List<string>();
            foreach (var dark in new[] { false, true })
            {
                ThemeLoader.Reset();
                ThemeService.Apply(dark ? ThemeMode.Dark : ThemeMode.Light);
                using (var rig = new Rig(dispatcher: WpfTestUi.Dispatcher()))
                {
                    DesignData.Configure(rig.Api);
                    var login = rig.Get<LoginViewModel>();
                    using var host = Hosted.Create(new LoginView { DataContext = login }, 1440, 900);
                    host.Settle(200);
                    found.AddRange(EffectsOnText(host.Element).Select(e => "sign-in: " + e));
                }

                using (var rig = new Rig(dispatcher: WpfTestUi.Dispatcher()))
                {
                    DesignData.Configure(rig.Api);
                    rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
                    var shell = rig.Get<ShellViewModel>();
                    shell.Attach(null, new DesignClock());
                    using var host = Hosted.Create(new ShellView { DataContext = shell }, 1440, 900);
                    Hosted.Await(() => shell.StartAsync());
                    shell.Palette.Open();
                    host.Settle(600);
                    found.AddRange(EffectsOnText(host.Element).Select(e => "shell: " + e));
                }

                var gallery = new StackPanel();
                gallery.Children.Add(new ElevatedSurface { Level = ElevationLevel.Flyout, Content = new TextBlock { Text = "Flyout" } });
                gallery.Children.Add(new ElevatedSurface { Level = ElevationLevel.Dialog, Content = new TextBlock { Text = "Dialog" } });
                gallery.Children.Add(new DialogSurface { Title = "Cancel this appointment?", Content = new TextBlock { Text = "Body" } });
                gallery.Children.Add(new SidePanel { IsOpen = true, Overline = "Donor D-0175", Content = new TextBlock { Text = "Panel" } });
                using (var host = Hosted.Create(gallery, 800, 900))
                {
                    host.Settle(300);
                    found.AddRange(EffectsOnText(host.Element).Select(e => "components: " + e));
                }
            }

            ThemeLoader.Reset();
            return found;
        });

        Assert.True(offenders.Count == 0, "Elements with an Effect contain text: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TitleBar_CaptionButtonsAre46x44_ContentExtendsUnderIt_AndMaximizedUsesTheRealFrameInset()
    {
        var facts = StaHost.Run(() =>
        {
            var window = new ChromeWindow { ExtendsContentIntoTitleBar = true, Content = new Grid() };
            window.ApplyTemplate();
            window.Measure(new Size(1200, 800));
            window.Arrange(new Rect(0, 0, 1200, 800));
            window.UpdateLayout();
            var minimize = (FrameworkElement)window.Template.FindName("PART_Minimize", window);
            var close = (FrameworkElement)window.Template.FindName("PART_Close", window);
            var host = (FrameworkElement)window.Template.FindName("ContentHost", window);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            var (min, closeSize, caption) = ((minimize.Width, minimize.Height), (close.Width, close.Height), chrome.CaptionHeight);
            window.TitleBarHeight = 40; // what the shell sets in a Compact window
            window.UpdateLayout();
            return (Min: min, Close: closeSize, Caption: caption, Row: Grid.GetRow(host), Span: Grid.GetRowSpan(host),
                MinAfter: (minimize.Width, minimize.Height), CloseAfter: (close.Width, close.Height), CaptionAfter: chrome.CaptionHeight, Inset: ChromeWindow.MaximizedInset);
        });

        Assert.Equal((46.0, 44.0), facts.Min);
        Assert.Equal((46.0, 44.0), facts.Close);
        Assert.Equal(44, facts.Caption); // the drag area is the title bar
        // The caption buttons follow the title bar: in a Compact window (40 DIP title bar) they are 46 x 40.
        Assert.Equal((46.0, 40.0), facts.MinAfter);
        Assert.Equal((46.0, 40.0), facts.CloseAfter);
        Assert.Equal(0, facts.Row);
        Assert.Equal(2, facts.Span);
        Assert.Equal(40, facts.CaptionAfter); // a compact window's 40 DIP title bar is also the drag area
        Assert.Equal(SystemParameters.WindowResizeBorderThickness, facts.Inset);
    }

    [Fact]
    public void Sidebar_CollapsesToTheRail_WithCtrlB_IsRemembered_AndIsARailInACompactWindow()
    {
        var facts = StaHost.Run(() =>
        {
            ThemeLoader.Reset();
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            DesignData.Configure(rig.Api);
            rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
            var shell = rig.Get<ShellViewModel>();
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(view, 1440, 900);
            host.Settle(100);
            var wide = (view.IsRail, Nav: view.FindName("NavColumn") is ColumnDefinition c ? c.ActualWidth : -1);
            Hosted.Await(() => shell.ToggleNavCommand.ExecuteAsync());
            host.Settle(100);
            var collapsed = (view.IsRail, Nav: ((ColumnDefinition)view.FindName("NavColumn")).ActualWidth, Saved: rig.Get<UiPreferences>().IsNavCollapsed);
            Hosted.Await(() => shell.ToggleNavCommand.ExecuteAsync());
            host.Layout(1024, 700);
            host.Settle(100);
            var compact = (view.IsRail, view.Breakpoint, Search: ((FrameworkElement)view.FindName("SearchField")).IsVisible);
            return (wide, collapsed, compact);
        });

        Assert.False(facts.wide.IsRail);
        Assert.Equal(236, facts.wide.Nav);
        Assert.True(facts.collapsed.IsRail);
        Assert.Equal(52, facts.collapsed.Nav);
        Assert.True(facts.collapsed.Saved);
        Assert.True(facts.compact.IsRail);
        Assert.Equal(Breakpoint.Compact, facts.compact.Breakpoint);
        Assert.False(facts.compact.Search);
    }

    [Fact]
    public void Palette_OpensWithCtrlK_FiltersCommands_SearchesRecords_MovesWithArrows_RunsWithEnter_AndClosesWithEscape()
    {
        var facts = StaHost.Run(() =>
        {
            ThemeLoader.Reset();
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            DesignData.Configure(rig.Api);
            rig.Api.On("SearchRecordsAsync", Lifestream.Client.Api.ApiResult<IReadOnlyList<RecordOption>>.Ok([new RecordOption(121, "Gideon Achterberg", "D-0121, O−")]));
            rig.Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), DesignData.User));
            var shell = rig.Get<ShellViewModel>();
            var view = new ShellView { DataContext = shell };
            using var host = Hosted.Create(view, 1440, 900);
            Hosted.Await(() => shell.StartAsync());
            host.Settle(100);

            var binding = view.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.K && b.Modifiers == ModifierKeys.Control);
            binding.Command.Execute(binding.CommandParameter);
            host.Settle(100);
            var palette = (CommandPaletteView)view.FindName("Palette");
            var opened = (palette.IsOpen, shell.Palette.Items.Count, First: shell.Palette.Items.FirstOrDefault()?.Title);

            Hosted.Await(() =>
            {
                shell.Palette.Query = "dark";
                return shell.Palette.PendingSearch;
            });
            var themeRows = shell.Palette.Items.Select(i => i.Title).ToList();

            Hosted.Await(() =>
            {
                shell.Palette.Query = "gideon";
                return shell.Palette.PendingSearch;
            });
            host.Settle(50);
            var records = shell.Palette.Items.Where(i => i.Group == "Donors").Select(i => i.Title).ToList();

            rig.Api.On("SearchRecordsAsync", Lifestream.Client.Api.ApiResult<IReadOnlyList<RecordOption>>.Ok([]));
            Hosted.Await(() =>
            {
                shell.Palette.Query = "go to";
                return shell.Palette.PendingSearch;
            });
            var before = shell.Palette.SelectedIndex;
            shell.Palette.Move(1);
            var after = shell.Palette.SelectedIndex;
            shell.Palette.Move(-2);
            var wrapped = shell.Palette.SelectedIndex == shell.Palette.Items.Count - 1;

            Hosted.Await(() =>
            {
                shell.Palette.Query = "stock";
                return shell.Palette.PendingSearch;
            });
            var goTo = shell.Palette.Items[shell.Palette.SelectedIndex];
            Hosted.Await(() => shell.Palette.RunSelectedAsync());
            host.Settle(200);
            var page = shell.PageTitle;

            shell.Palette.Open();
            host.Settle(50);
            shell.Palette.Close();
            host.Settle(50);
            return (opened, themeRows, records, before, after, wrapped, goTo.Title, page, Closed: !palette.IsOpen, Recent: shell.Palette.Items.Count > 0 ? shell.Palette.Items[0].Group : null);
        });

        Assert.True(facts.opened.IsOpen);
        Assert.True(facts.opened.Count > 10);
        Assert.Equal("Record donation", facts.opened.First);
        Assert.Contains("Theme: Dark", facts.themeRows);
        Assert.Equal(["Gideon Achterberg"], facts.records);
        Assert.Equal(0, facts.before);
        Assert.Equal(1, facts.after);
        Assert.True(facts.wrapped); // Up from the first row wraps to the last
        Assert.Equal("Stock", facts.Title);
        Assert.Equal("Stock", facts.page);
        Assert.True(facts.Closed);
        Assert.Equal("Recent", facts.Recent); // what was run last comes first next time
    }

    [Fact]
    public void Compatibility_FollowsAboAndRhD()
    {
        Assert.True(BloodType.ONeg.CanGiveTo(BloodType.ABPos));
        Assert.All(BloodTypeExtensions.All, t => Assert.True(BloodType.ONeg.CanGiveTo(t)));
        Assert.All(BloodTypeExtensions.All, t => Assert.True(t.CanGiveTo(BloodType.ABPos)));
        Assert.False(BloodType.APos.CanGiveTo(BloodType.ANeg));
        Assert.False(BloodType.BNeg.CanGiveTo(BloodType.ANeg));
        Assert.Equal(27, LoginViewModel.Compatibility.SelectMany(r => r.Cells).Count(c => c.CanGive));
        Assert.Equal("O−", BloodType.ONeg.ToTypographic());
        Assert.Equal("O-", BloodType.ONeg.ToDisplay());
    }

    /// <summary>Elements that only exist inside their owner (a popup, a grid, a calendar, a list) and are covered by the owner's own template.</summary>
    private static readonly HashSet<Type> NeedsAParent =
    [
        typeof(ToolTip), typeof(ContextMenu), typeof(MenuItem), typeof(DataGridRow), typeof(DataGridCell), typeof(System.Windows.Controls.Primitives.DataGridColumnHeader),
        typeof(System.Windows.Controls.Primitives.CalendarItem), typeof(System.Windows.Controls.Primitives.CalendarDayButton), typeof(System.Windows.Controls.Primitives.CalendarButton),
        typeof(ComboBoxItem), typeof(ListBoxItem), typeof(TabItem), typeof(Thumb), typeof(RepeatButton), typeof(ScrollBar), typeof(Separator),
    ];

    private static IEnumerable<(object Key, Style Style)> AllStyles(ResourceDictionary dictionary)
    {
        foreach (var key in dictionary.Keys)
        {
            if (dictionary[key] is Style style && key is string)
            {
                yield return (key, style);
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var item in AllStyles(merged))
            {
                yield return item;
            }
        }
    }

    /// <summary>Every element with an Effect whose visual subtree holds text (a TextBlock with text or a glyph run).</summary>
    private static IEnumerable<string> EffectsOnText(DependencyObject root)
    {
        foreach (var element in Hosted.Descendants(root).OfType<UIElement>().Where(e => e.Effect is not null))
        {
            var text = Hosted.Descendants(element).OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text));
            if (text is not null || Hosted.Descendants(element).OfType<TextBox>().Any())
            {
                yield return $"{element.GetType().Name} {(element as FrameworkElement)?.Name} ({element.Effect!.GetType().Name}) holds \"{text?.Text}\"";
            }
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c)
        {
            var s = c / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
