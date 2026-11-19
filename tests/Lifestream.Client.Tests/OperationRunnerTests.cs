using System.Net;
using System.Text;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Tests.Features;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// SHELL-07: the waking state and Cancel of every kind of operation (save, dialog submit, detail pane, picker, load), and that Cancel reaches the
/// HTTP request. Tests named RealClient use the real API client, the wake-aware handler and a stub server on a manual clock; the others use the fake API.
/// </summary>
[Collection("GlobalErrorHandler")]
public sealed class OperationRunnerTests
{
    /// <summary>A stub server that holds every request until it is opened, and records which requests it answered and which were cancelled before.</summary>
    private sealed class GatedServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private int _arrived;
        private int _cancelled;

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Answered { get; } = [];

        /// <summary>Requests that reached the stub.</summary>
        public int Arrived => Volatile.Read(ref _arrived);

        /// <summary>Requests whose cancellation token fired while they waited at the stub: Cancel reached HttpClient.</summary>
        public int Cancelled => Volatile.Read(ref _cancelled);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _arrived);
            try
            {
                await Open.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }

            lock (Answered)
            {
                Answered.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            }

            return answer(request);
        }
    }

    private sealed class RealClient : IDisposable
    {
        private readonly Rig _rig = new();
        private readonly HttpClient _http;
        private readonly LifestreamApiClient _inner;

        public RealClient(Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            Server = new GatedServer(answer);
            var policy = new WakePolicy { Clock = Clock };
            _http = new HttpClient(new WakeAwareHandler(policy, Status, Server)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
            _rig.Session.Establish(FakeApiClient.Tokens());
            _inner = new LifestreamApiClient(_http, _rig.Session);
            Api = ScopedApiClient.Wrap(_inner, policy, strict: true);
        }

        public ManualTimeProvider Clock { get; } = new();

        public ServerStatus Status { get; } = new();

        public GatedServer Server { get; }

        public ILifestreamApiClient Api { get; }

        public void Dispose()
        {
            _inner.Dispose();
            _http.Dispose();
            _rig.Dispose();
        }
    }

    private static async Task Pump(ManualTimeProvider clock, TimeSpan by)
    {
        await Task.Delay(100);
        clock.Advance(by);
        await Task.Delay(100);
    }

    private static HttpResponseMessage NoContent(HttpRequestMessage _) => new(HttpStatusCode.NoContent);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---- a save from an editor -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealClient_EditorSave_OnAWakingServer_ShowsWakingAfter3s_AndCancelStopsTheRequest_NeverReachingTheServer()
    {
        using var stack = new RealClient(_ => Json("{}"));
        var toasts = new ToastService(new InlineUiDispatcher(), stack.Clock);
        var form = DonorEditorViewModel.ForNew(stack.Api, toasts, new FakeDialogs(), stack.Clock);
        form.Attach(stack.Status, stack.Clock);
        form.Name = "Maria Lopez";
        form.DateOfBirth = DateOnly.FromDateTime(stack.Clock.GetLocalNow().DateTime).AddYears(-30);
        form.Gender = Gender.Female;
        form.BloodType = BloodType.APos;
        form.Frequency = DonorFrequency.Occasional;

        var save = form.SaveCommand.ExecuteAsync();
        await Pump(stack.Clock, TimeSpan.FromSeconds(1));
        Assert.False(form.IsOperationWaking); // not yet: under 3 s
        await Pump(stack.Clock, TimeSpan.FromSeconds(3));

        Assert.True(form.IsOperationWaking); // after 3 s: the editor shows "Waking up the server"
        Assert.Equal(ServerState.Waking, stack.Status.State);
        Assert.Equal(1, stack.Server.Arrived);

        await form.CancelCommand.ExecuteAsync(); // Cancel while the save is waiting on the server
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        stack.Server.Open.SetResult();
        await Task.Delay(200);

        Assert.Equal(1, stack.Server.Cancelled); // the token reached HttpClient and the stub saw the request cancelled
        Assert.Empty(stack.Server.Answered); // the request never reached the server's handler logic
        Assert.False(form.IsOperationWaking);
        Assert.Contains("cancelled", form.FormError, StringComparison.OrdinalIgnoreCase);
        Assert.False(form.SaveCommand.IsRunning);
        Assert.Equal(ServerState.Online, stack.Status.State);
        Assert.True(form.HasUnsavedChanges); // nothing typed was lost, so Save can be pressed again
    }

    // ---- a dialog's submit -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealClient_DialogSubmit_OnAWakingServer_CancelStopsTheRequest_TheDialogStaysOpen_AndNothingIsChanged()
    {
        using var stack = new RealClient(NoContent);
        using var dialog = new ChangePasswordDialogViewModel(stack.Api);
        dialog.Bind(stack.Status, stack.Clock, new FakeDialogs());
        var closed = 0;
        dialog.CloseRequested += (_, _) => closed++;
        dialog.CurrentPassword = "old-password-123";
        dialog.NewPassword = "new-password-456";
        dialog.ConfirmPassword = "new-password-456";

        var submit = dialog.PrimaryCommand.ExecuteAsync();
        await Pump(stack.Clock, TimeSpan.FromSeconds(3.5));
        Assert.True(dialog.IsOperationWaking);

        await dialog.CancelCommand.ExecuteAsync(); // Esc / Cancel / X while the request is in flight: cancels the request
        await submit.WaitAsync(TimeSpan.FromSeconds(5));
        stack.Server.Open.SetResult();
        await Task.Delay(200);

        Assert.Equal(1, stack.Server.Cancelled);
        Assert.Empty(stack.Server.Answered); // the password is not changed after the user cancelled
        Assert.False(dialog.Completed);
        Assert.Equal(0, closed); // the typed values are still there to retry or to discard
        Assert.Contains("cancelled", dialog.FormError, StringComparison.OrdinalIgnoreCase);
        Assert.False(dialog.IsOperationWaking);
    }

    [Fact]
    public async Task RealClient_DialogSubmit_AfterCancel_CanBeRetried()
    {
        using var stack = new RealClient(NoContent);
        using var dialog = new ChangePasswordDialogViewModel(stack.Api);
        dialog.Bind(stack.Status, stack.Clock, new FakeDialogs());
        dialog.CurrentPassword = "old-password-123";
        dialog.NewPassword = "new-password-456";
        dialog.ConfirmPassword = "new-password-456";
        var submit = dialog.PrimaryCommand.ExecuteAsync();
        await Pump(stack.Clock, TimeSpan.FromSeconds(3.5));
        await dialog.CancelCommand.ExecuteAsync();
        await submit.WaitAsync(TimeSpan.FromSeconds(5));

        stack.Server.Open.SetResult(); // the server is awake now
        await dialog.PrimaryCommand.ExecuteAsync();

        Assert.True(dialog.Completed);
        Assert.Equal(["PUT /api/v1/me/password"], stack.Server.Answered);
    }

    // ---- a detail pane ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetailPane_OnAWakingServer_ShowsWaking_AndCancelStopsTheRequest_OfferingRetry()
    {
        var rig = new FeatureRig();
        var status = new ServerStatus();
        var never = new TaskCompletionSource<object>();
        var observed = new List<CancellationToken>();
        rig.Fake.Handlers["GetDonorAsync"] = ct =>
        {
            observed.Add(ct);
            return never.Task.WaitAsync(ct);
        };
        rig.DonorsReturn(_ => Samples.DonorPage(1));
        var vm = rig.Donors();
        vm.Attach(status, rig.Clock);
        await vm.LoadAsync();

        vm.SelectedItem = vm.Items[0];
        await Task.Delay(100);
        Assert.Equal(LoadState.Loading, vm.DetailState);
        status.Enter(); // another call has been waiting 3 s: the server is waking
        rig.Clock.Advance(TimeSpan.FromSeconds(3.5));
        await Task.Delay(100);

        Assert.Equal(LoadState.Waking, vm.DetailState); // the pane shows the waking state, with Cancel
        Assert.True(vm.IsDetailWaking);

        await vm.CancelDetailCommand.ExecuteAsync();
        await vm.PendingDetail.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(observed.Single().IsCancellationRequested); // the cancellation token the API call got was cancelled
        Assert.Equal(LoadState.Error, vm.DetailState);
        Assert.Equal("The request was cancelled.", vm.DetailError);
        Assert.True(vm.RetryDetailCommand.CanExecute(null)); // and Retry is offered
    }

    // ---- a record picker -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordPicker_OnAWakingServer_ShowsWaking_AndCancelStopsTheSearch()
    {
        var api = new FakeApiClient();
        var observed = new List<CancellationToken>();
        var never = new TaskCompletionSource<ApiResult<IReadOnlyList<Contracts.Lookups.RecordOption>>>();
        api.On<IReadOnlyList<Contracts.Lookups.RecordOption>>("SearchRecordsAsync", ct =>
        {
            observed.Add(ct);
            return never.Task.WaitAsync(ct);
        });
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        using var picker = new Lookups.RecordPickerViewModel(ScopedApiClient.Wrap(api, strict: true), RecordKind.Donor, clock, serverStatus: status);
        picker.SearchText = "ma";
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Delay(100);
        status.Enter();
        clock.Advance(TimeSpan.FromSeconds(3.5));
        await Task.Delay(100);

        Assert.True(picker.IsWaking);

        await picker.CancelSearchCommand.ExecuteAsync();
        await Task.Delay(200);

        Assert.True(observed.Single().IsCancellationRequested);
        Assert.False(picker.IsSearching);
        Assert.Contains("cancelled", picker.SearchError, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the budget ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealClient_AnOperationOnAServerThatStaysWaking_UsesThe120SecondBudget_ThenReportsTheWakingProblem()
    {
        var clock = new ManualTimeProvider();
        var status = new ServerStatus();
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Problem(HttpStatusCode.ServiceUnavailable, Contracts.ProblemCodes.DatabaseWaking));
        using var rig = new Rig();
        rig.Session.Establish(FakeApiClient.Tokens());
        var policy = new WakePolicy { Clock = clock };
        using var http = new HttpClient(new WakeAwareHandler(policy, status, handler)) { BaseAddress = new Uri("https://api.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var api = ScopedApiClient.Wrap(inner, policy, strict: true);
        using var runner = new OperationRunner(() => status, () => clock);

        var task = runner.RunAsync(ct => api.GetDonorAsync(7, ct));
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsOk);
        Assert.True(result.Problem!.IsWaking, result.Problem.Code);
        Assert.InRange(clock.Elapsed.TotalSeconds, 100, 125); // the 120 s budget, not the 10 s a screen load is cut at
        Assert.Equal(120, new WakePolicy().Budget.TotalSeconds); // the product value the app is wired with
    }

    // ---- sign-in ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealClient_SignIn_OnAWakingServer_ShowsWaking_AndCancelStopsTheRequest()
    {
        using var stack = new RealClient(_ => Json("{}"));
        using var rig = new Rig();
        var login = new Auth.LoginViewModel(stack.Api, rig.Session, rig.Get<Navigation.IAppFlow>(), stack.Status) { Username = "u", Password = "p" };

        var attempt = login.SignInCommand.ExecuteAsync();
        await Pump(stack.Clock, TimeSpan.FromSeconds(3.5));
        Assert.True(login.IsServerWaking);
        Assert.True(login.CanCancel);

        await login.CancelCommand.ExecuteAsync();
        await attempt.WaitAsync(TimeSpan.FromSeconds(5));
        stack.Server.Open.SetResult();
        await Task.Delay(200);

        Assert.Equal(1, stack.Server.Cancelled);
        Assert.Empty(stack.Server.Answered);
        Assert.Equal("Sign-in cancelled.", login.Message);
        Assert.False(rig.Session.IsAuthenticated);
    }

    // ---- a screen load: Cancel, Retry, Cancel, Retry, then the server answers (the verifier's probe, ported) --------------------

    private sealed class ReadyzViewModel(ILifestreamApiClient api) : ViewModelBase
    {
        public AsyncRelayCommand RetryCommand => field ??= Command(ct => LoadAsync(ct));

        protected override async Task FetchAsync(CancellationToken ct) => (await api.GetReadinessAsync(ct).ConfigureAwait(false)).Unwrap();
    }

    [Fact]
    public async Task RealClient_ScreenLoad_CancelRetryCancelRetry_ThenTheServerAnswers_IsReady()
    {
        using var stack = new RealClient(_ => Json("{\"status\":\"ready\"}"));
        using var vm = new ReadyzViewModel(stack.Api);
        vm.Attach(stack.Status, stack.Clock);

        var load = vm.LoadAsync();
        await Pump(stack.Clock, TimeSpan.FromSeconds(3.5));
        Assert.Equal(LoadState.Waking, vm.State);
        vm.CancelLoadCommand.Execute(null);
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LoadState.Error, vm.State);
        Assert.True(vm.RetryCommand.CanExecute(null));

        vm.RetryCommand.Execute(null);
        await Pump(stack.Clock, TimeSpan.FromSeconds(3.5));
        Assert.Equal(LoadState.Waking, vm.State);
        vm.CancelLoadCommand.Execute(null);
        await Task.Delay(300);
        Assert.Equal(LoadState.Error, vm.State);

        vm.RetryCommand.Execute(null);
        await Pump(stack.Clock, TimeSpan.FromSeconds(0.5));
        stack.Server.Open.SetResult();
        await Task.Delay(500);

        Assert.Equal(LoadState.Ready, vm.State);
        Assert.Equal(2, stack.Server.Cancelled); // both cancels reached the HTTP request
    }

    // ---- sign-in with an unusable 200 (SHELL-02) -------------------------------------------------------------------------------

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("<html>captive portal</html>")]
    [InlineData("{\"accessToken\":\"a\",\"refreshToken\":\"r\",\"accessTokenExpiresUtc\":\"2030-01-01T00:00:00Z\",\"user\":null}")]
    [InlineData("{\"accessToken\":null,\"refreshToken\":null,\"accessTokenExpiresUtc\":\"2030-01-01T00:00:00Z\",\"user\":{\"staffId\":1,\"displayName\":\"X\",\"role\":\"Staff\",\"isDemo\":false,\"canManageStaff\":false}}")]
    [InlineData("{\"accessToken\":\"a\",\"refreshToken\":\"\",\"accessTokenExpiresUtc\":\"2030-01-01T00:00:00Z\",\"user\":{\"staffId\":1,\"displayName\":\"X\",\"role\":\"Staff\",\"isDemo\":false,\"canManageStaff\":false}}")]
    [InlineData("{\"accessToken\":\"a\",\"refreshToken\":\"r\",\"accessTokenExpiresUtc\":\"2030-01-01T00:00:00Z\",\"user\":{\"staffId\":1,\"displayName\":null,\"role\":\"Staff\",\"isDemo\":false,\"canManageStaff\":false}}")]
    public async Task RealClient_SignInAnsweredWith200ButAnUnusableBody_ShowsTheInlineError_AndNeverOpensTheMainWindow(string body)
    {
        using var rig = new Rig();
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy { Budget = TimeSpan.Zero, Clock = new ManualTimeProvider() }, new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        using var inner = new LifestreamApiClient(http, rig.Session);
        var login = new Auth.LoginViewModel(ScopedApiClient.Wrap(inner, strict: true), rig.Session, rig.Get<Navigation.IAppFlow>()) { Username = "u", Password = "p" };
        var reported = new List<Exception>();
        var previous = AsyncRelayCommand.DefaultErrorHandler;
        AsyncRelayCommand.DefaultErrorHandler = reported.Add;
        try
        {
            await login.SignInCommand.ExecuteAsync();
        }
        finally
        {
            AsyncRelayCommand.DefaultErrorHandler = previous;
        }

        Assert.Empty(reported);
        Assert.NotEqual(Auth.LoginProblem.None, login.Problem);
        Assert.False(string.IsNullOrEmpty(login.Message)); // the inline message
        Assert.True(login.SignInCommand.CanExecute(null)); // the button is enabled again
        Assert.False(rig.Session.IsAuthenticated);
        Assert.DoesNotContain("ShowMain", rig.Windows.Events); // the main window never opened
    }
}
