using System.Reflection;
using System.Windows.Input;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

public sealed class ViewModelTests
{
    private sealed class TestViewModel : ViewModelBase
    {
        public Func<CancellationToken, Task> Fetch { get; set; } = _ => Task.CompletedTask;

        public bool NoData { get; set; }

        public int Applied { get; private set; }

        public int? FetchThreadId { get; private set; }

        public int? ApplyThreadId { get; private set; }

        protected override bool HasNoData => NoData;

        protected override Task FetchAsync(CancellationToken ct)
        {
            FetchThreadId = Environment.CurrentManagedThreadId;
            return Fetch(ct);
        }

        protected override void Apply()
        {
            Applied++;
            ApplyThreadId = Environment.CurrentManagedThreadId;
        }
    }

    [Fact]
    public void ViewModelBase_LoadNeverRunsOnDispatcherThread()
    {
        // Run the load from a thread with a synchronization context (like the UI thread) and check where the API work ran.
        var callerThread = 0;
        TestViewModel? vm = null;
        var thread = new Thread(() =>
        {
            using var context = new SingleThreadContext();
            SynchronizationContext.SetSynchronizationContext(context);
            callerThread = Environment.CurrentManagedThreadId;
            vm = new TestViewModel();
            vm.Fetch = ct => Task.Delay(20, ct);
            var load = vm.LoadAsync();
            context.Pump(load);
        });
        thread.Start();
        thread.Join();

        Assert.NotNull(vm);
        Assert.NotEqual(callerThread, vm.FetchThreadId);
        Assert.Equal(callerThread, vm.ApplyThreadId); // results are applied back on the UI thread
        Assert.Equal(LoadState.Ready, vm.State);
    }

    /// <summary>A minimal single-thread message pump so continuations return to the thread that started the load.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Dispose() => _queue.Dispose();

