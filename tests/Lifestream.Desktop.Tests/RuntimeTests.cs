using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Services;
using Lifestream.Desktop.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lifestream.Desktop.Tests;

[Collection("Wpf")]
public sealed class RuntimeTests
{
    // Global exception handling (SHELL-01)

    private sealed class RecordingPresenter : IDialogPresenter
    {
        public List<(string Title, string Message, string? Details)> Errors { get; } = [];

        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb) => Task.FromResult(false);

        public Task ShowErrorAsync(string title, string message, string? details)
        {
            lock (Errors)
            {
                Errors.Add((title, message, details));
            }

            return Task.CompletedTask;
        }
    }

    private static (GlobalExceptionHandler Handler, RecordingPresenter Presenter) Install(Dispatcher dispatcher)
    {
        var presenter = new RecordingPresenter();
        var dialogs = new DialogService { Presenter = presenter };
        var reporter = new ErrorReporter(NullLogger<ErrorReporter>.Instance, dialogs);
        var handler = new GlobalExceptionHandler(reporter, dispatcher);
        handler.Install();
        return (handler, presenter);
    }

    [Fact]
    public void GlobalHandler_DispatcherException_ShowsDialog_AndTheUiThreadKeepsRunning()
    {
        var (reported, dialogs, ranAfter, friendly) = StaHost.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var (handler, presenter) = Install(dispatcher);
            using (handler)
            {
                var after = false;
                dispatcher.BeginInvoke(() => throw new InvalidOperationException("boom on the UI thread"));
                dispatcher.BeginInvoke(() => after = true);
                StaHost.Settle(200);
                return (handler.ReportedCount, presenter.Errors.Count, after, presenter.Errors.FirstOrDefault());
            }
        });

        Assert.Equal(1, reported);
        Assert.Equal(1, dialogs);
        Assert.True(ranAfter); // the dispatcher survived and processed the next item
        Assert.Equal("Something went wrong", friendly.Title);
        Assert.DoesNotContain("boom", friendly.Message, StringComparison.Ordinal); // the friendly text hides the exception
        Assert.Contains("boom on the UI thread", friendly.Details); // the detail is available behind the Details control
    }

    [Fact]
    public void GlobalHandler_UnobservedTaskException_ShowsDialog_NoCrash()
    {
        var (reported, dialogs) = StaHost.Run(() =>
        {
            var (handler, presenter) = Install(Dispatcher.CurrentDispatcher);
            using (handler)
            {
                Fault();
                for (var i = 0; i < 5 && handler.ReportedCount == 0; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(50);
                }

                return (handler.ReportedCount, presenter.Errors.Count);
            }
        });

        Assert.True(reported >= 1);
        Assert.True(dialogs >= 1);

        static void Fault() => _ = Task.Run(() => throw new InvalidOperationException("unobserved"));
    }

    [Fact]
    public void GlobalHandler_CommandException_IsReported_ThroughTheDefaultHandler_AndNeverEscapes()
    {
        var (dialogs, lastException, running) = StaHost.Run(() =>
        {
            var (handler, presenter) = Install(Dispatcher.CurrentDispatcher);
            using (handler)
            {
                var command = new AsyncRelayCommand(_ => throw new InvalidOperationException("command failed"));
                command.ExecuteAsync().GetAwaiter().GetResult();
                command.Execute(null); // the entry point a button uses
                StaHost.Settle(100);
                return (presenter.Errors.Count, command.LastException, command.IsRunning);
            }
        });

        Assert.Equal(2, dialogs);
        Assert.IsType<InvalidOperationException>(lastException);
        Assert.False(running);
    }

    [Fact]
    public void GlobalHandler_AppDomainException_IsReported_WithoutThrowing()
    {
        var reported = StaHost.Run(() =>
        {
            var (handler, _) = Install(Dispatcher.CurrentDispatcher);
            using (handler)
            {
                var method = typeof(GlobalExceptionHandler).GetMethod("OnDomainException", BindingFlags.Instance | BindingFlags.NonPublic)!;
                method.Invoke(handler, [null, new UnhandledExceptionEventArgs(new InvalidOperationException("worker thread"), false)]);
                return handler.ReportedCount;
            }
        });

        Assert.Equal(1, reported);
    }

    // No API call on the UI thread, and the dispatcher stays responsive while a screen loads over a slow link (SHELL-09)

    private sealed class SlowHandler(Func<int> uiThreadId, TimeSpan latency) : HttpMessageHandler
    {
        public List<int> CallerThreads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (CallerThreads)
            {
                CallerThreads.Add(Environment.CurrentManagedThreadId);
            }

            _ = uiThreadId();
            await Task.Delay(latency, cancellationToken); // a throttled network: every call takes this long
            return ScriptedHandler.Json(System.Net.HttpStatusCode.OK, """{"items":[],"total":0,"page":1,"pageSize":50}""");
        }
    }

    private sealed class ThreeCallScreen(ILifestreamApiClient api) : ViewModelBase
    {
        public int UiThread { get; private set; }

        public int FetchThread { get; private set; }

        protected override async Task FetchAsync(CancellationToken ct)
        {
            FetchThread = Environment.CurrentManagedThreadId;
            (await api.GetDonorsAsync(new Lifestream.Contracts.Donors.DonorQuery(), ct)).Unwrap();
            (await api.GetPatientsAsync(new Lifestream.Contracts.Patients.PatientQuery(), ct)).Unwrap();
            (await api.GetReceiptsAsync(new Lifestream.Contracts.Receipts.ReceiptQuery(), ct)).Unwrap();
        }

        protected override void Apply() => UiThread = Environment.CurrentManagedThreadId;
    }

    [Trait("Category", "Perf")] // asserts a wall-clock budget: runs in the perf pass (tests/perf/run-perf.ps1), not in the loaded default run
    [Trait("PerfPhase", "timing")]
    [Fact]
    public void Dispatcher_NeverGapsOver50ms_WhileAScreenLoadsOverAThrottledNetwork_AndNoApiCallRunsOnTheUiThread()
    {
        var (maxGap, ticks, callerThreads, uiThread, fetchThread, applyThread, state) = StaHost.Run(() =>
        {
            var uiThreadId = Environment.CurrentManagedThreadId;
            var handler = new SlowHandler(() => uiThreadId, TimeSpan.FromMilliseconds(250));
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
            using var api = new LifestreamApiClient(http, new SessionService());
            var screen = new ThreeCallScreen(api);

            // A probe that asks the dispatcher to run a callback every 5 ms and records the longest wait between callbacks.
            var dispatcher = Dispatcher.CurrentDispatcher;
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed;
            var worst = TimeSpan.Zero;
            var count = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) =>
            {
                var now = clock.Elapsed;
                if (now - last > worst)
                {
                    worst = now - last;
                }

                last = now;
                count++;
            };
            timer.Start();

            Task? load = null;
            dispatcher.BeginInvoke(() => load = screen.LoadAsync()); // started from the UI thread, as a button click would
            PumpWithoutBlocking(() => load is { IsCompleted: true }, 6000);
            timer.Stop();
            return (worst.TotalMilliseconds, count, handler.CallerThreads.ToList(), uiThreadId, screen.FetchThread, screen.UiThread, screen.State);
        });

        Assert.Equal(LoadState.Ready, state);
        Assert.True(ticks > 40, $"the probe only ran {ticks} times; the dispatcher was starved");
        Assert.True(maxGap < 50, $"the dispatcher stalled for {maxGap:0} ms");
        Assert.Equal(3, callerThreads.Count);
        Assert.DoesNotContain(uiThread, callerThreads); // no API call executed on the UI thread
        Assert.NotEqual(uiThread, fetchThread);
        Assert.Equal(uiThread, applyThread); // results are applied back on the UI thread
    }

    /// <summary>Runs the dispatcher until the condition holds, using a timer instead of sleeping so the UI thread is never blocked by the test itself.</summary>
    private static void PumpWithoutBlocking(Func<bool> done, int timeoutMilliseconds)
    {
        var frame = new DispatcherFrame();
        var started = Stopwatch.StartNew();
        var check = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        check.Tick += (_, _) =>
        {
            if (done() || started.ElapsedMilliseconds > timeoutMilliseconds)
            {
                check.Stop();
                frame.Continue = false;
            }
        };
        check.Start();
        Dispatcher.PushFrame(frame);
    }

    // Window chrome (SHELL-19, SHELL-20, SHELL-23)

    [Fact]
    public void Chrome_CaptionButtons_Are46x32_AndTheMaximizeHighlightIsItsHitArea()
    {
        var (min, max, close, hit) = StaHost.Run(() =>
        {
            var window = new TestWindow();
            window.ApplyTemplate();
            window.Measure(new Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));
            window.UpdateLayout();
            var minimize = (FrameworkElement)window.Template.FindName("PART_Minimize", window);
            var maximize = window.MaximizeButton!;
            maximize.Measure(Size.Empty.IsEmpty ? new Size(200, 200) : Size.Empty);
            maximize.Arrange(new Rect(new Point(0, 0), maximize.DesiredSize));
            var closeButton = (FrameworkElement)window.Template.FindName("PART_Close", window);
            // The highlight is drawn on the maximize element; its own bounds decide the hit test, so they are identical.
            var inside = window.IsWithinMaximize(new Point(maximize.ActualWidth - 0.5, maximize.ActualHeight - 0.5));
            var outside = window.IsWithinMaximize(new Point(maximize.ActualWidth + 0.5, 5)) || window.IsWithinMaximize(new Point(-0.5, 5));
            return ((minimize.Width, minimize.Height), (maximize.Width, maximize.Height), (closeButton.Width, closeButton.Height), (inside, outside));
        });

        Assert.Equal((46.0, 32.0), min);
        Assert.Equal((46.0, 32.0), max);
        Assert.Equal((46.0, 32.0), close);
        Assert.True(hit.inside);
        Assert.False(hit.outside);
    }

    private sealed class TestWindow : ChromeWindow
    {
    }

    [Fact]
    public void Chrome_ResizeEdgesSnapAltSpaceAndMonitorSettings_AreConfigured()
    {
        var (resize, captionHeight, aero, altSpace, noStyleKey, content) = StaHost.Run(() =>
        {
            var window = new TestWindow();
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            var keys = window.InputBindings.OfType<KeyBinding>().Select(k => (k.Key, k.Modifiers)).ToList();
            return (chrome.ResizeBorderThickness, chrome.CaptionHeight, chrome.UseAeroCaptionButtons, keys.Contains((Key.Space, ModifierKeys.Alt)), window.WindowStyle, window.ResizeMode);
        });

        Assert.True(resize.Left >= 6 && resize.Top >= 6 && resize.Right >= 6 && resize.Bottom >= 6); // edge and corner resizing
        Assert.Equal(32, captionHeight); // a drag area the height of the caption buttons; double-click on it maximizes
        Assert.False(aero); // the caption buttons are ours (46 x 32)
        Assert.True(altSpace);
        Assert.Equal(WindowStyle.SingleBorderWindow, noStyleKey); // keeps the system menu, Snap and Alt+Space
        Assert.Equal(ResizeMode.CanResize, content);

        var manifest = File.ReadAllText(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop/app.manifest"));
        Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal); // correct per-monitor maximize and DPI
    }

    [Fact]
    public void Chrome_Maximized_ShowsTheRestoreGlyph_AndRestoredShowsMaximize()
    {
        var (maximizedGlyph, normalGlyph, name) = StaHost.Run(() =>
        {
            var window = new TestWindow();
            window.ApplyTemplate();
            var glyph = (System.Windows.Controls.TextBlock)window.Template.FindName("MaximizeGlyph", window);
            var normal = glyph.Text;
            window.WindowState = WindowState.Maximized;
            window.ApplyTemplate();
            window.UpdateLayout();
            var maximized = glyph.Text;
            return (maximized, normal, System.Windows.Automation.AutomationProperties.GetName(window.MaximizeButton!));
        });

        Assert.Equal("", maximizedGlyph); // restore
        Assert.Equal("", normalGlyph);    // maximize
        Assert.Equal("Restore", name);
    }

    [Fact]
    public void Chrome_IsImplementedOnce_AndEveryWindowReusesIt()
    {
        var windows = typeof(App).Assembly.GetTypes().Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract && t.Namespace?.StartsWith("Lifestream.Desktop", StringComparison.Ordinal) == true).ToList();
        var notChrome = windows.Where(t => !typeof(ChromeWindow).IsAssignableFrom(t)).Select(t => t.Name).ToList();

        Assert.NotEmpty(windows);
        Assert.Empty(notChrome);
        Assert.Empty(typeof(App).Assembly.GetTypes().Where(t => t.Name.Contains("Spike", StringComparison.Ordinal)).Select(t => t.FullName)); // no spike code ships
        foreach (var file in Directory.EnumerateFiles(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop"), "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("<Window ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<ui:FluentWindow", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WindowChrome.WindowChrome", text, StringComparison.Ordinal); // no second chrome in a view
        }
    }

    [Fact]
    public void Windows_HaveTheRightTitles_AndTheMainWindowHasItsMinimumSize()
    {
        var (loginTitle, mainTitle, afterNavigation, minWidth, minHeight) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            rig.SignIn(Lifestream.Domain.Role.Admin);
            var shell = rig.Get<Lifestream.Client.Shell.ShellViewModel>();
            var login = new Lifestream.Desktop.Auth.LoginWindow { DataContext = rig.Get<Lifestream.Client.Auth.LoginViewModel>() };
            var main = new MainWindow { DataContext = shell };
            StaHost.Settle(50); // bindings of a window that has not been shown yet activate on the first dispatcher pass
            var initial = main.Title;
            shell.OpenAsync(Lifestream.Client.Shell.NavigationCatalog.Default.First(d => d.Title == "Donors")).GetAwaiter().GetResult();
            StaHost.Settle(100);
            return (login.Title, initial, main.Title, main.MinWidth, main.MinHeight);
        });

        Assert.Equal("Lifestream", loginTitle);
        Assert.True(mainTitle == "Lifestream", $"main window initial title was '{mainTitle}' (after navigation '{afterNavigation}')");
        Assert.Equal("Lifestream - Donors", afterNavigation);
        Assert.Equal(1024, minWidth);
        Assert.Equal(640, minHeight);
    }

    // View code-behind (SHELL-14) and view-model wiring (SHELL-13)

    [Fact]
    public void ViewCodeBehind_ContainsNoApiRepositorySqlOrBusinessCalls()
    {
        var forbidden = new[] { "ILifestreamApiClient", "LifestreamApiClient", "HttpClient", "SqlConnection", "SqlCommand", "Repository", "SearchRecordsAsync", "GetDonorsAsync", "GetPatientsAsync", "LoginAsync", "FulfilRequestAsync", "EntityFramework" };
        var files = Directory.EnumerateFiles(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop"), "*.xaml.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();

        Assert.True(files.Count >= 8, $"scanned {files.Count} code-behind files");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
            {
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} mentions {word}");
            }
        }
    }

    [Fact]
    public void Views_NeverSetTheirOwnDataContext_TheNavigationHostDoes()
    {
        var desktop = Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop");
        var viewFiles = Directory.EnumerateFiles(desktop, "*View.xaml.cs", SearchOption.AllDirectories).Where(f => !f.EndsWith("RecordPicker.xaml.cs", StringComparison.Ordinal)).ToList();
        var xamlViews = Directory.EnumerateFiles(desktop, "*View.xaml", SearchOption.AllDirectories).Where(f => !f.EndsWith("RecordPicker.xaml", StringComparison.Ordinal) && !f.EndsWith("ToastView.xaml", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(viewFiles);
        foreach (var file in viewFiles)
        {
            Assert.DoesNotContain("DataContext =", File.ReadAllText(file), StringComparison.Ordinal);
        }

        foreach (var file in xamlViews)
        {
            Assert.DoesNotContain(".DataContext>", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OneCommandClassExists_AcrossClientAndDesktop()
    {
        var commands = new[] { typeof(AsyncRelayCommand).Assembly, typeof(App).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(ICommand).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract && t.Namespace?.StartsWith("Lifestream", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .ToList();

        Assert.Equal([nameof(AsyncRelayCommand)], commands); // the two duplicate command classes of the old app are gone
    }

    [Fact]
    public void ThemeAndDesktopAssembly_DoNotReferenceDataAccess()
    {
        var references = typeof(App).Assembly.GetReferencedAssemblies().Select(r => r.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("System.Data.SqlClient", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r == "Lifestream.Data");
    }

    // Update check (SHELL-44)

    private sealed class FakeProbe(bool packaged) : IPackageIdentityProbe
    {
        public bool HasPackageIdentity() => packaged;
    }

    private sealed class CountingFeed(Version? latest) : IUpdateFeed
    {
        public int Checks { get; private set; }

        public int Applies { get; private set; }

        public Task<Version?> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return Task.FromResult(latest);
        }

        public Task ApplyAsync(CancellationToken ct)
        {
            Applies++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task UpdateCheck_IsSkipped_WhenRunningAsAMicrosoftStorePackage()
    {
        var feed = new CountingFeed(new Version(3, 0, 0));
        var factoryCalls = 0;
        var source = new StoreAwareUpdateSource(new FakeProbe(packaged: true), () =>
        {
            factoryCalls++;
            return feed;
        });

        var latest = await source.GetLatestVersionAsync(CancellationToken.None);
        await source.ApplyAsync(CancellationToken.None);

        Assert.Null(latest); // the Store updates a packaged install; the feed must not offer a second installer
        Assert.Equal(0, factoryCalls); // the release feed is never even constructed under MSIX
        Assert.Equal(0, feed.Checks);
        Assert.Equal(0, feed.Applies);
    }

    [Fact]
    public async Task UpdateCheck_UsesTheFeed_WhenNotAStorePackage_AndReportsTheNewerVersion()
    {
        var feed = new CountingFeed(new Version(3, 0, 0));
        var source = new StoreAwareUpdateSource(new FakeProbe(packaged: false), () => feed);

        var latest = await source.GetLatestVersionAsync(CancellationToken.None);
        await source.ApplyAsync(CancellationToken.None);

        Assert.Equal(new Version(3, 0, 0), latest);
        Assert.Equal(1, feed.Checks);
        Assert.Equal(1, feed.Applies);
    }

    [Fact]
    public async Task UpdateCheck_EndToEnd_ShowsTheUpdateReadyPrompt_OnlyOutsideTheStore()
    {
        async Task<string?> PromptFor(bool packaged)
        {
            var source = new StoreAwareUpdateSource(new FakeProbe(packaged), () => new CountingFeed(new Version(3, 0, 0)));
            var service = new Lifestream.Client.Updates.UpdateService(source, new Version(2, 0, 0), NullLogger<Lifestream.Client.Updates.UpdateService>.Instance);
            await service.CheckAsync();
            return service.IsUpdateReady ? service.Message : null;
        }

        Assert.Contains("3.0.0", await PromptFor(packaged: false));
        Assert.Null(await PromptFor(packaged: true));
    }

    [Fact]
    public void VelopackFeed_ForAnUninstalledBuild_ReportsNoUpdate()
    {
        var result = StaHost.Run(() =>
        {
            try
            {
                return new VelopackFeed("https://github.com/example/none").CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                return null; // no Velopack locator outside an installed or Velopack-started process: nothing to update
            }
        });

        Assert.Null(result);
    }

    [Fact]
    public void PackageIdentityProbe_ReportsNoPackage_ForAnUnpackagedProcess()
    {
        Assert.False(new WindowsPackageIdentityProbe().HasPackageIdentity()); // the test host has no MSIX identity
    }

    [Fact]
    public void Startup_RegistersTheStoreAwareUpdateSource()
    {
        var source = File.ReadAllText(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop/App.cs"));

        Assert.Contains("new StoreAwareUpdateSource(new WindowsPackageIdentityProbe(), () => new VelopackFeed(ReleaseRepositoryUrl))", source, StringComparison.Ordinal);
    }
}
