using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Lookups;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Lookups;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Services;

namespace Lifestream.Desktop.Tests;

[Collection("Wpf")]
public sealed class ComponentTests
{
    private static Grid Canvas(double width, double height, params UIElement[] children)
    {
        var grid = new Grid { Width = width, Height = height };
        grid.SetResourceReference(Panel.BackgroundProperty, "Brush.SurfaceCanvas");
        foreach (var child in children)
        {
            grid.Children.Add(child);
        }

        return grid;
    }

    // Skeleton

    [Fact]
    public void Skeleton_EachKind_HasTheExpectedShape()
    {
        var counts = StaHost.Run(() =>
        {
            var result = new Dictionary<SkeletonKind, (int Bars, double Height)>();
            foreach (var kind in Enum.GetValues<SkeletonKind>())
            {
                var skeleton = new Skeleton { Kind = kind, ColumnWidths = "2*,*,90" };
                using var host = Hosted.Create(Canvas(900, 700, skeleton), 900, 700);
                host.Settle();
                result[kind] = (skeleton.Bars.Count(), skeleton.ActualHeight);
                host.Capture($"Skeleton_{kind}.png");
            }

            return result;
        });

        Assert.Equal(1 + 10 * 3 + 1, counts[SkeletonKind.List].Bars); // toolbar search and action, plus 10 rows of 3 cells
        Assert.Equal(1 + 6 * 2, counts[SkeletonKind.Detail].Bars);      // avatar and six label/value pairs
        Assert.Equal(1 + 9 * 2, counts[SkeletonKind.Profile].Bars);     // avatar and nine label/value pairs
        Assert.Equal(4 + 1 + 15, counts[SkeletonKind.Dashboard].Bars);  // four KPI cards, a chart, a five-row table
        Assert.Equal(8, counts[SkeletonKind.StockTiles].Bars);
        Assert.Equal(6, counts[SkeletonKind.Picker].Bars);              // three option rows, two bars each
    }

    [Fact]
    public void Skeleton_ListKind_MatchesGridColumnWidths()
    {
        const string spec = "2*,*,120,90";
        var (barLefts, headerLefts, barRights, headerRights) = StaHost.Run(() =>
        {
            var skeleton = new Skeleton { Kind = SkeletonKind.List, ColumnWidths = spec, RowCount = 3 };
            var grid = new LifestreamDataGrid();
            var converter = new DataGridLengthConverter();
            foreach (var (title, width) in new[] { ("Name", "2*"), ("Blood type", "*"), ("Phone", "120"), ("Last donation", "90") })
            {
                grid.Columns.Add(new DataGridTextColumn { Header = title, Width = (DataGridLength)converter.ConvertFromString(width)!, Binding = new Binding(".") });
            }

            // Same container width, one stacked above the other, as on a real screen where one replaces the other.
            var stack = new StackPanel { Width = 800 };
            stack.Children.Add(skeleton);
            stack.Children.Add(grid);
            using var host = Hosted.Create(Canvas(800, 700, stack), 800, 700);
            host.Settle(300);

            // First data row of the skeleton: the three bars after the toolbar row.
            var firstRow = skeleton.Bars.Skip(2).Take(4).Select(b => Hosted.BoundsOf(b, skeleton)).ToList();
            var headers = host.All<System.Windows.Controls.Primitives.DataGridColumnHeader>().Where(h => h.Column is not null).OrderBy(h => h.Column.DisplayIndex).ToList();
            var headerBounds = headers.Select(h => Hosted.BoundsOf(h, grid)).ToList();
            return (firstRow.Select(r => r.Left).ToList(), headerBounds.Select(r => r.Left).ToList(), firstRow.Select(r => r.Right).ToList(), headerBounds.Select(r => r.Right).ToList());
        });

        Assert.Equal(4, barLefts.Count);
        Assert.Equal(4, headerLefts.Count);
        for (var i = 0; i < 4; i++)
        {
            // A bar starts where the cell text starts (cell left + 12) and ends 12 before the cell's right edge.
            Assert.Equal(headerLefts[i] + 12, barLefts[i], 1.5);
            Assert.Equal(headerRights[i] - 12, barRights[i], 1.5);
        }
    }