        public void Pump(Task until)
        {
            using var done = new ManualResetEventSlim();
            _ = until.ContinueWith(_ => _queue.Add((_ => done.Set(), null)), TaskScheduler.Default);
            while (!done.IsSet)
            {
                var (callback, state) = _queue.Take();
                callback(state);
            }
        }
    }

    [Fact]
    public async Task ViewModelBase_LoadSetsLoadingSynchronously_ThenReady()
    {
        var vm = new TestViewModel { State = LoadState.Ready };
        var gate = new TaskCompletionSource();
        vm.Fetch = _ => gate.Task;
        var states = new List<LoadState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.State))
            {
                states.Add(vm.State);
            }
        };

        var load = vm.LoadAsync();
        Assert.Equal(LoadState.Loading, vm.State); // before any await completes: the skeleton can render in the next frame
        Assert.True(vm.IsLoading);
        Assert.True(vm.ShowSkeleton);

        gate.SetResult();
        await load;

        Assert.Equal([LoadState.Loading, LoadState.Ready], states);
        Assert.Equal(1, vm.Applied);
    }

    [Fact]
    public async Task ViewModelBase_NoData_ShowsEmptyState()
    {
        var vm = new TestViewModel { NoData = true };

        await vm.LoadAsync();

        Assert.Equal(LoadState.Empty, vm.State);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task ViewModelBase_ApiProblem_ShowsErrorWithPlainCause_AndDetailSeparately()
    {
        var vm = new TestViewModel { Fetch = _ => throw new ApiProblemException(new ApiProblem(0, ClientProblemCodes.Unreachable, "socket 10061", "corr-9")) };

        await vm.LoadAsync();

        Assert.Equal(LoadState.Error, vm.State);
        Assert.Equal("Can't reach the server. Check your internet connection.", vm.ErrorMessage);
        Assert.Contains("corr-9", vm.ErrorDetail);
        Assert.DoesNotContain("socket", vm.ErrorMessage!);
    }

    [Fact]
    public async Task ViewModelBase_UnexpectedException_BecomesErrorState_NotACrash()
    {
        var vm = new TestViewModel { Fetch = _ => throw new InvalidOperationException("secret internal detail") };
        var seen = new List<Exception>();
        var previous = AsyncRelayCommand.DefaultErrorHandler;
        AsyncRelayCommand.DefaultErrorHandler = seen.Add;
        try
        {
            await vm.LoadAsync();
        }
        finally
        {
            AsyncRelayCommand.DefaultErrorHandler = previous;
        }

        Assert.Equal(LoadState.Error, vm.State);
        Assert.DoesNotContain("secret", vm.ErrorMessage!);
        Assert.Single(seen);
    }

    [Fact]
    public async Task ViewModelBase_WakingServer_ShowsWakingState_ThenBackToLoading()
    {
        var status = new ServerStatus();
        var vm = new TestViewModel();
        vm.Attach(status, TimeProvider.System);
        var gate = new TaskCompletionSource();
        vm.Fetch = _ => gate.Task;

        var load = vm.LoadAsync();
        status.Enter();
        Assert.Equal(LoadState.Waking, vm.State);
        Assert.True(vm.IsWaking);
        Assert.True(vm.ShowSkeleton);

        status.Leave();
        Assert.Equal(LoadState.Loading, vm.State);
        gate.SetResult();
        await load;
        Assert.Equal(LoadState.Ready, vm.State);
    }

    [Fact]
    public async Task ViewModelBase_StillLoadingAfter10Seconds_SwitchesToErrorWithRetry()
    {
        var clock = new ManualTimeProvider();
        var vm = new TestViewModel();
        vm.Attach(null, clock);
        vm.Fetch = async ct => await Task.Delay(Timeout.Infinite, ct);

        var load = vm.LoadAsync();
        clock.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(LoadState.Loading, vm.State);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoadState.Error, vm.State);
        Assert.Equal("This is taking longer than expected.", vm.ErrorMessage);

        // Retry: a fresh load with data replaces the error.
        vm.Fetch = _ => Task.CompletedTask;
        await vm.LoadAsync();
        Assert.Equal(LoadState.Ready, vm.State);
    }

    [Fact]
    public async Task ViewModelBase_Cancel_LeavesErrorStateWithRetry_NotASkeleton()
    {
        var vm = new TestViewModel { Fetch = async ct => await Task.Delay(Timeout.Infinite, ct) };

        var load = vm.LoadAsync();
        vm.CancelLoad();
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoadState.Error, vm.State);
    }

    [Fact]
    public void EveryViewModel_ExposesBindableIsLoadingAndIsBusy()
    {
        var types = ViewModelTypes();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var isLoading = type.GetProperty(nameof(ViewModelBase.IsLoading));
            var isBusy = type.GetProperty(nameof(ViewModelBase.IsBusy));
            Assert.NotNull(isLoading);
            Assert.NotNull(isBusy);
            Assert.True(typeof(System.ComponentModel.INotifyPropertyChanged).IsAssignableFrom(type), type.Name);
        }
    }

    [Fact]
    public void IsBusy_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsBusy = true;

        Assert.Contains(nameof(ViewModelBase.IsBusy), raised);
    }

    [Fact]
    public void NoClientType_HasAPublicMutableField()
    {
        var offenders = typeof(ClientAssembly).Assembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && !t.IsEnum)
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => !f.IsLiteral && !f.IsInitOnly)
                .Select(f => $"{t.Name}.{f.Name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryViewModelMember_ThatIsPublicAndWritable_RaisesPropertyChanged()
    {
        // Each writable public property of a view model must announce changes: generated properties call OnPropertyChanged.
        var failures = new List<string>();
        foreach (var type in ViewModelTypes().Where(t => t.GetConstructors().Any(c => c.GetParameters().Length == 0)))
        {
            var vm = (ViewModelBase)Activator.CreateInstance(type)!;
            foreach (var property in type.GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && p.DeclaringType == type))
            {
                var value = Sample(property.PropertyType);
                if (value is null)
                {
                    continue;
                }

                var raised = false;
                vm.PropertyChanged += (_, e) => raised |= e.PropertyName == property.Name;
                property.SetValue(vm, value);
                if (!raised)
                {
                    failures.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(failures);
    }

    private static object? Sample(Type type) =>
        type == typeof(string) ? "x" : type == typeof(bool) ? true : type == typeof(int) ? 5 : null;

    private static List<Type> ViewModelTypes() =>
        [.. typeof(ClientAssembly).Assembly.GetTypes().Where(t => typeof(ViewModelBase).IsAssignableFrom(t) && !t.IsAbstract)];

    [Fact]
    public void ThereIsExactlyOneCommandClass_InClient()
    {
        var commands = typeof(ClientAssembly).Assembly.GetTypes().Where(t => typeof(ICommand).IsAssignableFrom(t) && !t.IsInterface).ToList();

        var single = Assert.Single(commands);
        Assert.Equal(nameof(AsyncRelayCommand), single.Name);
    }

    [Fact]
    public async Task AsyncRelayCommand_IsRunning_CanExecuteFalseWhileRunning()
    {
        var gate = new TaskCompletionSource();
        var command = new AsyncRelayCommand(_ => gate.Task);
        var canExecuteChanges = 0;
        command.CanExecuteChanged += (_, _) => canExecuteChanges++;
        Assert.True(command.CanExecute(null));

        var running = command.ExecuteAsync();

        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        gate.SetResult();
        await running;
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
        Assert.Equal(2, canExecuteChanges);
    }

    [Fact]
    public async Task AsyncRelayCommand_CapturesException_NeverThrows_AndReportsIt()
    {
        var reported = new List<Exception>();
        var previous = AsyncRelayCommand.DefaultErrorHandler;
        AsyncRelayCommand.DefaultErrorHandler = reported.Add;
        try
        {
            var command = new AsyncRelayCommand(_ => throw new InvalidOperationException("kaboom"));

            await command.ExecuteAsync();
            command.Execute(null); // the ICommand entry point used by buttons, which cannot be awaited
            await Task.Delay(50);

            Assert.IsType<InvalidOperationException>(command.LastException);
            Assert.Equal(2, reported.Count);
            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute(null));
        }
        finally
        {
            AsyncRelayCommand.DefaultErrorHandler = previous;
        }
    }

    [Fact]
    public async Task AsyncRelayCommand_Cancellation_IsNotAnError()
    {
        var command = new AsyncRelayCommand(async ct => await Task.Delay(Timeout.Infinite, ct));

        var running = command.ExecuteAsync();
        command.Cancel();
        await running;

        Assert.Null(command.LastException);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task AsyncRelayCommand_RespectsCanExecuteDelegate()
    {
        var allowed = false;
        var runs = 0;
        var command = new AsyncRelayCommand(_ =>
        {
            runs++;
            return Task.CompletedTask;
        }, () => allowed);

        await command.ExecuteAsync();
        allowed = true;
        await command.ExecuteAsync();

        Assert.Equal(1, runs);
    }
}
