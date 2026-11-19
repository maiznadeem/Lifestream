using System.Globalization;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Lookups;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Validation;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Client.Tests;

public sealed class ComponentModelTests
{
    // Formatting (SHELL-36)

    [Fact]
    public void Formatting_Date_Is05Mar2022()
    {
        Assert.Equal("05 Mar 2022", Formatting.Date(new DateOnly(2022, 3, 5)));
        Assert.Equal("", Formatting.Date((DateOnly?)null));
        Assert.Equal("never", Formatting.Date((DateOnly?)null, "never"));
    }

    [Fact]
    public void Formatting_DateTime_UsesTheGivenZone_AndTheSameFormatEverywhere()
    {
        var utc = new DateTime(2022, 3, 5, 14, 30, 0, DateTimeKind.Utc);
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5");

        Assert.Equal("05 Mar 2022 19:30", Formatting.DateTime(utc, zone));
        Assert.Equal("05 Mar 2022 14:30", Formatting.DateTime(utc, TimeZoneInfo.Utc));
        Assert.Equal("dd MMM yyyy", Formatting.DateFormat);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("fr-FR")]
    public void Formatting_IsCultureIndependent(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Assert.Equal("05 Mar 2022", Formatting.Date(new DateOnly(2022, 3, 5)));
            Assert.Equal("05 Mar 2022 14:30", Formatting.DateTime(new DateTime(2022, 3, 5, 14, 30, 0, DateTimeKind.Utc), TimeZoneInfo.Utc));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Formatting_ParsesWhatItPrints()
    {
        Assert.True(Formatting.TryParseDate("05 Mar 2022", out var date));
        Assert.Equal(new DateOnly(2022, 3, 5), date);
        Assert.False(Formatting.TryParseDate("03/05/2022", out _)); // the ambiguous format is not accepted
    }

    // Toast (SHELL-30)

    [Fact]
    public void Toast_NamesTheRecord_AutoDismissesBetween6And10Seconds()
    {
        var clock = new ManualTimeProvider();
        var toasts = new ToastService(new InlineUiDispatcher(), clock);

        var toast = toasts.ShowForRecord(ToastKind.Success, "Donor", "Ayşe Öztürk", "saved");
        Assert.Equal("Donor 'Ayşe Öztürk' saved.", toast.Message);
        Assert.Single(toasts.Toasts);

        Assert.InRange(ToastService.Lifetime.TotalSeconds, 6, 10);
        clock.Advance(TimeSpan.FromSeconds(5.9));
        Assert.Single(toasts.Toasts);
        clock.Advance(ToastService.Lifetime);
        Assert.Empty(toasts.Toasts);
        Assert.True(toast.IsDismissed);
    }

    [Fact]
    public void Toast_WithAnUndoAction_StaysTenSeconds_AndTheLifetimeIsPerToastWithinSixToTen()
    {
        var clock = new ManualTimeProvider();
        var toasts = new ToastService(new InlineUiDispatcher(), clock);

        var undo = toasts.ShowForRecord(ToastKind.Success, "Donor", "Ayşe", "deleted", "Undo", () => Task.CompletedTask);
        var plain = toasts.Show(ToastKind.Info, "Plain");
        var short1 = toasts.Show(ToastKind.Info, "Too short", lifetime: TimeSpan.FromSeconds(2));
        var long1 = toasts.Show(ToastKind.Info, "Too long", lifetime: TimeSpan.FromSeconds(30));
        var seven = toasts.Show(ToastKind.Info, "Seven", lifetime: TimeSpan.FromSeconds(7));

        clock.Advance(TimeSpan.FromSeconds(5.9));
        Assert.Equal(5, toasts.Toasts.Count); // nothing dismissed before 6 s, whatever was asked for
        clock.Advance(TimeSpan.FromSeconds(0.2)); // 6.1 s: the 2 s request was raised to 6
        Assert.True(short1.IsDismissed);
        Assert.False(seven.IsDismissed);
        clock.Advance(TimeSpan.FromSeconds(1)); // 7.1 s
        Assert.True(seven.IsDismissed);
        Assert.False(plain.IsDismissed);
        clock.Advance(TimeSpan.FromSeconds(1)); // 8.1 s
        Assert.True(plain.IsDismissed);
        Assert.False(undo.IsDismissed, "a toast with Undo must outlive the 8 s default");
        clock.Advance(TimeSpan.FromSeconds(1.8)); // 9.9 s
        Assert.False(undo.IsDismissed);
        Assert.False(long1.IsDismissed);
        clock.Advance(TimeSpan.FromSeconds(0.2)); // 10.1 s: the 30 s request was cut to 10
        Assert.True(undo.IsDismissed);
        Assert.True(long1.IsDismissed);
    }

    [Fact]
    public void Toast_PausesOnHoverOrFocus_AndResumesWithTheRemainingTime()
    {
        var clock = new ManualTimeProvider();
        var toasts = new ToastService(new InlineUiDispatcher(), clock);
        var toast = toasts.Show(ToastKind.Info, "Hello");

        clock.Advance(TimeSpan.FromSeconds(5));
        toast.Pause();
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Single(toasts.Toasts); // paused: still there a minute later
        Assert.True(toast.IsPaused);

        toast.Resume();
        clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Single(toasts.Toasts); // 3 s of the 8 s were left
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Empty(toasts.Toasts);
    }

    [Fact]
    public async Task Toast_ActionRunsOnce_AndDismisses()
    {
        var clock = new ManualTimeProvider();
        var toasts = new ToastService(new InlineUiDispatcher(), clock);
        var undone = 0;
        var toast = toasts.Show(ToastKind.Success, "Donor 'X' deleted.", "Undo", () =>
        {
            undone++;
            return Task.CompletedTask;
        });

        Assert.True(toast.HasAction);
        Assert.Equal("Undo", toast.ActionLabel);
        await toast.ActionCommand.ExecuteAsync();

        Assert.Equal(1, undone);
        Assert.Empty(toasts.Toasts);
    }

    [Fact]
    public void Toast_WithoutAnAction_HasNoActionLabel()
    {
        var toast = new ToastService(new InlineUiDispatcher(), new ManualTimeProvider()).Show(ToastKind.Info, "x", "View", null);

        Assert.False(toast.HasAction);
    }

    // Record picker (SHELL-37)

    private static RecordPickerViewModel Picker(FakeApiClient api, ManualTimeProvider clock) => new(api, RecordKind.Donor, clock);

    [Fact]
    public async Task RecordPicker_TypingSearches_AfterDebounce_ShowsSkeletonWhileSearching_ThenChipAfterSelection()
    {
        var api = new FakeApiClient();
        var gate = new TaskCompletionSource<ApiResult<IReadOnlyList<RecordOption>>>();
        api.On<IReadOnlyList<RecordOption>>("SearchRecordsAsync", _ => gate.Task);
        var clock = new ManualTimeProvider();
        using var picker = Picker(api, clock);

        picker.SearchText = "ay";
        picker.SearchText = "ayş";
        clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(0, api.CountOf("SearchRecordsAsync")); // debounced
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(1, api.CountOf("SearchRecordsAsync")); // one call for the burst of typing
        Assert.True(picker.IsSearching); // skeleton rows show

        gate.SetResult(ApiResult<IReadOnlyList<RecordOption>>.Ok([new RecordOption(1123, "Ayşe Öztürk", "A+ · 02 Jan 1990"), new RecordOption(9, "Ayşegül Can", "O-")]));
        await Task.Delay(50);
        Assert.False(picker.IsSearching);
        Assert.Equal(2, picker.Options.Count);
        Assert.False(picker.HasSelection);

        picker.Select(picker.Options[0]);

        Assert.Equal(1123, picker.SelectedId);
        Assert.True(picker.HasSelection);
        Assert.Equal("Ayşe Öztürk · #1123 · A+ · 02 Jan 1990", picker.SummaryText);
        Assert.False(picker.ShowSearch);

        await picker.ChangeCommand.ExecuteAsync();
        Assert.Null(picker.SelectedId);
        Assert.True(picker.ShowSearch);
    }

    [Fact]
    public async Task RecordPicker_NoMatches_AndFailure_AreReportedDistinctly()
    {
        var api = new FakeApiClient();
        api.On("SearchRecordsAsync", ApiResult<IReadOnlyList<RecordOption>>.Ok([]));
        using var picker = Picker(api, new ManualTimeProvider());
        picker.SearchText = "zzz";

        await picker.SearchNowAsync();
        Assert.True(picker.HasNoMatches);

        api.On("SearchRecordsAsync", FakeApiClient.Unreachable<IReadOnlyList<RecordOption>>());
        await picker.SearchNowAsync();
        Assert.False(picker.HasNoMatches);
        Assert.NotNull(picker.SearchError);
    }

    // Validation (SHELL-32)

    private sealed class SampleForm : FormViewModelBase
    {
        public SampleForm() => DeclareFields("Name", "Dob", "Phone");

        public string Name { get; set; } = string.Empty;

        public DateOnly? Dob { get; set; }

        public string Phone { get; set; } = string.Empty;

        public override bool HasUnsavedChanges => Name.Length > 0;

        protected override string? ValidateField(string field) => field switch
        {
            "Name" when string.IsNullOrWhiteSpace(Name) => "Enter the donor's name.",
            "Dob" when Dob is null => "Enter a date of birth.",
            "Dob" when Dob > DateOnly.FromDateTime(DateTime.UtcNow) => "Date of birth can't be in the future.",
            "Phone" when Phone.Length > 0 && Phone.Length < 7 => "Enter at least 7 digits.",
            _ => null,
        };
    }

    [Fact]
    public void Form_ValidateAll_ShowsPerFieldMessages_AndMovesFocusToTheFirstInvalidFieldInVisualOrder()
    {
        var form = new SampleForm { Phone = "123" };
        var focused = new List<string>();
        form.FocusRequested += (_, field) => focused.Add(field);

        var ok = form.ValidateAll();

        Assert.False(ok);
        Assert.Equal("Enter the donor's name.", form.GetError("Name"));
        Assert.Equal("Enter a date of birth.", form.GetError("Dob"));
        Assert.Equal("Enter at least 7 digits.", form.GetError("Phone"));
        Assert.Equal(["Name"], focused);
        Assert.Equal("3 fields need attention.", form.ErrorSummary);
        Assert.True(form.HasErrors);
    }

    [Fact]
    public void Form_FocusGoesToTheFirstInvalidField_EvenWhenEarlierFieldsAreValid()
    {
        var form = new SampleForm { Name = "Ayşe", Dob = new DateOnly(1990, 1, 2), Phone = "12" };
        var focused = new List<string>();
        form.FocusRequested += (_, field) => focused.Add(field);

        Assert.False(form.ValidateAll());

        Assert.Equal(["Phone"], focused);
    }

    [Fact]
    public void Form_ValidateOnBlur_ChecksOnlyThatField_AndClearsWhenFixed()
    {
        var form = new SampleForm();
        var changed = new List<string?>();
        form.ErrorsChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(form.ValidateOnBlur("Name"));
        Assert.Null(form.GetError("Dob")); // not touched yet
        form.Name = "Ayşe";
        Assert.True(form.ValidateOnBlur("Name"));

        Assert.Null(form.GetError("Name"));
        Assert.Equal(["Name", "Name"], changed);
        Assert.False(form.HasErrors);
        Assert.Null(form.ErrorSummary);
    }

    [Fact]
    public void Form_AllValid_RequestsNoFocus_AndTracksUnsavedChanges()
    {
        var form = new SampleForm { Name = "Ayşe", Dob = new DateOnly(1990, 1, 2) };
        var focused = 0;
        form.FocusRequested += (_, _) => focused++;

        Assert.True(form.ValidateAll());

        Assert.Equal(0, focused);
        Assert.True(form.HasUnsavedChanges);
    }
}
