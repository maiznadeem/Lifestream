using System.Net;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>Requests, fulfilling from stock (FIFO), receipts, and the stock endpoint (API-14, 16, 18, 20, 22, 24, 25, 29).</summary>
public sealed class RequestStockTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Ago(int days) => DateTime.UtcNow.AddDays(-days);

    // ---- creating and changing requests ----

    [Fact]
    public async Task Request_Create_Returns201WithSavedResource_BloodTypeFromThePatient()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, "Request Patient", BloodType.ABNeg);
        var body = new CreateRequestRequest(patient.Id, 3, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        using var response = await staff.Client.PostApi(ApiRoutes.Requests, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await response.Read<RequestDetail>();
        Assert.Equal("Request Patient", saved.PatientName);
        Assert.Equal(BloodType.ABNeg, saved.BloodType);
        Assert.Equal(RequestStatus.Pending, saved.Status);
        Assert.Equal(3, saved.Units);
    }

    [Theory]
    [InlineData(0, 5, "units")]
    [InlineData(11, 5, "units")]
    [InlineData(2, -1, "requiredBy")]
    [InlineData(2, 800, "requiredBy")]
    public async Task Request_Create_InvalidInput_400(int units, int daysAhead, string field)
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx);
        using var response = await staff.Client.PostApi(ApiRoutes.Requests, new CreateRequestRequest(patient.Id, units, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysAhead))));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Request_Create_DefaultDate_UnknownPatient_400()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx);
        using var noDate = await staff.Client.PostApi(ApiRoutes.Requests, new CreateRequestRequest(patient.Id, 1, default));
        using var p1 = await noDate.Problem(400, ProblemCodes.Validation);
        using var unknown = await staff.Client.PostApi(ApiRoutes.Requests, new CreateRequestRequest(987654, 1, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2))));
        using var p2 = await unknown.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Request_UpdateOrDelete_UnknownId_404()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        using var patch = await staff.Client.PatchApi(ApiRoutes.RequestById, 987654, new UpdateRequestRequest("AAAAAAAAAAE=", Units: 2));
        using var p1 = await patch.Problem(404, ProblemCodes.NotFound);
        using var delete = await staff.Client.DeleteApi(ApiRoutes.RequestById, 987654);
        using var p2 = await delete.Problem(404, ProblemCodes.NotFound);
    }

    [Fact]
    public async Task Request_Patch_ChangesOnlySuppliedFields_StaleVersion409()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx);
        var created = await (await staff.Client.PostApi(ApiRoutes.Requests, new CreateRequestRequest(patient.Id, 2, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(9))))).Read<RequestDetail>();
        var updated = await (await staff.Client.PatchApi(ApiRoutes.RequestById, created.Id, new UpdateRequestRequest(created.RowVersion, Units: 4))).Read<RequestDetail>();
        Assert.Equal(4, updated.Units);
        Assert.Equal(created.RequiredBy, updated.RequiredBy);
        using var stale = await staff.Client.PatchApi(ApiRoutes.RequestById, created.Id, new UpdateRequestRequest(created.RowVersion, Units: 5));
        using var p = await stale.Problem(409, ProblemCodes.Concurrency);
    }

    // ---- fulfilling from stock (API-18) ----

    [Fact]
    public async Task Fulfil_IssuesOldestUnexpiredFirst_InsideOneTransaction()
    {
        await Seed.ClearBagsAsync(fx, BloodType.ANeg);
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, type: BloodType.ANeg);

        // Collected 50 days ago = expired (42-day life): older than everything, but must never be issued.
        var expired = (await Seed.BagsAsync(fx, BloodType.ANeg, Ago(50)))[0];
        var bags = await Seed.BagsAsync(fx, BloodType.ANeg, Ago(30), Ago(10), Ago(20), Ago(2));
        var oldest = bags[0];
        var secondOldest = bags[2];
        var request = await Seed.RequestAsync(fx, patient, 2);

        using var response = await staff.Client.PostApi(ApiRoutes.RequestFulfil, id: request.Id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Read<ReceiptDetail>();
        Assert.Equal(ReceiptKind.Issue, receipt.Kind);
        Assert.Equal(staff.Id, receipt.StaffId);
        Assert.Equal(new[] { oldest.Id, secondOldest.Id }.Order(), receipt.BagIds.Order());

        var all = await Seed.AllBagsAsync(fx, BloodType.ANeg);
        Assert.Equal(BagStatus.Available, all.Single(b => b.Id == expired.Id).Status);
        Assert.Equal(2, all.Count(b => b.Status == BagStatus.Issued && b.IssueReceiptId == receipt.Id));
        Assert.Equal(BagStatus.Issued, all.Single(b => b.Id == oldest.Id).Status);
        Assert.Equal(BagStatus.Available, all.Single(b => b.Id == bags[1].Id).Status);

        await using var db = fx.Db.NewContext();
        Assert.Equal(RequestStatus.Fulfilled, (await db.Requests.SingleAsync(r => r.Id == request.Id, Ct)).Status);

        using var again = await staff.Client.PostApi(ApiRoutes.RequestFulfil, id: request.Id);
        using var p = await again.Problem(409, ProblemCodes.Conflict);
    }

    [Fact]
    public async Task Fulfil_InsufficientStock_409_NothingChanges()
    {
        await Seed.ClearBagsAsync(fx, BloodType.BPos);
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, type: BloodType.BPos);
        await Seed.BagsAsync(fx, BloodType.BPos, Ago(3), Ago(4));
        await Seed.BagsAsync(fx, BloodType.BPos, Ago(60)); // expired, must not count toward the stock
        var request = await Seed.RequestAsync(fx, patient, 3);
        await using var db = fx.Db.NewContext();
        var receiptsBefore = await db.Receipts.CountAsync(Ct);

        using var response = await staff.Client.PostApi(ApiRoutes.RequestFulfil, id: request.Id);
        using var problem = await response.Problem(409, ProblemCodes.InsufficientStock);

        Assert.Equal(RequestStatus.Pending, (await db.Requests.AsNoTracking().SingleAsync(r => r.Id == request.Id, Ct)).Status);
        Assert.Equal(receiptsBefore, await db.Receipts.CountAsync(Ct));
        var bags = await Seed.AllBagsAsync(fx, BloodType.BPos);
        Assert.Equal(3, bags.Count);
        Assert.All(bags, b =>
        {
            Assert.Equal(BagStatus.Available, b.Status);
            Assert.Null(b.IssueReceiptId);
        });
    }

    [Fact]
    public async Task Fulfil_TwoAtOnce_NeverIssuesTwice()
    {
        await Seed.ClearBagsAsync(fx, BloodType.ABPos);
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, type: BloodType.ABPos);
        await Seed.BagsAsync(fx, BloodType.ABPos, Ago(1), Ago(2), Ago(3), Ago(4));
        var request = await Seed.RequestAsync(fx, patient, 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var response = await staff.Client.PostApi(ApiRoutes.RequestFulfil, id: request.Id);
            return response.StatusCode;
        }));
        Assert.Equal(1, results.Count(s => s == HttpStatusCode.Created));
        var bags = await Seed.AllBagsAsync(fx, BloodType.ABPos);
        Assert.Equal(2, bags.Count(b => b.Status == BagStatus.Issued));
    }

    [Fact]
    public async Task Request_Cancel_ThenCannotFulfil()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx);
        var request = await Seed.RequestAsync(fx, patient, 1);
        var cancelled = await (await staff.Client.PostApi(ApiRoutes.RequestCancel, id: request.Id)).Read<RequestDetail>();
        Assert.Equal(RequestStatus.Cancelled, cancelled.Status);
        using var fulfil = await staff.Client.PostApi(ApiRoutes.RequestFulfil, id: request.Id);
        using var p = await fulfil.Problem(409, ProblemCodes.Conflict);
    }

    // ---- stock (API-20, 29) ----

    [Fact]
    public async Task Stock_ExcludesExpired_FlagsBelowThreshold_CountsExpiringSoon()
    {
        await Seed.ClearBagsAsync(fx, BloodType.OPos);
        var staff = await fx.AddStaffAsync(Role.Staff);

        // 12 fresh bags, 2 expiring within 7 days (collected 37 and 40 days ago), 3 expired. No job ever ran.
        await Seed.BagsAsync(fx, BloodType.OPos, [.. Enumerable.Range(1, 12).Select(i => Ago(i % 5))]);
        await Seed.BagsAsync(fx, BloodType.OPos, Ago(37), Ago(40));
        await Seed.BagsAsync(fx, BloodType.OPos, Ago(43), Ago(50), Ago(90));

        var stock = await (await staff.Client.GetApi(ApiRoutes.Stock)).Read<List<StockItem>>();
        Assert.Equal(8, stock.Count);
        var item = stock.Single(s => s.BloodType == BloodType.OPos);
        Assert.Equal(14, item.Available);
        Assert.Equal(2, item.ExpiringSoon);
        Assert.Equal(10, item.Threshold);
        Assert.False(item.BelowThreshold);
    }

    [Fact]
    public async Task Stock_BelowThresholdFlag_IsSetFromTheConfiguredMinimum()
    {
        await Seed.ClearBagsAsync(fx, BloodType.ONeg);
        var staff = await fx.AddStaffAsync(Role.Staff);
        await Seed.BagsAsync(fx, BloodType.ONeg, Ago(1), Ago(2), Ago(3));
        var low = (await (await staff.Client.GetApi(ApiRoutes.Stock)).Read<List<StockItem>>()).Single(s => s.BloodType == BloodType.ONeg);
        Assert.Equal(3, low.Available);
        Assert.True(low.BelowThreshold);
        var none = (await (await staff.Client.GetApi(ApiRoutes.Stock)).Read<List<StockItem>>()).Single(s => s.BloodType == BloodType.BNeg && s.Available == 0);
        Assert.True(none.BelowThreshold);
    }

    // ---- receipts and requests lists ----

    [Fact]
    public async Task Receipts_ListShowsNames_DetailShowsBags_NoPlaceholders()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var receipt = await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddMinutes(-5));
        // By its code, which names this receipt and nothing else. The number alone is ambiguous by the search rule: it is also a word of a name
        // (another test's "Bag donor 7" when this receipt is number 7) and, from three digits, a part of a phone.
        var page = await (await staff.Client.GetApi(ApiRoutes.Receipts, $"?search=RC-{receipt.Id}")).Read<PagedResult<ReceiptListItem>>();
        var item = Assert.Single(page.Items);
        Assert.Equal(receipt.Id, item.Id);
        Assert.StartsWith("Receipt donor", item.PartyName, StringComparison.Ordinal);
        Assert.Equal("Test Staff", item.StaffName);
        var detail = await (await staff.Client.GetApi(ApiRoutes.ReceiptById, receipt.Id)).Read<ReceiptDetail>();
        Assert.Equal(item.PartyName, detail.PartyName);
        using var missing = await staff.Client.GetApi(ApiRoutes.ReceiptById, 987654);
        using var p = await missing.Problem(404, ProblemCodes.NotFound);
    }

    [Fact]
    public async Task Receipts_DateFilter_ToDateIncludesEveryRecordThatDay()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var day = DateTime.UtcNow.Date.AddDays(-200);
        var late = await Seed.ReceiptAsync(fx, ReceiptKind.Issue, day.AddHours(23).AddMinutes(59).AddSeconds(30));
        var after = await Seed.ReceiptAsync(fx, ReceiptKind.Issue, day.AddDays(1).AddMinutes(1));
        var d = day.ToString("yyyy-MM-dd");
        var page = await (await staff.Client.GetApi(ApiRoutes.Receipts, $"?fromUtc={d}&toUtc={d}&kind=Issue")).Read<PagedResult<ReceiptListItem>>();
        Assert.Contains(page.Items, r => r.Id == late.Id);
        Assert.DoesNotContain(page.Items, r => r.Id == after.Id);
    }

    [Fact]
    public async Task Requests_DateFilter_ToDateIncludesEveryRecordThatDay()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx);
        var day = DateTime.UtcNow.Date.AddDays(-300);
        var late = await Seed.RequestAsync(fx, patient, 1, placedAtUtc: day.AddHours(23).AddMinutes(59));
        var after = await Seed.RequestAsync(fx, patient, 1, placedAtUtc: day.AddDays(1).AddMinutes(2));
        var d = day.ToString("yyyy-MM-dd");
        var page = await (await staff.Client.GetApi(ApiRoutes.Requests, $"?patientId={patient.Id}&fromUtc={d}&toUtc={d}")).Read<PagedResult<RequestListItem>>();
        Assert.Contains(page.Items, r => r.Id == late.Id);
        Assert.DoesNotContain(page.Items, r => r.Id == after.Id);
    }

    [Fact]
    public async Task Requests_ListCarriesPatientNames_AndSearchMatchesNameIdAndContact()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, "Quillon Requestee");
        var request = await Seed.RequestAsync(fx, patient, 2);
        foreach (var term in new[] { "quill", request.Id.ToString(), "+1-555-0102" })
        {
            var page = await (await staff.Client.GetApi(ApiRoutes.Requests, "?search=" + Uri.EscapeDataString(term))).Read<PagedResult<RequestListItem>>();
            var hit = page.Items.FirstOrDefault(r => r.Id == request.Id);
            Assert.NotNull(hit);
            Assert.Equal("Quillon Requestee", hit.PatientName);
            Assert.Equal(patient.Id, hit.PatientId);
        }
    }

    [Fact]
    public async Task RequestsList_FilterByPatientName_OneSqlCommand()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var patient = await Seed.PatientAsync(fx, "Onecommand Patient");
        for (var i = 0; i < 6; i++)
        {
            await Seed.RequestAsync(fx, patient, 1);
        }

        // Warm the session check so only the list itself is counted.
        (await staff.Client.GetApi(ApiRoutes.Me)).Dispose();
        foreach (var route in new[] { ApiRoutes.Requests, ApiRoutes.Receipts, ApiRoutes.Appointments })
        {
            fx.Commands.Clear();
            fx.Commands.Recording = true;
            try
            {
                var query = route == ApiRoutes.Requests ? "?search=onecommand" : "?search=zzz";
                var response = await staff.Client.GetApi(route, query);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                fx.Commands.Recording = false;
            }

            var commands = fx.Commands.Commands;
            Assert.True(commands.Count == 1, $"{route}: expected exactly one SQL command, got {commands.Count}: {string.Join(" || ", commands)}");
            Assert.Contains("JOIN", commands[0], StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT *", commands[0], StringComparison.Ordinal);
        }
    }
}
