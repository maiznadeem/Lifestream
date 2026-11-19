using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

/// <summary>FEAT-06 (real API, real SQL Server): Undo after a delete restores the record faithfully, with the same id and every value.</summary>
public sealed class RestoreTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RestoreDonor_PutsBackTheSameIdAndEveryValue_IncludingLastDonatedAndTheHold()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var last = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-25));
        var donor = await Seed.DonorAsync(fx, lastDonated: last, medicalHold: true, type: BloodType.BNeg);
        var before = await (await staff.Client.GetApi(ApiRoutes.DonorById, donor.Id)).Read<DonorDetail>();

        using var deleted = await staff.Client.DeleteApi(ApiRoutes.DonorById, donor.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await staff.Client.GetApi(ApiRoutes.DonorById, donor.Id);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        var restored = await (await staff.Client.PostApi(ApiRoutes.DonorRestore, null, donor.Id)).Read<DonorDetail>();

        Assert.Equal(donor.Id, restored.Id);
        Assert.Equal(before with { RowVersion = restored.RowVersion }, restored);
        Assert.Equal(last, restored.LastDonated);
        Assert.True(restored.MedicalHold);
        var eligibility = await (await staff.Client.GetApi(ApiRoutes.DonorEligibility, donor.Id)).Read<DonorEligibility>();
        Assert.False(eligibility.IsEligible);
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DeletedRecord WHERE Entity = 'Donor' AND EntityId = {donor.Id}", Ct));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = 'Donor' AND EntityId = {donor.Id} AND Action = 'Delete'", Ct));
        Assert.Equal(2, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM AuditLog WHERE Entity = 'Donor' AND EntityId = {donor.Id} AND Action = 'Insert'", Ct)); // the seed row and the restore
    }

    [Fact]
    public async Task RestorePatient_PutsBackTheSameIdAndEveryValue()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, type: BloodType.ABNeg);
        var before = await (await staff.Client.GetApi(ApiRoutes.PatientById, patient.Id)).Read<PatientDetail>();
        (await staff.Client.DeleteApi(ApiRoutes.PatientById, patient.Id)).Dispose();

        var restored = await (await staff.Client.PostApi(ApiRoutes.PatientRestore, null, patient.Id)).Read<PatientDetail>();

        Assert.Equal(patient.Id, restored.Id);
        Assert.Equal(before with { RowVersion = restored.RowVersion }, restored);
    }

    [Fact]
    public async Task Restore_NeverDeleted_404_AndTwice_404()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        using var never = await staff.Client.PostApi(ApiRoutes.DonorRestore, null, 2_000_000_000);
        using var none = await never.Problem(404);

        var donor = await Seed.DonorAsync(fx);
        (await staff.Client.DeleteApi(ApiRoutes.DonorById, donor.Id)).Dispose();
        (await staff.Client.PostApi(ApiRoutes.DonorRestore, null, donor.Id)).Dispose();
        using var again = await staff.Client.PostApi(ApiRoutes.DonorRestore, null, donor.Id);
        using var twice = await again.Problem(404); // the copy was used up by the first restore
    }

    [Fact]
    public async Task Restore_AfterTheWindow_404()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        (await staff.Client.DeleteApi(ApiRoutes.DonorById, donor.Id)).Dispose();
        await fx.Db.ExecuteAsync($"UPDATE DeletedRecord SET DeletedAtUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE Entity = 'Donor' AND EntityId = {donor.Id}", Ct);

        using var late = await staff.Client.PostApi(ApiRoutes.DonorRestore, null, donor.Id);
        using var problem = await late.Problem(404);
    }

    [Fact]
    public async Task DeleteDonorWithAppointments_Still409_AndLeavesNoRestorableCopy()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        await Seed.AppointmentAsync(fx, donor, Seed.Slot(5, 10));

        using var refused = await staff.Client.DeleteApi(ApiRoutes.DonorById, donor.Id);
        using var problem = await refused.Problem(409);

        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DeletedRecord WHERE Entity = 'Donor' AND EntityId = {donor.Id}", Ct));
        using var still = await staff.Client.GetApi(ApiRoutes.DonorById, donor.Id);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task ExpiredCopies_AreDroppedByTheNextDeleteAndTheNextRestore_NoTimerNeeded()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var first = await Seed.DonorAsync(fx);
        var second = await Seed.DonorAsync(fx);
        var third = await Seed.DonorAsync(fx);
        (await staff.Client.DeleteApi(ApiRoutes.DonorById, first.Id)).Dispose();
        await fx.Db.ExecuteAsync($"UPDATE DeletedRecord SET DeletedAtUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE EntityId = {first.Id}", Ct);

        (await staff.Client.DeleteApi(ApiRoutes.DonorById, second.Id)).Dispose(); // the delete drops the expired copy
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DeletedRecord WHERE EntityId = {first.Id}", Ct));

        (await staff.Client.DeleteApi(ApiRoutes.DonorById, third.Id)).Dispose();
        await fx.Db.ExecuteAsync($"UPDATE DeletedRecord SET DeletedAtUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE EntityId = {third.Id}", Ct);
        (await staff.Client.PostApi(ApiRoutes.DonorRestore, null, second.Id)).Dispose(); // the restore drops the other expired copy
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM DeletedRecord WHERE EntityId = {third.Id}", Ct));
    }

    [Fact]
    public async Task TheKeptCopy_HoldsNoRowVersion()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx, lastDonated: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
        (await staff.Client.DeleteApi(ApiRoutes.DonorById, donor.Id)).Dispose();

        var json = await fx.Db.ScalarAsync<string>($"SELECT Json FROM DeletedRecord WHERE Entity = 'Donor' AND EntityId = {donor.Id}", Ct);

        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lastDonated", json, StringComparison.OrdinalIgnoreCase);
    }
}
