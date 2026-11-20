using System.Windows;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Lookups;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;
using Lifestream.Contracts.Stock;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// Clearly synthetic demo-like data for the part 2 screens, served by the fake API (never a real database): generated names, phone numbers in the reserved
/// range +1-555-0100 to +1-555-0199. Appointment ids and donor ids are different numbers on purpose (FEAT-10).
/// </summary>
public static class DemoData2
{
    /// <summary>A day two weeks from today: the day the fake clinic offers times on (it must never be in the past, whenever the tests run).</summary>
    public static DateOnly BookingDay => DateOnly.FromDateTime(DateTime.Today).AddDays(14);

    public static DateTime Slot(int i) => new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(i / 3).AddMinutes(30 * (i % 6));

    public static AppointmentListItem AppointmentRow(int i) =>
        new(i, 100 + i, DemoData.Name(i), DemoData.Type(i), new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc).AddHours(i), Slot(i), (AppointmentStatus)(i % 5 == 0 ? 2 : i % 7 == 0 ? 3 : i % 4 == 0 ? 1 : 0));

    public static AppointmentDetail AppointmentDetail(int i = 1)
    {
        var row = AppointmentRow(i);
        return new(row.Id, row.DonorId, row.DonorName, row.DonorBloodType, row.PlacedAtUtc, row.SlotStartUtc, AppointmentStatus.Pending, null, null, $"rv-a{i}");
    }

    public static IReadOnlyList<SlotInfo> Slots(DateOnly day) =>
        [.. Enumerable.Range(0, 16).Select(n => new SlotInfo(DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(9, 0)).AddMinutes(30 * n), DateTimeKind.Utc), n is 1 or 4 or 7))];

    public static RequestListItem RequestRow(int i) =>
        new(i, 200 + i, DemoData.Name(i + 3), DemoData.Type(i + 1), 1 + (i % 4), new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc).AddHours(i), new DateOnly(2026, 12, 1).AddDays(i), (RequestStatus)(i % 6 == 0 ? 1 : i % 9 == 0 ? 2 : 0));

    public static RequestDetail RequestDetail(int i = 1)
    {
        var row = RequestRow(i);
        return new(row.Id, row.PatientId, row.PatientName, row.BloodType, row.Units, row.PlacedAtUtc, row.RequiredBy, RequestStatus.Pending, null, $"rv-r{i}");
    }

    public static ReceiptListItem ReceiptRow(int i) =>
        new(i, new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc).AddHours(i * 3), i % 3 == 0 ? ReceiptKind.Issue : ReceiptKind.Donation, DemoData.Type(i), 1 + (i % 2), 1 + (i % 4), $"Dr. {DemoData.Name(i + 5)}",
            DemoData.Name(i), i % 3 == 0 ? null : 300 + i, i % 3 == 0 ? 400 + i : null);

    public static ReceiptDetail ReceiptDetail(int i = 1)
    {
        var row = ReceiptRow(i);
        return new(row.Id, row.CreatedAtUtc, row.Kind, row.BloodType, row.Units, row.StaffId, row.StaffName, row.PartyName, row.AppointmentId, row.RequestId, null, [5001 + i]);
    }

    public static StaffListItem StaffRow(int i) =>
        new(i, i == 1 ? "Test User" : DemoData.Name(i + 7), $"user{i:D3}", (Designation)(i % 4), (Shift)(i % 3), i % 11 == 1 ? Role.Admin : Role.Staff, i % 2 == 0, 2400 + (i * 35 % 3000), DemoData.Phone(i));

    public static StaffDetail StaffDetail(int i = 2)
    {
        var row = StaffRow(i);
        return new(row.Id, row.DisplayName, row.Username, (Gender)(i % 3), new DateOnly(1985 + (i % 15), 1 + (i % 12), 1 + (i % 27)), row.Designation, row.Shift, row.Role, row.HasPhoto, row.Salary, row.Phone, false, $"rv-s{i}");
    }

    /// <summary>A small valid JPEG (a drawn portrait), so a thumbnail has real bytes to decode and a render shows a photo.</summary>
    public static byte[] Jpeg()
    {
        // A small drawn portrait (a head with hair and shoulders on a pale background), so a render shows something a reader takes for a photo. A flat
        // colour looked like a broken avatar in the design review.
        const int size = 64;
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        var pixels = new byte[size * size * 4];
        static bool In(double x, double y, double cx, double cy, double rx, double ry) => (((x - cx) * (x - cx)) / (rx * rx)) + (((y - cy) * (y - cy)) / (ry * ry)) <= 1;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                (byte R, byte G, byte B) colour = (0xC9, 0xD6, 0xE3); // background
                if (In(x, y, 32, 70, 27, 25))
                {
                    colour = (0x3A, 0x4A, 0x5E); // shoulders
                }

                if (In(x, y, 32, 27, 13, 15))
                {
                    colour = y < 20 && In(x, y, 32, 24, 14, 14) ? ((byte)0x4A, (byte)0x35, (byte)0x28) : ((byte)0xE0, (byte)0xB8, (byte)0x99); // hair over the face
                }

                var i = ((y * size) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (colour.B, colour.G, colour.R);
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static ProfileResponse Profile(bool hasPhoto = true) => new(1, "Test User", "tuser", Designation.Admin, Shift.FullTime, "+1-555-0142", hasPhoto, "rv-p1");

    public static DonorDetail Donor(int id, bool recentlyDonated) =>
        new(id, DemoData.Name(1), new DateOnly(1990, 5, 17), Gender.Female, BloodType.OPos, DemoData.Phone(1), "12 Example Street, Demo City", DonorFrequency.Regular,
            DemoData.Today.AddDays(recentlyDonated ? -45 : -200), "rv-d1");

    /// <summary>Scripts the fake API with a page of every part 2 list, the details, slots, stock, photos and the profile.</summary>
    public static void Configure(FakeApiClient fake, int appointmentTotal = 86, int requestTotal = 40, int receiptTotal = 120, int staffTotal = 110)
    {
        fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new([.. Enumerable.Range(1, Math.Min(50, appointmentTotal)).Select(AppointmentRow)], appointmentTotal, 1, 50)));
        fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(AppointmentDetail()));
        fake.On("GetSlotsAsync", _ => Task.FromResult(ApiResult<IReadOnlyList<SlotInfo>>.Ok(Slots(BookingDay))));
        fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Donor(101, recentlyDonated: false)));
        fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([.. Enumerable.Range(1, Math.Min(50, requestTotal)).Select(RequestRow)], requestTotal, 1, 50)));
        fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(RequestDetail()));
        fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(DemoData.Stock()));
        fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([.. Enumerable.Range(1, Math.Min(50, receiptTotal)).Select(ReceiptRow)], receiptTotal, 1, 50)));
        fake.On("GetReceiptAsync", ApiResult<ReceiptDetail>.Ok(ReceiptDetail()));
        fake.On("GetStaffAsync", ApiResult<PagedResult<StaffListItem>>.Ok(new([.. Enumerable.Range(1, Math.Min(50, staffTotal)).Select(StaffRow)], staffTotal, 1, 50)));
        fake.On("GetStaffMemberAsync", ApiResult<StaffDetail>.Ok(StaffDetail()));
        fake.On("GetStaffPhotoAsync", ApiResult<byte[]>.Ok(Jpeg()));
        fake.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Profile()));
        fake.On("GetMyPhotoAsync", ApiResult<byte[]>.Ok(Jpeg()));
    }
}

