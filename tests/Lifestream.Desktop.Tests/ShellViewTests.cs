using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Services;
using Lifestream.Desktop.Shell;

namespace Lifestream.Desktop.Tests;

[Collection("Wpf")]
public sealed class ShellViewTests
{
    private static (Rig Rig, ShellViewModel Shell) ShellFor(Lifestream.Domain.Role role = Lifestream.Domain.Role.Admin, bool demo = false, Version? latest = null)
    {
        var rig = new Rig(latestVersion: latest);
        rig.SignIn(role, demo);
        return (rig, rig.Get<ShellViewModel>());
    }

    private static ShellView ViewFor(ShellViewModel shell) => new() { DataContext = shell };

    private static LoginView LoginFor(LoginViewModel vm) => new() { DataContext = vm };

    // Shell layout (SHELL-20, SHELL-21)

    [Fact]
    public void Shell_At1024x640_NoClipping()
    {
        var (scrollable, logoOutsideScroll, navHeight, itemCount, overlaps, accountSize, titleMatchesMenu) = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor();
            using (rig)
            {
                var view = ViewFor(shell);
                using var host = Hosted.Create(view, 1024, 608); // 640 minus the 32 DIP caption
                _ = shell.StartAsync();
                host.Settle(400);
                host.Capture("Shell_At1024x640.png");

                var scroll = host.Find<ScrollViewer>("NavScroll");
                var logo = host.Find<StackPanel>("LogoRow");
                var inScroll = Ancestors(logo).Any(a => a == scroll);
                var radios = host.All<RadioButton>().ToList();
                var account = host.Find<Button>("AccountButton");
                var title = host.All<TextBlock>().First(t => t.Style is not null && t.Text == shell.PageTitle && t.FontSize == 24);
                var titleBounds = Hosted.BoundsOf(title, view);
                var accountBounds = Hosted.BoundsOf(account, view);
                var navBounds = radios.Select(r => Hosted.BoundsOf(r, view)).ToList();
                var overlap = navBounds.Any(b => b.Right > 232 + 0.5) || Overlaps(titleBounds, accountBounds);
                var selected = radios.Single(r => r.IsChecked == true);
                return (scroll.ScrollableHeight, !inScroll, scroll.ViewportHeight, radios.Count, overlap, (account.ActualWidth, account.ActualHeight), (string)selected.Content == shell.PageTitle);
            }
        });

        Assert.Equal(0, scrollable); // the sidebar does not scroll at 640 DIP
        Assert.True(logoOutsideScroll); // the logo is pinned outside the scroll region
        Assert.True(navHeight > 300);
        Assert.Equal(8, itemCount);
        Assert.False(overlaps);
        Assert.True(accountSize.ActualWidth >= 32 && accountSize.ActualHeight >= 32);
        Assert.True(titleMatchesMenu); // the selected menu label is the page title
    }

    /// <summary>True when two rectangles share area (touching edges do not count).</summary>
    private static bool Overlaps(Rect a, Rect b)
    {
        var overlap = Rect.Intersect(a, b);
        return !overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(element); parent is not null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
    }

    public static TheoryData<int, int, double> Sizes()
    {
        var data = new TheoryData<int, int, double>();
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            foreach (var (w, h) in new[] { (1024, 640), (1366, 768), (1920, 1080), (2560, 1440) })
            {
                // The window cannot be smaller than 1024 x 640 DIP, so a physical size that would give less is not a reachable state.
                if (w / scale >= 1024 && h / scale >= 640)
                {
                    data.Add(w, h, scale);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Shell_Reflows_WithoutClippedOrOverlappingContent_AtEveryReachableSizeAndScale(int pixelWidth, int pixelHeight, double scale)
    {
        var problems = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor();
            using (rig)
            {
                var width = pixelWidth / scale;
                var height = (pixelHeight / scale) - 32;
                var view = ViewFor(shell);
                using var host = Hosted.Create(view, width, height);
                _ = shell.StartAsync();
                host.Settle(300);

                var list = new List<string>();
                var bounds = new Rect(0, 0, width, height);
                var scroll = host.Find<ScrollViewer>("NavScroll");
                if (scroll.ScrollableHeight > 0)
                {
                    list.Add("sidebar scrolls");
                }

                foreach (var element in host.All<FrameworkElement>().Where(e => e.IsVisible && e.ActualWidth > 0 && e is TextBlock or Button or RadioButton))
                {
                    var rect = Hosted.BoundsOf(element, view);
                    if (rect.Left < -0.5 || rect.Top < -0.5 || rect.Right > width + 0.5 || rect.Bottom > height + 0.5)
                    {
                        list.Add($"{element.GetType().Name} outside the window: {rect}");
                    }
                }

                var title = host.All<TextBlock>().First(t => t.FontSize == 24);
                var account = host.Find<Button>("AccountButton");
                if (Overlaps(Hosted.BoundsOf(title, view), Hosted.BoundsOf(account, view)))
                {
                    list.Add("title overlaps the account control");
                }

                _ = bounds;
                if (scale == 1.0 && pixelWidth == 2560)
                {
                    host.Capture("Shell_At2560x1440.png");
                }

                return list;
            }
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void Shell_Account_Control_HasTheNameAvatarChevron_AndAMenuWithThreeEntries()
    {
        var (name, text, menuItems, hasImage, tabStop) = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor();
            using (rig)
            {
                var view = ViewFor(shell);
                using var host = Hosted.Create(view, 1100, 600);
                host.Settle(200);
                var account = host.Find<Button>("AccountButton");
                var texts = host.All<TextBlock>().Where(t => account.IsAncestorOf(t)).Select(t => t.Text).ToList();
                return (AutomationProperties.GetName(account), string.Join("|", texts), account.ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header).ToList(), host.All<Image>().Any(i => account.IsAncestorOf(i)), account.IsTabStop && account.Focusable);
            }
        });

        Assert.Equal("Account menu", name);
        Assert.Contains("Test User", text);
        Assert.Contains("", text); // chevron
        Assert.Equal(["Profile", "Change password", "Sign out"], menuItems);
        Assert.True(hasImage); // the avatar
        Assert.True(tabStop); // reachable by keyboard
    }

    [Fact]
    public void Shell_NonAdmin_DoesNotSeeStaff_InTheSidebar()
    {
        var (adminItems, staffItems) = StaHost.Run(() =>
        {
            List<string> Items(Lifestream.Domain.Role role)
            {
                var (rig, shell) = ShellFor(role);
                using (rig)
                {
                    using var host = Hosted.Create(ViewFor(shell), 1100, 600);
                    host.Settle(200);
                    return [.. host.All<RadioButton>().Select(r => (string)r.Content)];
                }
            }

            return (Items(Lifestream.Domain.Role.Admin), Items(Lifestream.Domain.Role.Staff));
        });

        Assert.Contains("Staff", adminItems);
        Assert.DoesNotContain("Staff", staffItems);
        Assert.Equal(7, staffItems.Count);
    }

    [Fact]
    public void Shell_UpdateReady_ShowsAnUpdatePromptWithARestartButton()
    {
        var (prompt, button) = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor(latest: new Version(2, 1, 0));
            using (rig)
            {
                using var host = Hosted.Create(ViewFor(shell), 1100, 600);
                _ = shell.StartAsync();
                Hosted.PumpUntil(() => shell.UpdatePrompt is not null, 2000);
                host.Settle(200);
                host.Capture("Shell_UpdateReady.png");
                var text = host.All<TextBlock>().FirstOrDefault(t => t.IsVisible && t.Text.Contains("2.1.0", StringComparison.Ordinal))?.Text;
                var restart = host.All<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Restart and update");
                return (text, restart is not null);
            }
        });

        Assert.Contains("Version 2.1.0 is ready", prompt);
        Assert.True(button);
    }

    // View locator and navigation

    [Fact]
    public void ViewLocator_FindsAViewForEveryDestination_AndForAccessDenied()
    {
        var found = NavigationCatalog.Default.Select(d => (d.Title, View: ViewLocator.Resolve(d.ViewModelType))).ToList();

        Assert.All(found, f => Assert.NotNull(f.View));
        Assert.Equal(typeof(AccessDeniedView), ViewLocator.Resolve(typeof(AccessDeniedViewModel)));
        Assert.Null(ViewLocator.Resolve(typeof(string)));
    }

    [Fact]
    public void Navigation_ShowsOneViewPerVisit_SetsItsDataContextOnce_AndMakesNoApiCallForPlaceholders()
    {
        var (contextChanges, views, calls, titles) = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor();
            using (rig)
            {
                var view = ViewFor(shell);
                using var host = Hosted.Create(view, 1100, 640);
                host.Settle(100);
                var pageHost = host.Find<ContentControl>("PageHost");
                var changes = new List<int>();
                var seen = new List<FrameworkElement>();

                void Visit(string title)
                {
                    Hosted.Await(() => shell.NavigateCommand.ExecuteAsync(shell.Items.First(i => i.Title == title)));
                    host.Settle(150);
                    var page = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(pageHost, 0) is ContentPresenter presenter
                        ? (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0)
                        : throw new InvalidOperationException("no page");
                    seen.Add(page);
                    changes.Add(page.DataContext == shell.CurrentPage ? 1 : 0);
                }

                Visit("Donors");
                Visit("Stock");
                Visit("Receipts");
                return (changes, seen.Distinct().Count(), rig.Api.Calls.Count, shell.PageTitle);
            }
        });

        Assert.Equal([1, 1, 1], contextChanges); // each page's DataContext is its view model, set by navigation
        Assert.Equal(3, views); // one view per visit
        Assert.Equal(0, calls);
        Assert.Equal("Receipts", titles);
    }

    [Fact]
    public void Shell_Shortcuts_CtrlF_FocusesSearch_CtrlN_RunsNew_AreBoundOnTheShell()
    {
        var (bindings, searchRaised, newRan) = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor();
            using (rig)
            {
                var view = ViewFor(shell);
                using var host = Hosted.Create(view, 1100, 640);
                host.Settle(100);
                var keyBindings = view.InputBindings.OfType<KeyBinding>().Select(k => (k.Key, k.Modifiers)).ToList();

                var nav = rig.Get<Lifestream.Client.Navigation.INavigationService>();
                nav.NavigateTo<ShortcutPage>().GetAwaiter().GetResult();
                var page = (ShortcutPage)nav.Current!;
                var raised = 0;
                page.FocusSearchRequested += (_, _) => raised++;
                shell.SearchCommand.ExecuteAsync().GetAwaiter().GetResult();
                shell.NewRecordCommand.ExecuteAsync().GetAwaiter().GetResult();
                return (keyBindings, raised, page.NewRan);
            }
        });

        Assert.Contains((Key.F, ModifierKeys.Control), bindings);
        Assert.Contains((Key.N, ModifierKeys.Control), bindings);
        Assert.Equal(1, searchRaised);
        Assert.Equal(1, newRan);
    }

    private sealed class ShortcutPage : ViewModelBase
    {
        public ShortcutPage()
            : base(LoadState.Ready)
        {
            NewCommand = new AsyncRelayCommand(_ =>
            {
                NewRan++;
                return Task.CompletedTask;
            });
        }

        public int NewRan { get; private set; }

        public override AsyncRelayCommand NewCommand { get; }
    }

    // Login view (SHELL-02..06)

    [Fact]
    public void LoginWindow_Renders()
    {
        var (usernameText, buttonEnabled, hasForgot, texts, distinct) = StaHost.Run(() =>
        {
            using var rig = new Rig();
            var vm = rig.Get<LoginViewModel>();
            var view = LoginFor(vm);
            using var host = Hosted.Create(view, 520, 688);
            host.Settle(300);
            var png = host.Capture("LoginWindow.png");
            var boxes = host.All<TextBox>().ToList();
            var password = host.All<PasswordBox>().First();
            var signIn = host.Find<Button>("SignInButton");
            var visibleTexts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (boxes.First().Text + password.Password, signIn.IsEnabled, visibleTexts.Concat(host.All<Button>().Select(b => b.Content as string ?? string.Empty)).Any(t => t.Contains("Forgot", StringComparison.OrdinalIgnoreCase)), visibleTexts, StaHost.CountDistinctColours(png));
        });

        Assert.Equal(string.Empty, usernameText);
        Assert.False(buttonEnabled);
        Assert.False(hasForgot);
        Assert.Contains("Lifestream", texts);
        Assert.True(distinct > 8);
    }

    [Fact]
    public void Login_TryTheDemoButton_IsOffered_AndNoCredentialsAreOnScreen()
    {
        var (demoText, demoCommandCalls, texts) = StaHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(LoginFor(vm), 520, 688);
            host.Settle(200);
            var demo = host.Find<Button>("DemoButton");
            demo.Command.Execute(null);
            Hosted.PumpUntil(() => rig.Session.IsAuthenticated, 2000);
            return ((string)demo.Content, rig.Api.CountOf("DemoLoginAsync"), host.All<TextBlock>().Select(t => t.Text).ToList());
        });

        Assert.Equal("Try the demo", demoText);
        Assert.Equal(1, demoCommandCalls);
        Assert.DoesNotContain(texts, t => t.Contains("password:", StringComparison.OrdinalIgnoreCase) || t.Contains("admin123", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Login_SignIn_ShowsBusyWithin100ms_WithSpinnerAndSigningInText_ReadOnlyFields_EnterSubmits()
    {
        var (elapsed, busyText, spinnerVisible, fieldsReadOnly, isDefault, status, afterFailure) = StaHost.Run(() =>
        {
            using var rig = new Rig();
            var gate = new TaskCompletionSource<ApiResult<TokenResponse>>();
            rig.Api.On<TokenResponse>("LoginAsync", _ => gate.Task);
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(LoginFor(vm), 520, 688);
            host.Settle(200);
            vm.Username = "nadeem";
            vm.Password = "pw";
            host.Settle(100);
            var signIn = host.Find<Button>("SignInButton");

            var clock = Stopwatch.StartNew();
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(signIn)!).Invoke(); // what Enter does through IsDefault (queued to the dispatcher)
            var busyLayer = (FrameworkElement)signIn.Template.FindName("BusyLayer", signIn);
            var label = (TextBlock)signIn.Template.FindName("BusyLabel", signIn);
            var visible = Hosted.PumpUntil(() => busyLayer.IsVisible, 1000); // the click, the view model and the layout, all included
            clock.Stop();
            var statusWhileBusy = AutomationProperties.GetItemStatus(signIn);
            host.Settle(100);
            host.Capture("LoginWindow_Busy.png");
            var username = host.All<TextBox>().First(t => t.Text == "nadeem");
            var readOnly = username.IsReadOnly && host.All<PasswordField>().First().IsReadOnly;

            gate.SetResult(FakeApiClient.Fail<TokenResponse>(401, "unauthorized"));
            Hosted.PumpUntil(() => !vm.IsBusy, 2000);
            host.Settle(150);
            host.Capture("LoginWindow_WrongCredentials.png");
            var after = (signIn.IsEnabled, host.All<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("don't match", StringComparison.Ordinal)));
            return (clock.ElapsedMilliseconds, label.Text, visible, readOnly, signIn.IsDefault, statusWhileBusy, after);
        });

        Assert.True(elapsed < 100, $"busy state took {elapsed} ms");
        Assert.Equal("Signing in...", busyText);
        Assert.True(spinnerVisible, "spinner layer visible");
        Assert.True(fieldsReadOnly, "fields read-only while busy");
        Assert.True(isDefault, "Enter submits through IsDefault");
        Assert.Equal("Busy", status);
        Assert.True(afterFailure.Item1); // re-enabled
        Assert.True(afterFailure.Item2); // inline message
    }

    [Fact]
    public void Login_ServerUnreachableAndWaking_ShowDistinctMessagesWithRetry()
    {
        var results = StaHost.Run(() =>
        {
            var list = new List<(string Case, string Message, bool Retry)>();
            foreach (var (name, answer) in new (string, ApiResult<TokenResponse>)[]
            {
                ("unreachable", FakeApiClient.Unreachable<TokenResponse>()),
                ("waking", FakeApiClient.Fail<TokenResponse>(503, Lifestream.Contracts.ProblemCodes.DatabaseWaking)),
            })
            {
                using var rig = new Rig();
                rig.Api.On("LoginAsync", answer);
                var vm = rig.Get<LoginViewModel>();
                using var host = Hosted.Create(LoginFor(vm), 520, 688);
                host.Settle(100);
                vm.Username = "u";
                vm.Password = "p";
                vm.SignInCommand.ExecuteAsync().GetAwaiter().GetResult();
                host.Settle(200);
                host.Capture($"LoginWindow_{name}.png");
                var message = host.All<TextBlock>().First(t => t.IsVisible && t.Text.StartsWith(vm.Message![..10], StringComparison.Ordinal)).Text;
                var retry = host.All<Button>().Any(b => b.IsVisible && b.Content as string == "Retry");
                list.Add((name, message, retry));
            }

            return list;
        });

        Assert.Equal(2, results.Select(r => r.Message).Distinct().Count());
        Assert.All(results, r => Assert.True(r.Retry));
    }

    [Fact]
    public void Login_ProblemMessage_IsAnAssertiveLiveRegion_AndFieldsHaveLabels()
    {
        var (live, labels, names) = StaHost.Run(() =>
        {
            using var rig = new Rig();
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(LoginFor(vm), 520, 688);
            host.Settle(200);
            var problem = host.All<Border>().First(b => AutomationProperties.GetName(b) == "Sign-in problem");
            var fields = host.All<FormField>().Select(f => f.Label).ToList();
            var inputs = host.All<TextBox>().Select(AutomationProperties.GetName).Concat(host.All<PasswordField>().Select(AutomationProperties.GetName)).ToList();
            return (AutomationProperties.GetLiveSetting(problem), fields, inputs);
        });

        Assert.Equal(AutomationLiveSetting.Assertive, live);
        Assert.Equal(["Username", "Password"], labels); // persistent visible labels
        Assert.Contains("Username", names);
        Assert.Contains("Password", names);
    }

    // UI Automation and keyboard (SHELL-34, SHELL-35)

    private static IEnumerable<FrameworkElement> Interactive(Hosted host) =>
        host.All<FrameworkElement>().Where(e => e is ButtonBase or TextBoxBase or PasswordBox or ComboBox or CheckBox or RadioButton or MenuItem or Selector or Slider or PasswordField or DataGrid);

    [Fact]
    public void AllInteractiveControls_HaveAutomationNames()
    {
        var unnamed = StaHost.Run(() =>
        {
            var offenders = new List<string>();
            void Check(Hosted host, string where)
            {
                foreach (var element in Interactive(host).Where(e => e.IsVisible && (e is not TextBoxBase || e is not TextBox { IsReadOnly: true })))
                {
                    var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                    if (peer is null)
                    {
                        continue; // not exposed (template internals such as the scroll viewer's bars)
                    }

                    if (element is ScrollBar or Thumb or RepeatButton)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(peer.GetName()) && string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
                    {
                        offenders.Add($"{where}: {element.GetType().Name} {element.Name}");
                    }
                }
            }

            using (var rig = new Rig())
            {
                using var host = Hosted.Create(LoginFor(rig.Get<LoginViewModel>()), 520, 688);
                host.Settle(250);
                Check(host, "login");
            }

            var (rig2, shell) = ShellFor();
            using (rig2)
            {
                using var host = Hosted.Create(ViewFor(shell), 1100, 640);
                _ = shell.StartAsync();
                host.Settle(300);
                Check(host, "shell");
            }

            var dialog = DialogWindow.CreateConfirm("Delete donor 'X'?", "Body", "Delete donor");
            foreach (var button in new[] { dialog.Cancel, dialog.Primary })
            {
                if (UIElementAutomationPeer.CreatePeerForElement(button)!.GetName().Length == 0)
                {
                    offenders.Add("dialog: " + button.Name);
                }
            }

            return offenders;
        });

        Assert.Empty(unnamed);
    }

    [Fact]
    public void Controls_ExposeNameRoleAndState_ToUIAutomation()
    {
        var (roles, selectedState, toggleState) = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor();
            using (rig)
            {
                using var host = Hosted.Create(ViewFor(shell), 1100, 640);
                _ = shell.StartAsync();
                host.Settle(300);
                var radio = host.All<RadioButton>().First(r => r.IsChecked == true);
                var account = host.Find<Button>("AccountButton");
                var peers = new[] { radio, (FrameworkElement)account }.Select(e => UIElementAutomationPeer.CreatePeerForElement(e)!).ToList();
                var selection = (ISelectionItemProvider)peers[0].GetPattern(PatternInterface.SelectionItem);
                var pw = new PasswordField();
                using var host2 = Hosted.Create(pw, 300, 80);
                host2.Settle(100);
                var toggle = host2.All<ToggleButton>().First();
                var toggleProvider = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle)!.GetPattern(PatternInterface.Toggle);
                return (peers.Select(p => p.GetAutomationControlType()).ToList(), selection.IsSelected, toggleProvider.ToggleState);
            }
        });

        Assert.Equal([AutomationControlType.RadioButton, AutomationControlType.Button], roles);
        Assert.True(selectedState); // state: the current page is a selected item
        Assert.Equal(ToggleState.Off, toggleState);
    }

    [Fact]
    public void Keyboard_NoCustomTabIndex_TabOrderFollowsVisualOrder_OnTheLoginForm()
    {
        var order = StaHost.Run(() =>
        {
            using var rig = new Rig();
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(LoginFor(vm), 520, 688);
            host.Settle(250);
            var stops = host.All<Control>().Where(c => c.IsTabStop && c.Focusable && c.IsVisible && c.IsEnabled && c is TextBox or PasswordBox or ToggleButton or Button).ToList();
            return stops.Select(c => (c.GetType().Name, Y: Hosted.BoundsOf(c, host.Element).Top, X: Hosted.BoundsOf(c, host.Element).Left)).ToList();
        });

        Assert.True(order.Count >= 4, "username, password, show toggle and demo (sign in is disabled while the form is empty, so Tab skips it)");
        for (var i = 1; i < order.Count; i++)
        {
            Assert.True(order[i].Y >= order[i - 1].Y - 0.5, $"tab stop {i} ({order[i].Item1}) comes before the previous one on screen");
        }

        var views = Directory.EnumerateFiles(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop"), "*.xaml", SearchOption.AllDirectories);
        Assert.All(views, f => Assert.DoesNotContain("TabIndex", File.ReadAllText(f), StringComparison.Ordinal));
    }

    [Fact]
    public void FocusVisual_IsATwoPixelRing_AtThreeToOneContrast()
    {
        var (thickness, brushColour) = StaHost.Run(() =>
        {
            var resources = Lifestream.Desktop.Theme.ThemeLoader.Resources;
            var style = (Style)resources[SystemParameters.FocusVisualStyleKey];
            var template = (ControlTemplate)style.Setters.OfType<Setter>().First(s => s.Property == Control.TemplateProperty).Value;
            var rect = (System.Windows.Shapes.Rectangle)template.LoadContent();
            return (rect.StrokeThickness, ((System.Windows.Media.SolidColorBrush)resources["Brush.FocusRing"]).Color);
        });

        Assert.Equal(2, thickness);
        Assert.Equal(0xFF, brushColour.R); // #FFB4BF: 11.01:1 on the canvas (ThemeTests checks every pair)
    }

    // Toasts through the shell

    [Fact]
    public void Shell_ShowsToastsFromTheService()
    {
        var texts = StaHost.Run(() =>
        {
            var (rig, shell) = ShellFor();
            using (rig)
            {
                using var host = Hosted.Create(ViewFor(shell), 1100, 640);
                host.Settle(150);
                rig.Get<IToastService>().ShowForRecord(ToastKind.Success, "Donor", "Ayşe Öztürk", "saved", "Undo", () => Task.CompletedTask);
                host.Settle(250);
                return host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            }
        });

        Assert.Contains("Donor 'Ayşe Öztürk' saved.", texts);
    }
}
