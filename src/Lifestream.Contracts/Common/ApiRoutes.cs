using System.Globalization;
using System.Reflection;

namespace Lifestream.Contracts;

/// <summary>
/// Every route of the API. Templates (with {id}) are used by the server to map endpoints; <see cref="Path(string, int)"/>
/// builds the concrete path on the client. Templates are relative to <see cref="Prefix"/> except the health routes.
/// Photo routes carry raw image bytes (PUT) and return image/jpeg (GET); every other body is JSON.
/// </summary>
public static class ApiRoutes
{
    public const string Prefix = "/api/v1";

    // Health (not under the prefix; only Readyz may touch the database)
    public const string Healthz = "/healthz";
    public const string Readyz = "/readyz";

    // Auth
    public const string AuthLogin = "auth/login";
    public const string AuthDemo = "auth/demo";
    public const string AuthRefresh = "auth/refresh";
    public const string AuthLogout = "auth/logout";

    // Donors
    public const string Donors = "donors";
    public const string DonorById = "donors/{id}";
    public const string DonorEligibility = "donors/{id}/eligibility";

    /// <summary>POST: puts a donor deleted a moment ago back, with the same id and data (the Undo of a delete).</summary>
    public const string DonorRestore = "donors/{id}/restore";

    // Patients
    public const string Patients = "patients";
    public const string PatientById = "patients/{id}";

    /// <summary>POST: puts a patient deleted a moment ago back, with the same id and data (the Undo of a delete).</summary>
    public const string PatientRestore = "patients/{id}/restore";

    // Appointments (GET appointments/slots?date=yyyy-MM-dd returns SlotInfo[])
    public const string Appointments = "appointments";
    public const string AppointmentById = "appointments/{id}";
    public const string AppointmentSlots = "appointments/slots";
    public const string AppointmentDonation = "appointments/{id}/donation";
    public const string AppointmentCancel = "appointments/{id}/cancel";

    // Requests
    public const string Requests = "requests";
    public const string RequestById = "requests/{id}";
    public const string RequestFulfil = "requests/{id}/fulfil";
    public const string RequestCancel = "requests/{id}/cancel";

    // Receipts
    public const string Receipts = "receipts";
    public const string ReceiptById = "receipts/{id}";

    // Stock and dashboard (GET dashboard?from=yyyy-MM-dd&to=yyyy-MM-dd)
    public const string Stock = "stock";
    public const string Dashboard = "dashboard";

    // Staff
    public const string Staff = "staff";
    public const string StaffById = "staff/{id}";
    public const string StaffPhoto = "staff/{id}/photo";
    public const string StaffResetPassword = "staff/{id}/reset-password";

    // Profile
    public const string Me = "me";
    public const string MePassword = "me/password";
    public const string MePhoto = "me/photo";

    // Lookups (GET lookups/{kind}?search=)
    public const string LookupDonors = "lookups/donors";
    public const string LookupPatients = "lookups/patients";
    public const string LookupStaff = "lookups/staff";
    public const string LookupAppointments = "lookups/appointments";
    public const string LookupRequests = "lookups/requests";
    public const string LookupEnums = "lookups/enums";

    // Demo
    public const string DemoReset = "demo/reset";

    /// <summary>All route templates, for tests that check every route is mapped and authorised.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. typeof(ApiRoutes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(Prefix))
            .Select(f => (string)f.GetRawConstantValue()!)];

    /// <summary>Replaces {id} in a route template and prepends the API prefix.</summary>
    public static string Path(string template, int id) =>
        Path(template.Replace("{id}", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

    /// <summary>Prepends the API prefix to a template; health routes are returned unchanged.</summary>
    public static string Path(string template) =>
        template.StartsWith('/') ? template : $"{Prefix}/{template}";
}