    [Fact]
    public void Skeleton_ReducedMotion_IsStatic()
    {
        var (animatedFlag, animatedDiff, staticFlag, staticDiff) = StaHost.Run(() =>
        {
            (bool, int) Measure(bool animations)
            {
                var skeleton = new Skeleton { Kind = SkeletonKind.Detail };
                using var host = Hosted.Create(Canvas(500, 500, skeleton), 500, 500, animations: animations);
                host.Settle(100);
                var first = host.Pixels();
                host.Settle(450);
                var second = host.Pixels();
                if (!animations)
                {
                    host.Capture("Skeleton_ReducedMotion_Static.png");
                }

                return (skeleton.IsAnimating, Hosted.Differences(first, second));
            }

            var on = Measure(true);
            var off = Measure(false);
            return (on.Item1, on.Item2, off.Item1, off.Item2);
        });

        Assert.True(animatedFlag);
        Assert.True(animatedDiff > 50, "the shimmer must move when animations are on");
        Assert.False(staticFlag);
        Assert.Equal(0, staticDiff); // pixel-identical over time under reduced motion
    }

    // LoadSurface (skeleton in place, empty, error)

    private sealed class SurfaceViewModel : ViewModelBase
    {
        public TaskCompletionSource Gate { get; } = new();

        protected override Task FetchAsync(CancellationToken ct) => Gate.Task;
    }

    private static (LoadSurface Surface, Grid Root, SurfaceViewModel ViewModel) SurfaceFor()
    {
        var viewModel = new SurfaceViewModel();
        var surface = new LoadSurface { SkeletonKind = SkeletonKind.List, SkeletonColumns = "2*,*,*", Content = new Border { Height = 400, Child = new TextBlock { Text = "real content" } } };
        surface.SetBinding(LoadSurface.StateProperty, new Binding(nameof(ViewModelBase.State)));
        surface.SetBinding(LoadSurface.ErrorMessageProperty, new Binding(nameof(ViewModelBase.ErrorMessage)));
        surface.SetBinding(LoadSurface.ErrorDetailProperty, new Binding(nameof(ViewModelBase.ErrorDetail)));
        var root = Canvas(900, 600, surface);
        root.DataContext = viewModel;
        return (surface, root, viewModel);
    }

    [Fact]
    public void Skeleton_AppearsWithin100msOfADataRequest_AndIsReplacedInPlaceWithNoLayoutShift()
    {
        var (visibleAfter, sizes, readyContentVisible, skeletonGone) = StaHost.Run(() =>
        {
            var (surface, root, viewModel) = SurfaceFor();
            viewModel.State = LoadState.Ready;
            using var host = Hosted.Create(root, 900, 600);
            host.Settle(200);
            var readyBounds = Hosted.BoundsOf(surface, root);

            var clock = Stopwatch.StartNew();
            var load = viewModel.LoadAsync(); // the data request
            Hosted.PumpUntil(() => surface.SkeletonPart is { IsVisible: true } skeleton && skeleton.Bars.Any(), 2000);
            var elapsed = clock.ElapsedMilliseconds;
            var loadingBounds = Hosted.BoundsOf(surface, root);
            host.Capture("Skeleton_ListKind_Loading.png");

            viewModel.Gate.SetResult();
            Hosted.PumpUntil(() => load.IsCompleted, 2000);
            host.Settle(100);
            var afterBounds = Hosted.BoundsOf(surface, root);
            var content = (FrameworkElement)surface.Content;
            return (elapsed, (readyBounds, loadingBounds, afterBounds), content.IsVisible, surface.SkeletonPart!.IsVisible);
        });

        Assert.True(visibleAfter < 100, $"skeleton took {visibleAfter} ms");
        Assert.Equal(sizes.readyBounds, sizes.loadingBounds);
        Assert.Equal(sizes.loadingBounds, sizes.afterBounds);
        Assert.True(readyContentVisible);
        Assert.False(skeletonGone);
    }

