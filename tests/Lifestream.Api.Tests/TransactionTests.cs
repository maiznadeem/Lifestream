using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>
/// DB-10: donation and receiving logic run inside transactions in the API. Each test makes the database fail at one named step of the
/// multi-step write (an EF command interceptor in the real API host throws when a command contains the marker) and then reads the
/// database directly: nothing of the write may remain, and the same call must succeed once the fault is gone. Real SQL Server.
/// </summary>
public sealed class TransactionTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<int> CountAsync(string sql) => await fx.Db.ScalarAsync<int>(sql, Ct);

    private async Task<(int Receipts, int Bags, int Audit)> SnapshotAsync() =>
        (await CountAsync("SELECT COUNT(*) FROM Receipt"), await CountAsync("SELECT COUNT(*) FROM BloodBag"), await CountAsync("SELECT COUNT(*) FROM AuditLog"));

    [Fact]
    public void RanAgainstARealSqlServer_AndTheFaultInjectorIsInTheApiHost()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion, StringComparison.Ordinal);
        Assert.Contains(fx.Factory.Services.GetServices<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>(), i => ReferenceEquals(i, fx.Faults));
    }

    [Theory]
    [InlineData("INSERT INTO [BloodBag]")] // the last step: the bags, after the receipt, the appointment and the donor were written
    [InlineData("INSERT INTO [AuditLog]")] // the audit rows of the business save
    [InlineData("INSERT INTO [Receipt]")] // the first step
    public async Task RecordDonation_AbortedMidWay_LeavesNoReceiptNoBagsNoAuditRow_AppointmentStillPending(string failAt)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(2, 10));
        var before = await SnapshotAsync();

        fx.Faults.FailWhenSqlContains = failAt;
        try
        {
            using var response = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(2, "fault test"), appointment.Id);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            fx.Faults.FailWhenSqlContains = null;
        }

        Assert.True(fx.Faults.Injected > 0, "the fault was never reached");
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal("Pending", await fx.Db.ScalarAsync<string>($"SELECT Status FROM Appointment WHERE Id = {appointment.Id}", Ct));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM Donor WHERE Id = {donor.Id} AND LastDonated IS NOT NULL"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM Receipt WHERE AppointmentId = {appointment.Id}"));

        // Nothing is stuck: the same call goes through once the database behaves, and writes exactly one receipt and two bags.
        using var again = await staff.Client.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(2, "fault test"), appointment.Id);
        var receipt = await again.Read<ReceiptDetail>();
        Assert.Equal(2, receipt.BagIds.Count);
        Assert.Equal(before.Receipts + 1, await CountAsync("SELECT COUNT(*) FROM Receipt"));
        Assert.Equal(before.Bags + 2, await CountAsync("SELECT COUNT(*) FROM BloodBag"));
    }

    [Theory]
    [InlineData("UPDATE [BloodBag]")] // issuing the bags, after the receipt was written and the request fulfilled
    [InlineData("INSERT INTO [AuditLog]")]
    [InlineData("INSERT INTO [Receipt]")]
    public async Task Fulfil_AbortedMidWay_LeavesNoReceipt_BagsStillAvailable_RequestStillPending(string failAt)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, type: BloodType.BNeg);
        await Seed.ClearBagsAsync(fx, BloodType.BNeg);
        await Seed.BagsAsync(fx, BloodType.BNeg, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1));
        var request = await Seed.RequestAsync(fx, patient, 2);
        var before = await SnapshotAsync();

        fx.Faults.FailWhenSqlContains = failAt;
        try
        {
            using var response = await staff.Client.PostApi(ApiRoutes.RequestFulfil, null, request.Id);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            fx.Faults.FailWhenSqlContains = null;
        }

        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal("Pending", await fx.Db.ScalarAsync<string>($"SELECT Status FROM Request WHERE Id = {request.Id}", Ct));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM BloodBag WHERE BloodType = 'BNeg' AND Status <> 'Available'"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM BloodBag WHERE IssueReceiptId IS NOT NULL AND BloodType = 'BNeg'"));

        using var again = await staff.Client.PostApi(ApiRoutes.RequestFulfil, null, request.Id);
        var receipt = await again.Read<ReceiptDetail>();
        Assert.Equal(2, receipt.BagIds.Count);
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM BloodBag WHERE BloodType = 'BNeg' AND Status = 'Issued'"));
    }
}
