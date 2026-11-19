using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Session;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

public static class FakeApiExtensions
{
    /// <summary>Scripts the answer of a call that has no body (an <see cref="ApiResult"/>).</summary>
    public static void OnVoid(this FakeApiClient fake, string name, ApiResult answer) => fake.Handlers[name] = _ => Task.FromResult<object>(answer);
}

/// <summary>A form-dialog host for tests: records the dialogs that were shown and lets the test act as the user inside them. Nothing is ever displayed.</summary>
public sealed class FakeFormDialogs : IFormDialogService
{
    public List<DialogViewModel> Shown { get; } = [];

    /// <summary>What the "user" does while the dialog is open, for example set the fields and run the primary command.</summary>
    public Func<DialogViewModel, Task>? Script { get; set; }

    public async Task<bool> ShowAsync(DialogViewModel viewModel)
    {
        Shown.Add(viewModel);
        if (Script is not null)
        {
            await Script(viewModel);
        }

        return viewModel.Completed;
    }
}

public sealed class FakeReceiptViewer : IReceiptViewer
{
    public List<(int Id, bool IsDuplicate)> Opened { get; } = [];

    public Task ShowAsync(int receiptId, bool isDuplicate)
    {
        Opened.Add((receiptId, isDuplicate));
        return Task.CompletedTask;
    }
}

/// <summary>A printer that only counts; no print dialog can open in a test.</summary>
public sealed class FakePrinter : IReceiptPrinter
{
    public List<ReceiptDocument> Printed { get; } = [];

    public List<ReceiptDocument> Saved { get; } = [];

    public PrintOutcome Outcome { get; set; } = PrintOutcome.Done;

    public Task<PrintOutcome> PrintAsync(ReceiptDocument document, CancellationToken ct = default)
    {
        Printed.Add(document);
        return Task.FromResult(Outcome);
    }

    public Task<PrintOutcome> SaveAsPdfAsync(ReceiptDocument document, CancellationToken ct = default)
    {
        Saved.Add(document);
        return Task.FromResult(Outcome);
    }
}

public sealed class FakePhotoPicker(byte[]? bytes) : IPhotoPicker
{
    public byte[]? Bytes { get; set; } = bytes;

    public int Picked { get; private set; }

    public Task<byte[]?> PickAsync()
    {
        Picked++;
        return Task.FromResult(Bytes);
    }
}

public sealed class FakePasswordPrompt : IChangePasswordPrompt
{
    public List<bool> Prompts { get; } = [];

    public Task<bool> PromptAsync(bool required = false)
    {
        Prompts.Add(required);
        return Task.FromResult(true);
    }
}

/// <summary>The part 2 view models wired to the fake API (recording arguments), a manual clock, fake dialogs, fake form dialogs, a fake printer and a real session.</summary>
public sealed class Part2Rig
{
    public Part2Rig(Role role = Role.Admin, bool demo = false, int staffId = 1)
    {
        Base = new FeatureRig();
        Forms = new FakeFormDialogs();
        Viewer = new FakeReceiptViewer();
        Printer = new FakePrinter();
        Navigation = new FakeNavigation();
        Session = new SessionService();
        Session.Establish(new Contracts.Auth.TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), new Contracts.Auth.SessionUser(staffId, "Test Admin", role, demo, role == Role.Admin)));
    }

    public FeatureRig Base { get; }

    public FakeApiClient Fake => Base.Fake;

    public ILifestreamApiClient Api => Base.Api;

    public RecordingApi Recorder => Base.Recorder;

    public ManualTimeProvider Clock => Base.Clock;

    public FakeDialogs Dialogs => Base.Dialogs;

    public ToastService Toasts => Base.Toasts;

    public FakeFormDialogs Forms { get; }

    public FakeReceiptViewer Viewer { get; }

    public FakePrinter Printer { get; }

    public FakeNavigation Navigation { get; }

    public SessionService Session { get; }

    public DateOnly Today => Base.Today;

    public AppointmentsViewModel Appointments()
    {
        var vm = new AppointmentsViewModel(Api, Base.States, Toasts, Dialogs, Forms, Viewer, Navigation, Base.Ui) { Zone = TimeZoneInfo.Utc };
        vm.Attach(null, Clock);
        return vm;
    }

    public RequestsViewModel Requests()
    {
        var vm = new RequestsViewModel(Api, Base.States, Toasts, Dialogs, Forms, Viewer, Navigation, Base.Ui);
        vm.Attach(null, Clock);
        return vm;
    }

    public ReceiptsViewModel Receipts()
    {
        var vm = new ReceiptsViewModel(Api, Base.States, Toasts, Dialogs, Viewer, Navigation, Base.Ui) { Zone = TimeZoneInfo.Utc };
        vm.Attach(null, Clock);
        return vm;
    }

    public StaffViewModel Staff()
    {
        var vm = new StaffViewModel(Api, Base.States, Toasts, Dialogs, Forms, Session, Base.Ui);
        vm.Attach(null, Clock);
        return vm;
    }

    public ProfileViewModel Profile(IPhotoPicker? picker = null, IChangePasswordPrompt? prompt = null)
    {
        var vm = new ProfileViewModel(Api, Toasts, picker ?? new FakePhotoPicker(null), prompt ?? new FakePasswordPrompt());
        vm.Attach(null, Clock);
        return vm;
    }

    public AppointmentEditorViewModel BookingForm(Contracts.Lookups.RecordOption? donor = null, TimeZoneInfo? zone = null) =>
        AppointmentEditorViewModel.ForNew(Api, Toasts, Dialogs, Clock, zone ?? TimeZoneInfo.Utc, donor);

    public AppointmentEditorViewModel RescheduleForm(AppointmentDetail appointment, TimeZoneInfo? zone = null) =>
        AppointmentEditorViewModel.ForReschedule(Api, Toasts, Dialogs, appointment, Clock, zone ?? TimeZoneInfo.Utc);
}