/// <summary>Shared set-up of the part 2 view tests: a client rig signed in as an admin with the WPF test dispatcher, capture saving, and the record picker wired to the fake API.</summary>
public static class Part2Harness
{
    /// <summary>Where render captures go: <c>LIFESTREAM_CAPTURE_DIR</c> when set, otherwise this task's captures folder.</summary>
    public static string CaptureDirectory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } fromEnvironment
        ? fromEnvironment
        : Directory.Exists(@"G:\Claude\workspace\lifestream\run") ? @"G:\Claude\workspace\lifestream\run\captures\b-06-features2" : Hosted.CaptureDirectory;

    /// <summary>A rig signed in with the given role, the part 2 data on its fake API and the record pickers wired to it.</summary>
    /// <param name="role">The signed-in user's role.</param>
    /// <param name="demo">Whether the session is the public demo's.</param>
    /// <param name="clock">The clock of the rig's navigation, toasts and dialogs (a manual clock, so a test can step over the 3 s waking threshold); the system clock when null.</param>
    public static Rig NewRig(Role role = Role.Admin, bool demo = false, TimeProvider? clock = null)
    {
        var rig = new Rig(dispatcher: WpfTestUi.Dispatcher(), clock: clock);
        DemoData.Configure(rig.Api);
        DemoData2.Configure(rig.Api);
        rig.SignIn(role, demo);
        RecordPicker.InlineResults = true; // never open a popup window on the desktop
        RecordPicker.ViewModelFactory = kind => new RecordPickerViewModel(rig.Api, kind, debounce: TimeSpan.FromMilliseconds(10));
        rig.Api.On("SearchRecordsAsync", ApiResult<IReadOnlyList<RecordOption>>.Ok([new RecordOption(101, DemoData.Name(1), "O+"), new RecordOption(102, DemoData.Name(2), "A+")]));
        return rig;
    }

    public static T Create<T>(Rig rig)
        where T : ViewModelBase
    {
        var vm = ActivatorUtilities.CreateInstance<T>(rig.Provider);
        vm.Attach(null, TimeProvider.System);
        return vm;
    }

    /// <summary>Gives a view the canvas colour the shell's page area has, so a capture of the bare view looks as it does in the app.</summary>
    public static T OnCanvas<T>(T view, object model)
        where T : System.Windows.Controls.UserControl
    {
        view.DataContext = model;
        view.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Brush.SurfaceCanvas");
        return view;
    }

    /// <summary>Saves the capture under the capture folder and returns the PNG bytes.</summary>
    public static byte[] Save(Hosted host, string fileName)
    {
        var png = host.Capture();
        Directory.CreateDirectory(CaptureDirectory);
        File.WriteAllBytes(Path.Combine(CaptureDirectory, fileName), png);
        return png;
    }

    public static IEnumerable<T> Visible<T>(Hosted host)
        where T : FrameworkElement => host.All<T>().Where(e => e.IsVisible);
}
