using System.Reflection;
using System.Windows.Input;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Shell;
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
        Assert.Equal("Can't reach the server. Check your internet connection, then retry.", vm.ErrorMessage);
        Assert.Contains("corr-9", vm.ErrorDetail);
        Assert.DoesNotContain("socket", vm.ErrorMessage!);
    }

    [Fact]
    public async Task ViewModelBase_UnexpectedException_BecomesErrorState_NotACrash()
    {
        var vm = new TestViewModel { Fetch = _ => throw new InvalidOperationException("secret internal detail") };
        var seen = new List<Exception>();
        using (AsyncRelayCommand.CaptureErrors(seen.Add))
        {
            await vm.LoadAsync();
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

        // The load's call is out and the server never answers it (the limit times the wait for the server: a load that makes no call has none).
        var fake = new FakeApiClient();
        fake.On<Lifestream.Contracts.Donors.DonorDetail>("GetDonorAsync", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var api = ScopedApiClient.Wrap(fake, strict: true);
        vm.Fetch = async ct => await api.GetDonorAsync(1, ct);

        var load = vm.LoadAsync();
        for (var i = 0; i < 2500 && fake.CountOf("GetDonorAsync") == 0; i++)
        {
            await Task.Delay(2); // the request is on its way before the clock moves
        }

        clock.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(LoadState.Loading, vm.State);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LoadState.Error, vm.State);
        Assert.Equal("The server is taking too long to answer. Check your connection, then retry.", vm.ErrorMessage);

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
    public void EveryTypeWithChangeNotification_ThatExposesAsyncWork_ExposesBindableIsLoadingAndIsBusy()
    {
        // SHELL-11: not only ViewModelBase subclasses. Every non-command type in the Client assembly that implements
        // INotifyPropertyChanged and offers async work (a Task-returning method or an AsyncRelayCommand) must have both.
        var observable = typeof(ClientAssembly).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(System.ComponentModel.INotifyPropertyChanged).IsAssignableFrom(t) && !typeof(ICommand).IsAssignableFrom(t))
            .Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .ToList();
        Assert.Contains(typeof(Lifestream.Client.Lookups.RecordPickerViewModel), observable);
        Assert.Contains(typeof(ViewModelBase), typeof(ViewModelBase).Assembly.GetTypes().Where(t => t.IsAbstract));

        var missing = new List<string>();
        var checkedTypes = 0;
        foreach (var type in observable.Where(ExposesAsyncWork))
        {
            checkedTypes++;
            foreach (var name in new[] { nameof(ViewModelBase.IsLoading), nameof(ViewModelBase.IsBusy) })
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property is null || property.PropertyType != typeof(bool))
                {
                    missing.Add($"{type.Name}.{name}");
                }
            }
        }

        Assert.True(checkedTypes >= 5, $"only {checkedTypes} types were checked");
        Assert.Empty(missing);
    }

    private static bool ExposesAsyncWork(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => typeof(Task).IsAssignableFrom(m.ReturnType) || m.ReturnType == typeof(ValueTask))
        || typeof(ViewModelBase).IsAssignableFrom(type)
        || type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.PropertyType == typeof(AsyncRelayCommand));

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
    public void EveryViewModelMember_ThatIsPublicAndWritable_RaisesPropertyChanged_ForEveryPropertyType()
    {
        // SHELL-12: every public writable property of every type with change notification (any property type, none skipped)
        // must announce its change. A property whose type cannot be given a sample value is a failure, not a skip.
        using var rig = new Rig();
        var failures = new List<string>();
        var tested = 0;
        var types = typeof(ClientAssembly).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(System.ComponentModel.INotifyPropertyChanged).IsAssignableFrom(t) && !typeof(ICommand).IsAssignableFrom(t))
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.SetMethod is { IsPublic: true }));
        foreach (var type in types)
        {
            var instance = Create(rig, type);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && !IsInitOnly(p)))
            {
                var current = property.GetValue(instance);
                var value = Sample(property.PropertyType, current);
                if (value is null || Equals(value, current))
                {
                    failures.Add($"{type.Name}.{property.Name}: no sample value of type {property.PropertyType.Name} that differs from the current one");
                    continue;
                }

                tested++;
                // The handler also runs on the thread of a background load that raises other properties: it only ever writes true,
                // because `raised |= name == x` is a read-modify-write that another thread's "false" could overwrite.
                var raised = false;
                void Handler(object? s, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == property.Name)
                    {
                        Volatile.Write(ref raised, true);
                    }
                }

                instance.PropertyChanged += Handler;

                // A screen may still be finishing its first load on another thread and change the property between the sample and the set;
                // re-read right before setting and sample again, so the set is always a real change of value.
                var latest = property.GetValue(instance);
                if (Equals(value, latest))
                {
                    value = Sample(property.PropertyType, latest);
                }

                property.SetValue(instance, value);
                instance.PropertyChanged -= Handler;
                if (!Volatile.Read(ref raised))
                {
                    failures.Add($"{type.Name}.{property.Name} ({property.PropertyType.Name}) did not raise PropertyChanged");
                }
            }
        }

        Assert.True(tested >= 10, $"only {tested} properties were tested");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static System.ComponentModel.INotifyPropertyChanged Create(Rig rig, Type type)
    {
        if (type == typeof(ToastItem))
        {
            return rig.Get<IToastService>().Show(ToastKind.Info, "message");
        }

        var registered = rig.Provider.GetService(type);
        if (registered is not null)
        {
            return (System.ComponentModel.INotifyPropertyChanged)registered;
        }

        // Not registered (editors are built by their list screen, with a private constructor): build it from the constructor that
        // has the most parameters, taking each from the container, a sample, or null for an optional/nullable one.
        var pool = new object[] { RecordKind.Donor, NavigationCatalog.Default[0] };
        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters()
            .Select(p => rig.Provider.GetService(p.ParameterType) ?? pool.FirstOrDefault(o => p.ParameterType.IsInstanceOfType(o)) ?? (p.ParameterType == typeof(TimeProvider) ? TimeProvider.System : null))
            .ToArray();
        return (System.ComponentModel.INotifyPropertyChanged)constructor.Invoke(arguments);
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));

    private static object? Sample(Type type, object? current)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string))
        {
            return current as string == "x" ? "y" : "x";
        }

        if (underlying == typeof(bool))
        {
            return !(current as bool? ?? false);
        }

        if (underlying.IsEnum)
        {
            return Enum.GetValues(underlying).Cast<object>().FirstOrDefault(v => !Equals(v, current));
        }

        if (underlying == typeof(TimeSpan))
        {
            return TimeSpan.FromSeconds(((current as TimeSpan?) ?? TimeSpan.Zero).TotalSeconds + 7);
        }

        if (underlying == typeof(DateOnly))
        {
            return new DateOnly(2022, 3, 5);
        }

        if (underlying == typeof(Version))
        {
            return new Version(9, 9, 9);
        }

        if (underlying == typeof(AsyncRelayCommand))
        {
            return new AsyncRelayCommand(_ => Task.CompletedTask);
        }

        if (underlying.IsPrimitive)
        {
            return Convert.ChangeType(Convert.ToDouble(current ?? 0, System.Globalization.CultureInfo.InvariantCulture) + 5, underlying, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (underlying == typeof(Lifestream.Contracts.Lookups.RecordOption))
        {
            return new Lifestream.Contracts.Lookups.RecordOption(77, "Sample", null);
        }

        // Collections: a list holding one sample element.
        var element = underlying.IsGenericType && underlying.GetGenericTypeDefinition() is var d && (d == typeof(IReadOnlyList<>) || d == typeof(IEnumerable<>) || d == typeof(IList<>) || d == typeof(List<>) || d == typeof(IReadOnlyCollection<>))
            ? underlying.GetGenericArguments()[0]
            : null;
        if (element is not null)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            list.Add(Sample(element, null) ?? (element.IsValueType ? Activator.CreateInstance(element) : null));
            return list;
        }

        // Any other type: an instance built from its widest constructor (an abstract type: from the first concrete subclass in the assembly).
        var concrete = underlying.IsAbstract || underlying.IsInterface
            ? typeof(ClientAssembly).Assembly.GetTypes().FirstOrDefault(t => !t.IsAbstract && underlying.IsAssignableFrom(t))
            : underlying;
        return concrete is null ? null : Build(concrete);
    }

    private static object Build(Type type)
    {
        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor is null)
        {
            return Activator.CreateInstance(type)!;
        }

        var arguments = constructor.GetParameters().Select(p => p.ParameterType == typeof(string) ? "x" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        try
        {
            return constructor.Invoke(arguments);
        }
        catch (TargetInvocationException)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        }
    }

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
        using (AsyncRelayCommand.CaptureErrors(reported.Add))
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
    }

    [Fact]
    public void CancelLoadAndDispose_WhenTheLoadJustFinishedAndDisposedItsSource_DoNotThrow()
    {
        // A load's last step disposes its cancellation source; a cancel or a dispose that read the field a moment earlier must find that harmless
        // (seen as "The CancellationTokenSource has been disposed" when a screen was torn down while its first load was finishing).
        var vm = new TestViewModel();
        var finished = new CancellationTokenSource();
        typeof(ViewModelBase).GetField("_loadCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, finished);
        finished.Dispose();

        vm.CancelLoad();
        vm.Dispose();
    }

    [Fact]
    public async Task CaptureErrors_ReceivesOnlyTheErrorsOfItsOwnFlow_NotThoseOfACommandRunningElsewhere()
    {
        // Tests that run in parallel must not see each other's command errors: a failing command started before the capture, on another flow,
        // reports to the process-wide handler while the capture is open, and never to the capture.
        var mine = new List<Exception>();
        var handlerBefore = AsyncRelayCommand.DefaultErrorHandler;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = new AsyncRelayCommand(async _ =>
        {
            await gate.Task;
            throw new InvalidOperationException("another test's failure");
        });
        var otherRun = Task.Run(() => other.ExecuteAsync());

        using (AsyncRelayCommand.CaptureErrors(mine.Add))
        {
            var own = new AsyncRelayCommand(_ => throw new InvalidOperationException("this test's failure"));
            await own.ExecuteAsync();
            gate.SetResult();
            await otherRun;
            Assert.Same(handlerBefore, AsyncRelayCommand.DefaultErrorHandler); // the capture did not swap the shared handler
        }

        Assert.Equal("this test's failure", Assert.Single(mine).Message);
        Assert.IsType<InvalidOperationException>(other.LastException);
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