/// <summary>Sample records for the part 2 tests. Dates are far enough ahead that the manual clock's "now" never makes a slot past.</summary>
public static class Samples2
{
    public static readonly DateTime SlotUtc = new(2027, 3, 10, 10, 30, 0, DateTimeKind.Utc);

    public static AppointmentListItem AppointmentRow(int id, int donorId, string? donorName = null, AppointmentStatus status = AppointmentStatus.Pending) =>
        new(id, donorId, donorName ?? $"Donor {donorId:D3}", BloodType.OPos, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), SlotUtc.AddDays(id), status);

    public static AppointmentDetail Appointment(int id = 5, int donorId = 42, string donorName = "Ayşe Nur Öztürk-Abdulrahman", AppointmentStatus status = AppointmentStatus.Pending, string? overrideReason = null, int? receiptId = null) =>
        new(id, donorId, donorName, BloodType.OPos, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), SlotUtc, status, overrideReason, receiptId, "rv-a1");

    public static PagedResult<AppointmentListItem> AppointmentPage(params AppointmentListItem[] rows) => new(rows, rows.Length, 1, 50);

    public static IReadOnlyList<SlotInfo> Slots(DateOnly day, params (int Hour, int Minute, bool Taken)[] slots) =>
        [.. slots.Select(s => new SlotInfo(DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(s.Hour, s.Minute)), DateTimeKind.Utc), s.Taken))];

    public static IReadOnlyList<SlotInfo> DaySlots(DateOnly day) => Slots(day, (9, 0, false), (9, 30, true), (10, 0, false), (10, 30, false), (12, 0, false), (13, 0, false));

    /// <summary>A donor born in 1990 whose last donation was 45 days before the fake "today" (2026-01-01): ineligible for another 45 days.</summary>
    public static DonorDetail RecentDonor(int id = 42) =>
        new(id, "Ayşe Nur Öztürk-Abdulrahman", new DateOnly(1990, 5, 17), Gender.Female, BloodType.OPos, "+1-555-0101", "12 Mill Road", DonorFrequency.Regular, new DateOnly(2025, 11, 17), "rv-d1");

    public static DonorDetail RestedDonor(int id = 43) => RecentDonor(id) with { Name = "Omar Haddad", LastDonated = new DateOnly(2025, 1, 10) };

    public static DonorEligibility NotEligible() => new(false, ["At least 90 days are needed between donations; next eligible on 15 Feb 2026."], new DateOnly(2026, 2, 15));

    public static DonorEligibility Eligible() => new(true, [], null);

    public static ReceiptDetail Receipt(int id = 90, ReceiptKind kind = ReceiptKind.Donation) =>
        new(id, new DateTime(2026, 11, 19, 14, 30, 0, DateTimeKind.Utc), kind, BloodType.OPos, 1, 1, "Dr. Staff Member", "Ayşe Nur Öztürk-Abdulrahman", kind == ReceiptKind.Donation ? 5 : null, kind == ReceiptKind.Issue ? 7 : null, null, [301]);

    public static ReceiptListItem ReceiptRow(int id, ReceiptKind kind = ReceiptKind.Donation, string party = "Ayşe Nur Öztürk") =>
        new(id, new DateTime(2026, 11, 19, 14, 30, 0, DateTimeKind.Utc), kind, BloodType.OPos, 1, 1, "Dr. Staff Member", party, kind == ReceiptKind.Donation ? 5 : null, kind == ReceiptKind.Issue ? 7 : null);

    public static RequestListItem RequestRow(int id, int patientId, string? name = null, BloodType type = BloodType.ANeg, RequestStatus status = RequestStatus.Pending) =>
        new(id, patientId, name ?? $"Patient {patientId:D3}", type, 3, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), new DateOnly(2027, 3, 1), status);

    public static RequestDetail Request(int id = 7, int patientId = 3, string name = "Omar Haddad", RequestStatus status = RequestStatus.Pending) =>
        new(id, patientId, name, BloodType.ANeg, 3, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), new DateOnly(2027, 3, 1), status, null, "rv-r1");

    public static StaffListItem StaffRow(int id, string? name = null, bool hasPhoto = false, int? salary = 3000, Role role = Role.Staff) =>
        new(id, name ?? $"Staff {id:D3}", $"user{id}", Designation.Worker, Shift.Morning, role, hasPhoto, salary, "+1-555-0150");

    public static StaffDetail Staff(int id = 2, string name = "Staff 002", bool isProtected = false, Role role = Role.Staff) =>
        new(id, name, $"user{id}", Gender.Female, new DateOnly(1988, 4, 2), Designation.Worker, Shift.Morning, role, false, 3000, "+1-555-0150", isProtected, "rv-s1");

    public static PagedResult<StaffListItem> StaffPage(params StaffListItem[] rows) => new(rows, rows.Length, 1, 50);

    public static ProfileResponse Profile(bool hasPhoto = false) =>
        new(1, "Test Admin", "admin", Designation.Admin, Shift.FullTime, "+1-555-0101", hasPhoto, "rv-p1");
}