    [Fact]
    public void LoadSurface_ErrorState_ShowsPlainCause_RetryAndDetailsBehindAControl()
    {
        var (cause, retryVisible, detailsClosed, detailsOpen, rawVisibleInMain) = StaHost.Run(() =>
        {
            var (surface, root, viewModel) = SurfaceFor();
            var retried = 0;
            surface.RetryCommand = new AsyncRelayCommand(_ =>
            {
                retried++;
                return Task.CompletedTask;
            });
            viewModel.State = LoadState.Ready;
            using var host = Hosted.Create(root, 900, 600);
            host.Settle(100);

            viewModel.Gate.SetException(new InvalidOperationException("secret failure"));
            var load = viewModel.LoadAsync();
            Hosted.PumpUntil(() => load.IsCompleted, 2000);
            host.Settle(150);

            var error = host.All<ErrorState>().First();
            var retry = host.All<Button>().First(b => b.IsVisible && b.Content as string == "Retry");
            var toggle = host.All<System.Windows.Controls.Primitives.ToggleButton>().First(t => t.Content as string == "Details");
            var detailPanel = host.All<TextBox>().FirstOrDefault(t => AutomationProperties.GetName(t) == "Technical details");
            var closed = detailPanel is null || !detailPanel.IsVisible;
            host.Capture("ErrorState_Plain.png");

            toggle.IsChecked = true;
            host.Settle(100);
            var open = host.All<TextBox>().First(t => AutomationProperties.GetName(t) == "Technical details").IsVisible;
            var main = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text);
            return (error.Message, retry.IsVisible && retry.Command is not null, closed, open, main.Any(t => t.Contains("secret", StringComparison.Ordinal) && t != viewModel.ErrorDetail));
        });

        Assert.Equal("Something went wrong while loading this screen.", cause);
        Assert.True(retryVisible);
        Assert.True(detailsClosed);
        Assert.True(detailsOpen);
        Assert.False(rawVisibleInMain);
    }

    [Fact]
    public void LoadSurface_EmptyState_ShowsCauseAndAction_WakingShowsBannerWithCancelAndRetry()
    {
        var (emptyVisible, actionText, wakingText, wakingButtons) = StaHost.Run(() =>
        {
            var (surface, root, viewModel) = SurfaceFor();
            surface.EmptyTitle = "No donors match";
            surface.EmptyMessage = "Try a different search.";
            surface.EmptyActionText = "Clear filters";
            surface.EmptyActionCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            using var host = Hosted.Create(root, 900, 600);
            viewModel.State = LoadState.Empty;
            host.Settle(150);
            var empty = host.All<EmptyState>().First();
            var emptyShown = empty.IsVisible;
            host.Capture("EmptyState_NoMatches.png");
            var action = host.All<Button>().First(b => b.Content as string == "Clear filters");

            surface.CancelCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            surface.RetryCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            viewModel.State = LoadState.Waking;
            host.Settle(150);
            host.Capture("LoadSurface_Waking.png");
            var banner = (FrameworkElement)surface.Template.FindName("PART_WakingBanner", surface);
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var buttons = host.All<Button>().Where(b => b.IsVisible).Select(b => b.Content as string).ToList();
            return (emptyShown && banner is not null, (string)action.Content, texts.First(t => t.StartsWith("Waking up", StringComparison.Ordinal)), buttons);
        });

        Assert.True(emptyVisible);
        Assert.Equal("Clear filters", actionText);
        Assert.Equal("Waking up the server, this can take up to a minute", wakingText);
        Assert.Contains("Cancel", wakingButtons);
        Assert.Contains("Retry", wakingButtons);
    }

    // Status chip

    [Fact]
    public void StatusChip_EveryStatus_HasIconTextAndTokenColour()
    {
        var chips = StaHost.Run(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };
            var all = Enum.GetValues<StatusKind>().Select(kind => new StatusChip { Status = kind }).ToList();
            foreach (var chip in all)
            {
                panel.Children.Add(chip);
            }

            using var host = Hosted.Create(Canvas(260, 260, panel), 260, 260);
            host.Settle(150);
            host.Capture("StatusChips.png");
            return all.Select(c => (c.Status, c.Glyph, c.DisplayText, Brush: (c.AccentBrush as SolidColorBrush)?.Color, Name: AutomationProperties.GetName(c))).ToList();
        });

        Assert.Equal(6, chips.Count);
        Assert.Equal(["Pending", "Completed", "Cancelled", "Incomplete", "Low stock", "Expiring"], chips.Select(c => c.DisplayText));
        Assert.All(chips, c =>
        {
            Assert.False(string.IsNullOrEmpty(c.Glyph));
            Assert.NotNull(c.Brush); // a token colour
            Assert.Equal(c.DisplayText, c.Name);
        });
        Assert.Equal(6, chips.Select(c => c.Glyph).Distinct().Count()); // every status has its own icon...
        Assert.Equal(6, chips.Select(c => c.DisplayText).Distinct().Count()); // ...and its own text, so meaning never depends on colour alone
    }

    // Toast

    [Fact]
    public void Toast_Host_ShowsToasts_PausesOnHover_AndIsALiveRegion()
    {
        var (names, paused, resumed, live, messageLive) = StaHost.Run(() =>
        {
            var clock = new ManualTimeProvider();
            var service = new ToastService(WpfTestUi.Dispatcher(), clock);
            var host = new ToastHost { DataContext = service };
            host.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(IToastService.Toasts)));
            using var hosted = Hosted.Create(Canvas(700, 300, host), 700, 300);
            hosted.Settle(100);

            var toast = service.ShowForRecord(ToastKind.Success, "Donor", "Ayşe Öztürk", "saved", "View", () => Task.CompletedTask);
            hosted.Settle(200);
            hosted.Capture("Toast_Success.png");
            var view = hosted.All<ToastView>().Single();
            var texts = hosted.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();

            view.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            var isPaused = toast.IsPaused;
            view.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            var isResumed = !toast.IsPaused;
            var message = hosted.All<TextBlock>().First(t => t.Text.StartsWith("Donor", StringComparison.Ordinal));
            return (texts, isPaused, isResumed, AutomationProperties.GetLiveSetting(host), AutomationProperties.GetLiveSetting(message));
        });

        Assert.Contains("Donor 'Ayşe Öztürk' saved.", names);
        Assert.True(paused);
        Assert.True(resumed);
        Assert.Equal(AutomationLiveSetting.Polite, live);
        Assert.Equal(AutomationLiveSetting.Polite, messageLive);
    }

    // Confirm dialog

    [Fact]
    public void ConfirmDialog_NamesTheRecord_FocusOnCancel_EscCancels_DangerButtonHasVerbAndObject()
    {
        var (title, body, danger, cancelIsCancel, cancelDefaultFocus, dangerStyle) = StaHost.Run(() =>
        {
            var dialog = DialogWindow.CreateConfirm("Delete donor 'Ayşe Öztürk'?", "This removes Ayşe Öztürk and their appointment history. It can't be undone.", "Delete donor");
            var focused = System.Windows.Input.FocusManager.GetFocusedElement(dialog);
            return (dialog.Title, ((TextBlock)dialog.FindName("BodyText")).Text, dialog.Primary.Content, dialog.Cancel.IsCancel, ReferenceEquals(focused, dialog.Cancel), dialog.Primary.Style == dialog.FindResource("Button.Danger"));
        });

        Assert.Contains("Ayşe Öztürk", title);
        Assert.Contains("Ayşe Öztürk", body);
        Assert.Equal("Delete donor", danger);
        Assert.True(cancelIsCancel); // Esc closes it
        Assert.True(cancelDefaultFocus, "initial focus is on Cancel");
        Assert.True(dangerStyle);
    }

    [Fact]
    public void ErrorDialog_ShowsFriendlyTextWithDetailsBehindAControl()
    {
        var (heading, detailsControlShown, detailsTextHidden, primaryHidden) = StaHost.Run(() =>
        {
            var dialog = DialogWindow.CreateError("Something went wrong", "Lifestream hit an unexpected problem.", "InvalidOperationException: boom");
            var panel = (StackPanel)dialog.FindName("DetailsPanel");
            var box = (TextBox)dialog.FindName("DetailsBox");
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)dialog.FindName("DetailsToggle");
            return (((TextBlock)dialog.FindName("HeadingText")).Text, panel.Visibility == Visibility.Visible, toggle.IsChecked != true && box.Visibility == Visibility.Collapsed, dialog.Primary.Visibility == Visibility.Collapsed);
        });

        Assert.Equal("Something went wrong", heading);
        Assert.True(detailsControlShown);
        Assert.True(detailsTextHidden); // the technical text is behind the Details control
        Assert.True(primaryHidden);
    }

    // Password field

    [Fact]
    public void PasswordField_AlwaysDisplaysItsBoundState_InBothDirections()
    {
        var results = StaHost.Run(() =>
        {
            var field = new PasswordField();
            var model = new PasswordModel();
            field.DataContext = model;
            field.SetBinding(PasswordField.PasswordProperty, new Binding(nameof(PasswordModel.Password)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            using var host = Hosted.Create(Canvas(400, 100, field), 400, 100);
            host.Settle(100);
            var box = host.All<PasswordBox>().First();
            var plain = host.All<TextBox>().First(t => t.Name == "PART_Plain");

            var list = new List<(string, string?)> { ("start", field.DisplayedText) };
            model.Password = TestSecrets.FromViewModel;
            host.Settle(50);
            list.Add(("vm-to-box", box.Password));
            box.Password = TestSecrets.TypedByUser;
            host.Settle(50);
            list.Add(("box-to-vm", model.Password));
            model.Password = string.Empty; // a cleared view model clears the box
            host.Settle(50);
            list.Add(("cleared-box", box.Password));
            list.Add(("cleared-plain", plain.Text));

            field.IsRevealed = true;
            model.Password = TestSecrets.Shown;
            host.Settle(50);
            list.Add(("revealed", field.DisplayedText));
            plain.Text = "edited-while-shown";
            host.Settle(50);
            list.Add(("plain-to-vm", model.Password));
            list.Add(("plain-to-box", box.Password));
            return list;
        });

        Assert.Equal(
            [("start", ""), ("vm-to-box", TestSecrets.FromViewModel), ("box-to-vm", TestSecrets.TypedByUser), ("cleared-box", ""), ("cleared-plain", ""), ("revealed", "shown"), ("plain-to-vm", "edited-while-shown"), ("plain-to-box", "edited-while-shown")],
            results.Select(r => (r.Item1, r.Item2 ?? "<null>")));
    }

    private sealed class PasswordModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _password = string.Empty;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Password
        {
            get => _password;
            set
            {
                _password = value;
                PropertyChanged?.Invoke(this, new(nameof(Password)));
            }
        }
    }

    [Fact]
    public void PasswordField_ReadOnly_BlocksTyping_AndHasOneToggleWithAName()
    {
        var (toggleName, toggleCount, readOnlyBlocks, toggleEnabled) = StaHost.Run(() =>
        {
            var field = new PasswordField { IsReadOnly = true };
            using var host = Hosted.Create(Canvas(400, 100, field), 400, 100);
            host.Settle(100);
            var toggles = host.All<System.Windows.Controls.Primitives.ToggleButton>().ToList();
            var box = host.All<PasswordBox>().First();
            var args = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, box, "x")) { RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent };
            box.RaiseEvent(args);
            return (AutomationProperties.GetName(toggles[0]), toggles.Count, args.Handled, toggles[0].IsEnabled);
        });

        Assert.Equal("Show password", toggleName);
        Assert.Equal(1, toggleCount);
        Assert.True(readOnlyBlocks);
        Assert.False(toggleEnabled);
    }

    // Form field

    [Fact]
    public void FormField_PersistentLabel_AssociatedWithInput_ErrorWithIconAndText_Announced_FocusMoves()
    {
        var (labeledBy, name, errorVisible, errorText, live, focusMoved, hintVisible) = StaHost.Run(() =>
        {
            var input = new TextBox();
            var field = new FormField { Label = "Date of birth", Hint = "Format: 05 Mar 2022", Content = input };
            using var host = Hosted.Create(Canvas(400, 200, field), 400, 200);
            host.Settle(100);
            var label = (TextBlock)field.Template.FindName("PART_Label", field);

            field.Error = "Date of birth can't be in the future.";
            host.Settle(100);
            host.Capture("FormField_Error.png");
            var errorRow = (FrameworkElement)field.Template.FindName("ErrorRow", field);
            var errorBlock = (TextBlock)field.Template.FindName("PART_Error", field);
            var hint = (FrameworkElement)field.Template.FindName("HintText", field);
            var moved = ReferenceEquals(field.Input, input) && input is { Focusable: true, IsEnabled: true, IsVisible: true };
            return (AutomationProperties.GetLabeledBy(input) == label, AutomationProperties.GetName(input), errorRow.IsVisible, errorBlock.Text, AutomationProperties.GetLiveSetting(errorBlock), moved, hint.IsVisible);
        });

        Assert.True(labeledBy);
        Assert.Equal("Date of birth", name);
        Assert.True(errorVisible);
        Assert.Equal("Date of birth can't be in the future.", errorText);
        Assert.Equal(AutomationLiveSetting.Assertive, live);
        Assert.True(focusMoved, "the field's input is the focusable control inside it");
        Assert.False(hintVisible); // the error takes the hint's place
    }

    private sealed class DemoForm : Lifestream.Client.Validation.FormViewModelBase
    {
        public DemoForm() => DeclareFields("Name", "Dob", "Phone");

        public string Name { get; set; } = string.Empty;

        public string Dob { get; set; } = string.Empty;

        public string Phone { get; set; } = "5551234";

        public override bool HasUnsavedChanges => false;

        protected override string? ValidateField(string field) => field switch
        {
            "Name" when Name.Length == 0 => "Enter a name.",
            "Dob" when Dob.Length == 0 => "Enter a date of birth.",
            _ => null,
        };
    }

    [Fact]
    public void Form_OnSubmit_ShowsEveryError_AndTheFirstInvalidFieldTakesFocus()
    {
        var (messages, requested) = StaHost.Run(() =>
        {
            var form = new DemoForm { Name = "Ayşe" };
            var stack = new StackPanel { DataContext = form };
            FormFocus.SetIsEnabled(stack, true);
            var fields = new List<FormField>();
            foreach (var (label, name) in new[] { ("Name", "Name"), ("Date of birth", "Dob"), ("Phone", "Phone") })
            {
                var field = new FormField { Label = label, Content = new TextBox() };
                FormFocus.SetFieldName(field, name);
                field.SetBinding(FormField.ErrorProperty, new Binding($"[{name}]") { Source = new ErrorLookup(form) });
                fields.Add(field);
                stack.Children.Add(field);
            }

            using var host = Hosted.Create(Canvas(500, 400, stack), 500, 400);
            host.Settle(100);

            var ok = form.ValidateAll();
            host.Settle(100);
            host.Capture("Form_ValidationOnSubmit.png");
            return (fields.Select(f => f.Error).ToList(), fields.Select(f => f.FocusRequestCount).ToList());
        });

        Assert.Equal([null, "Enter a date of birth.", null], messages);
        Assert.Equal([0, 1, 0], requested); // the first invalid field in visual order, and only that one
    }

    /// <summary>Indexer binding source: the error text of a field of the form.</summary>
    private sealed class ErrorLookup : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly Lifestream.Client.Validation.FormViewModelBase _form;

        public ErrorLookup(Lifestream.Client.Validation.FormViewModelBase form)
        {
            _form = form;
            form.ErrorsChanged += (_, _) => PropertyChanged?.Invoke(this, new("Item[]"));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string? this[string field] => _form.GetError(field);
    }

    // Record picker

    private static void UseFakeApiForPickers(FakeApiClient api)
    {
        RecordPicker.InlineResults = true; // never open a popup window on the desktop
        RecordPicker.ViewModelFactory = kind => new RecordPickerViewModel(api, kind, debounce: TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void RecordPicker_SearchShowsSkeletonRows_ThenOptions_ThenSummaryChip_AndBindsTheId()
    {
        var api = new FakeApiClient();
        var gate = new TaskCompletionSource<ApiResult<IReadOnlyList<RecordOption>>>();
        api.On<IReadOnlyList<RecordOption>>("SearchRecordsAsync", _ => gate.Task);
        UseFakeApiForPickers(api);
        var (skeletonWhileSearching, optionTitles, chipText, selectedId, boundId, noSearchBoxAfter) = StaHost.Run(() =>
        {
            var model = new PickerModel();
            var picker = new RecordPicker { Kind = RecordKind.Donor, Label = "Donor", DataContext = model };
            picker.SetBinding(RecordPicker.SelectedIdProperty, new Binding(nameof(PickerModel.DonorId)) { Mode = BindingMode.TwoWay });
            using var host = Hosted.Create(Canvas(500, 400, picker), 500, 400);
            host.Settle(100);
            var vm = picker.ViewModel!;

            vm.SearchText = "ays";
            Hosted.PumpUntil(() => vm.IsSearching, 2000);
            host.Settle(100);
            var skeleton = host.All<Skeleton>().FirstOrDefault(s => s.Kind == SkeletonKind.Picker);
            var skeletonVisible = skeleton is { IsVisible: true } && skeleton.Bars.Count() == 6;

            gate.SetResult(ApiResult<IReadOnlyList<RecordOption>>.Ok([new RecordOption(1123, "Ayşe Öztürk", "A+ · 02 Jan 1990"), new RecordOption(7, "Ayşegül Can", "O-")]));
            Hosted.PumpUntil(() => !vm.IsSearching && vm.Options.Count == 2, 2000);
            host.Settle(150);
            host.Capture("RecordPicker_Results.png");
            var titles = vm.Options.Select(o => o.Title).ToList();

            vm.Select(vm.Options[0]);
            host.Settle(150);
            host.Capture("RecordPicker_Chip.png");
            var chip = host.All<TextBlock>().First(t => t.IsVisible && t.Text.StartsWith("Ayşe Öztürk", StringComparison.Ordinal)).Text;
            var searchBoxHidden = !host.All<TextBox>().Any(t => t.IsVisible);
            return (skeletonVisible, titles, chip, picker.SelectedId, model.DonorId, searchBoxHidden);
        });

        Assert.True(skeletonWhileSearching);
        Assert.Equal(["Ayşe Öztürk", "Ayşegül Can"], optionTitles);
        Assert.Equal("Ayşe Öztürk · #1123 · A+ · 02 Jan 1990", chipText);
        Assert.Equal(1123, selectedId);
        Assert.Equal(1123, boundId);
        Assert.True(noSearchBoxAfter);
    }

    private sealed class PickerModel : System.ComponentModel.INotifyPropertyChanged
    {
        private int? _donorId;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public int? DonorId
        {
            get => _donorId;
            set
            {
                _donorId = value;
                PropertyChanged?.Invoke(this, new(nameof(DonorId)));
            }
        }
    }

    [Fact]
    public void RecordPicker_Placeholder_IsNeverTheValue_AndSearchBoxHasAnAccessibleName()
    {
        var api = new FakeApiClient();
        api.On("SearchRecordsAsync", ApiResult<IReadOnlyList<RecordOption>>.Ok([]));
        UseFakeApiForPickers(api);
        var (text, hintVisible, name, selected) = StaHost.Run(() =>
        {
            var picker = new RecordPicker { Kind = RecordKind.Patient, Label = "Patient" };
            using var host = Hosted.Create(Canvas(500, 200, picker), 500, 200);
            host.Settle(100);
            var box = host.All<TextBox>().First();
            var hint = host.All<TextBlock>().First(t => t.Text == "Search by name or ID");
            return (box.Text, hint.IsVisible, AutomationProperties.GetName(box), picker.SelectedId);
        });

        Assert.Equal(string.Empty, text); // the hint is a separate element, never the text
        Assert.True(hintVisible);
        Assert.Equal("Patient", name);
        Assert.Null(selected);
    }

    [Fact]
    public void RecordPicker_PreselectedId_ShowsAChip_WithoutTypingAnId()
    {
        var api = new FakeApiClient();
        UseFakeApiForPickers(api);
        var (chip, searchBoxes) = StaHost.Run(() =>
        {
            var picker = new RecordPicker { Kind = RecordKind.Donor, Label = "Donor", SelectedOption = new RecordOption(42, "Jane Q. Public", "B-") };
            using var host = Hosted.Create(Canvas(500, 200, picker), 500, 200);
            host.Settle(150);
            return (host.All<TextBlock>().First(t => t.IsVisible && t.Text.StartsWith("Jane", StringComparison.Ordinal)).Text, host.All<TextBox>().Count(t => t.IsVisible));
        });

        Assert.Equal("Jane Q. Public · #42 · B-", chip);
        Assert.Equal(0, searchBoxes);
    }

    // Avatar and photos

    [Fact]
    public void Avatar_Default_IsOneSharedFrozenResource_AndPhotosDecodeAtALimitedSize()
    {
        var (same, frozen, decodedWidth, fromBytes, notAnImage) = StaHost.Run(() =>
        {
            var converter = new AvatarConverter();
            var first = converter.Convert(null!, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture);
            var second = converter.Convert(Array.Empty<byte>(), typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture);

            var big = System.Windows.Media.Imaging.BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgra32, null, new byte[1024 * 1024 * 4], 1024 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(big));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var decoded = PhotoDecoder.Decode(stream.ToArray(), AvatarConverter.DecodePixelWidth);
            var viaConverter = converter.Convert(stream.ToArray(), typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture);
            return (ReferenceEquals(first, second), ((Freezable)first).IsFrozen, decoded!.PixelWidth, viaConverter is System.Windows.Media.Imaging.BitmapSource, PhotoDecoder.Decode([1, 2, 3], 64));
        });

        Assert.True(same);
        Assert.True(frozen);
        Assert.Equal(AvatarConverter.DecodePixelWidth, decodedWidth); // 1024 px source decoded at 192
        Assert.True(fromBytes);
        Assert.Null(notAnImage);
    }

    [Fact]
    public void ShippedImages_AreSmall_AndEveryOneIsReferenced()
    {
        var desktop = Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop");
        var images = Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico")
            .ToList();
        var sources = string.Concat(Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".xaml" or ".cs" or ".csproj" or ".manifest" && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        Assert.All(images, i =>
        {
            Assert.True(new FileInfo(i).Length <= 50 * 1024, $"{Path.GetFileName(i)} is over 50 KB");
            Assert.Contains(Path.GetFileName(i), sources, StringComparison.OrdinalIgnoreCase); // unused images are removed from the build
        });
    }
}
