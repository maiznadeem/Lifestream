using System.Net;
using System.Text;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests;

/// <summary>API-05/06/07: every route against every role plus anonymous, plus the staff-management and demo rules.</summary>
public sealed class AuthorizationMatrixTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum Who
    {
        Anonymous,
        Staff,
        Admin,
        Demo,
    }

    private sealed record Endpoint(string Template, HttpMethod Method, Who[] Allowed, bool PublicRoute = false, bool SkipAllowedCheck = false);

    private static readonly Who[] Everyone = [Who.Staff, Who.Admin, Who.Demo];
    private static readonly Who[] AdminOnly = [Who.Admin];
    private static readonly Who[] AdminOrDemo = [Who.Admin, Who.Demo];
    private static readonly Who[] NotDemo = [Who.Staff, Who.Admin];

    /// <summary>Every endpoint the API maps. A route added to ApiRoutes without a row here fails <see cref="Matrix_CoversEveryRoute"/>.</summary>
    private static readonly Endpoint[] Matrix =
    [
        new(ApiRoutes.Healthz, HttpMethod.Get, [], PublicRoute: true),
        new(ApiRoutes.Readyz, HttpMethod.Get, [], PublicRoute: true),
        new(ApiRoutes.AuthLogin, HttpMethod.Post, [], PublicRoute: true),
        new(ApiRoutes.AuthDemo, HttpMethod.Post, [], PublicRoute: true),
        new(ApiRoutes.AuthRefresh, HttpMethod.Post, [], PublicRoute: true),
        new(ApiRoutes.AuthLogout, HttpMethod.Post, [], PublicRoute: true),

        new(ApiRoutes.Donors, HttpMethod.Get, Everyone),
        new(ApiRoutes.Donors, HttpMethod.Post, Everyone),
        new(ApiRoutes.DonorById, HttpMethod.Get, Everyone),
        new(ApiRoutes.DonorById, HttpMethod.Patch, Everyone),
        new(ApiRoutes.DonorById, HttpMethod.Delete, Everyone),
        new(ApiRoutes.DonorEligibility, HttpMethod.Get, Everyone),
        new(ApiRoutes.DonorRestore, HttpMethod.Post, Everyone),

        new(ApiRoutes.Patients, HttpMethod.Get, Everyone),
        new(ApiRoutes.Patients, HttpMethod.Post, Everyone),
        new(ApiRoutes.PatientById, HttpMethod.Get, Everyone),
        new(ApiRoutes.PatientById, HttpMethod.Patch, Everyone),
        new(ApiRoutes.PatientById, HttpMethod.Delete, Everyone),
        new(ApiRoutes.PatientRestore, HttpMethod.Post, Everyone),

        new(ApiRoutes.Appointments, HttpMethod.Get, Everyone),
        new(ApiRoutes.Appointments, HttpMethod.Post, Everyone),
        new(ApiRoutes.AppointmentById, HttpMethod.Get, Everyone),
        new(ApiRoutes.AppointmentById, HttpMethod.Patch, Everyone),
        new(ApiRoutes.AppointmentById, HttpMethod.Delete, Everyone),
        new(ApiRoutes.AppointmentSlots, HttpMethod.Get, Everyone),
        new(ApiRoutes.AppointmentDonation, HttpMethod.Post, Everyone),
        new(ApiRoutes.AppointmentCancel, HttpMethod.Post, Everyone),

        new(ApiRoutes.Requests, HttpMethod.Get, Everyone),
        new(ApiRoutes.Requests, HttpMethod.Post, Everyone),
        new(ApiRoutes.RequestById, HttpMethod.Get, Everyone),
        new(ApiRoutes.RequestById, HttpMethod.Patch, Everyone),
        new(ApiRoutes.RequestById, HttpMethod.Delete, Everyone),
        new(ApiRoutes.RequestFulfil, HttpMethod.Post, Everyone),
        new(ApiRoutes.RequestCancel, HttpMethod.Post, Everyone),

        new(ApiRoutes.Receipts, HttpMethod.Get, Everyone),
        new(ApiRoutes.ReceiptById, HttpMethod.Get, Everyone),
        new(ApiRoutes.Stock, HttpMethod.Get, Everyone),
        new(ApiRoutes.Dashboard, HttpMethod.Get, Everyone),

        new(ApiRoutes.Staff, HttpMethod.Get, AdminOrDemo),
        new(ApiRoutes.Staff, HttpMethod.Post, AdminOnly),
        new(ApiRoutes.StaffById, HttpMethod.Get, AdminOrDemo),
        new(ApiRoutes.StaffById, HttpMethod.Patch, AdminOnly),
        new(ApiRoutes.StaffById, HttpMethod.Delete, AdminOnly),
        new(ApiRoutes.StaffPhoto, HttpMethod.Get, AdminOrDemo),
        new(ApiRoutes.StaffPhoto, HttpMethod.Put, AdminOnly),
        new(ApiRoutes.StaffResetPassword, HttpMethod.Post, AdminOnly),

        new(ApiRoutes.Me, HttpMethod.Get, Everyone),
        new(ApiRoutes.Me, HttpMethod.Patch, NotDemo),
        new(ApiRoutes.MePassword, HttpMethod.Put, NotDemo),
        new(ApiRoutes.MePhoto, HttpMethod.Put, NotDemo),

        new(ApiRoutes.LookupDonors, HttpMethod.Get, Everyone),
        new(ApiRoutes.LookupPatients, HttpMethod.Get, Everyone),
        new(ApiRoutes.LookupStaff, HttpMethod.Get, AdminOrDemo),
        new(ApiRoutes.LookupAppointments, HttpMethod.Get, Everyone),
        new(ApiRoutes.LookupRequests, HttpMethod.Get, Everyone),
        new(ApiRoutes.LookupEnums, HttpMethod.Get, Everyone),

        // The demo account's own action; running it from the matrix would wipe the fixture, so DemoTests runs it for real.
        new(ApiRoutes.DemoReset, HttpMethod.Post, [Who.Demo], SkipAllowedCheck: true),
    ];

    private static string PathFor(Endpoint e)
    {
        var path = e.Template.StartsWith('/') ? e.Template : ApiRoutes.Path(e.Template, 999_999);
        return e.Template == ApiRoutes.AppointmentSlots ? path + "?date=2030-01-07" : path;
    }

    private static HttpRequestMessage Build(Endpoint e)
    {
        var request = new HttpRequestMessage(e.Method, PathFor(e));
        if (e.Method == HttpMethod.Put && e.Template.Contains("photo", StringComparison.Ordinal))
        {
            request.Content = new ByteArrayContent([1, 2, 3]);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        }
        else if (e.Method != HttpMethod.Get && e.Method != HttpMethod.Delete)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public void Matrix_CoversEveryRoute()
    {
        var covered = Matrix.Select(e => e.Template).ToHashSet();
        var missing = ApiRoutes.All.Where(r => !covered.Contains(r)).ToList();
        Assert.True(missing.Count == 0, "Routes without a matrix row: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task Matrix_Anonymous_Gets401_OnEveryProtectedRoute()
    {
        using var client = fx.NewClient();
        var wrong = new List<string>();
        foreach (var e in Matrix.Where(e => !e.PublicRoute))
        {
            using var request = Build(e);
            using var response = await client.SendAsync(request, Ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                wrong.Add($"{e.Method} {e.Template} -> {(int)response.StatusCode}");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public async Task Matrix_PublicRoutes_AreNotBehindAuthentication()
    {
        using var client = fx.NewClient();
        foreach (var e in Matrix.Where(e => e.PublicRoute))
        {
            using var request = Build(e);
            using var response = await client.SendAsync(request, Ct);
            Assert.True(response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{e.Method} {e.Template} -> {(int)response.StatusCode}");
        }
    }

    private async Task Matrix_EveryRoute_EveryRole(Who who)
    {
        var user = who switch
        {
            Who.Staff => await fx.AddStaffAsync(Role.Staff),
            Who.Admin => await fx.AddStaffAsync(Role.Admin),
            _ => await fx.AddDemoAsync(),
        };
        var wrong = new List<string>();
        foreach (var e in Matrix.Where(e => !e.PublicRoute))
        {
            if (e.SkipAllowedCheck && e.Allowed.Contains(who))
            {
                continue;
            }

            using var request = Build(e);
            using var response = await user.Client.SendAsync(request, Ct);
            var status = (int)response.StatusCode;
            var allowed = e.Allowed.Contains(who);
            if (allowed && status is 401 or 403)
            {
                wrong.Add($"{who} {e.Method} {e.Template} should be allowed but got {status}");
            }

            if (!allowed)
            {
                if (status != 403)
                {
                    wrong.Add($"{who} {e.Method} {e.Template} should be 403 but got {status}");
                }
                else
                {
                    using var doc = await response.Problem(403);
                    var code = doc.RootElement.GetProperty("code").GetString();
                    var expected = who == Who.Demo ? ProblemCodes.DemoForbidden : "forbidden";
                    if (code != expected)
                    {
                        wrong.Add($"{who} {e.Method} {e.Template} 403 code {code}, expected {expected}");
                    }
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // The matrix theory needs public members for xunit; this thin Fact set keeps the three roles individually named in the report.
    [Fact]
    public Task Matrix_Staff() => Matrix_EveryRoute_EveryRole(Who.Staff);

    [Fact]
    public Task Matrix_Admin() => Matrix_EveryRoute_EveryRole(Who.Admin);

    [Fact]
    public Task Matrix_Demo() => Matrix_EveryRoute_EveryRole(Who.Demo);

    [Fact]
    public async Task Staff_CanReadAndEditOnlyTheirOwnProfile()
    {
        var one = await fx.AddStaffAsync(Role.Staff);
        var two = await fx.AddStaffAsync(Role.Staff);

        var mine = await (await one.Client.GetApi(ApiRoutes.Me)).Read<Lifestream.Contracts.Profile.ProfileResponse>();
        Assert.Equal(one.Id, mine.StaffId);

        // Staff-management routes are closed to a non-admin, even for their own id.
        using var own = await one.Client.GetApi(ApiRoutes.StaffById, one.Id);
        using var p1 = await own.Problem(403, "forbidden");
        using var other = await one.Client.GetApi(ApiRoutes.StaffById, two.Id);
        using var p2 = await other.Problem(403, "forbidden");
        using var edit = await one.Client.PatchApi(ApiRoutes.StaffById, two.Id, new { rowVersion = "AAAAAAAAAAE=", displayName = "Hacked" });
        using var p3 = await edit.Problem(403, "forbidden");

        // PATCH me edits the caller's row and nobody else's.
        var updated = await (await one.Client.PatchMe(new { rowVersion = mine.RowVersion, displayName = "Renamed Me" })).Read<Lifestream.Contracts.Profile.ProfileResponse>();
        Assert.Equal("Renamed Me", updated.DisplayName);
        await using var db = fx.Db.NewContext();
        Assert.Equal($"Test {two.Username}", await db.Staff.Where(s => s.Id == two.Id).Select(s => s.DisplayName).SingleAsync(Ct));
    }

    [Fact]
    public async Task Demo_CannotCreateEditDeleteResetStaff_OrChangePasswords()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var demo = await fx.AddDemoAsync();
        var target = await fx.AddStaffAsync(Role.Staff);

        foreach (var response in new[]
        {
            await demo.Client.PostApi(ApiRoutes.Staff, new { displayName = "X" }),
            await demo.Client.PatchApi(ApiRoutes.StaffById, target.Id, new { rowVersion = "AAAAAAAAAAE=", displayName = "X" }),
            await demo.Client.DeleteApi(ApiRoutes.StaffById, target.Id),
            await demo.Client.PostApi(ApiRoutes.StaffResetPassword, id: target.Id),
            await demo.Client.PutApi(ApiRoutes.MePassword, new { currentPassword = "a", newPassword = "bbbbbbbbbbbbbbbb" }),
            await demo.Client.PatchMe(new { rowVersion = "AAAAAAAAAAE=", displayName = "X" }),
        })
        {
            using var doc = await response.Problem(403, ProblemCodes.DemoForbidden);
        }

        // ...but it can read the staff screen.
        Assert.Equal(HttpStatusCode.OK, (await demo.Client.GetApi(ApiRoutes.Staff)).StatusCode);
        Assert.NotNull(admin);
    }

    [Fact]
    public async Task Demo_CannotReachDataOutsideTheDemoDataset_OwnerAdminIsInvisible()
    {
        int ownerId;
        await using (var db = fx.Db.NewContext())
        {
            var owner = Data.TestRows.Staff("owner-hidden");
            owner.IsProtected = true;
            owner.Role = Role.Admin;
            db.Staff.Add(owner);
            await db.SaveChangesAsync(Ct);
            ownerId = owner.Id;
        }

        var demo = await fx.AddDemoAsync();
        var admin = await fx.AddStaffAsync(Role.Admin);
        var seen = await (await demo.Client.GetApi(ApiRoutes.Staff, "?pageSize=200")).Read<PagedResult<StaffListItem>>();
        Assert.DoesNotContain(seen.Items, s => s.Id == ownerId);
        using var detail = await demo.Client.GetApi(ApiRoutes.StaffById, ownerId);
        using var p = await detail.Problem(404, "not_found");
        var options = await (await demo.Client.GetApi(ApiRoutes.LookupStaff)).Read<List<Lifestream.Contracts.Lookups.RecordOption>>();
        Assert.DoesNotContain(options, o => o.Id == ownerId);

        // The admin sees the owner row; salary and phone are for admins only.
        var adminSees = await (await admin.Client.GetApi(ApiRoutes.Staff, "?pageSize=200")).Read<PagedResult<StaffListItem>>();
        Assert.Contains(adminSees.Items, s => s.Id == ownerId);
        Assert.All(seen.Items, s =>
        {
            Assert.Null(s.Salary);
            Assert.Null(s.Phone);
        });
    }

    [Fact]
    public async Task Demoted_Admin_ExistingToken_Gets403_OnAnAdminRoute()
    {
        var boss = await fx.AddStaffAsync(Role.Admin);
        var other = await fx.AddStaffAsync(Role.Admin);
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetApi(ApiRoutes.Staff)).StatusCode);

        var detail = await (await boss.Client.GetApi(ApiRoutes.StaffById, other.Id)).Read<StaffDetail>();
        var demoted = await boss.Client.PatchApi(ApiRoutes.StaffById, other.Id, new { rowVersion = detail.RowVersion, role = "Staff" });
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);

        // The same, still-unexpired token now fails the Admin policy: the database decides the role, not the token.
        using var after = await other.Client.GetApi(ApiRoutes.Staff);
        using var problem = await after.Problem(403, "forbidden");
    }

    [Fact]
    public async Task PhotoUpload_Gates_ProtectedTarget_NonAdmin_Demo()
    {
        int ownerId;
        await using (var db = fx.Db.NewContext())
        {
            var owner = Data.TestRows.Staff(fx.NextName("owner"));
            owner.IsProtected = true;
            owner.Role = Role.Admin;
            db.Staff.Add(owner);
            await db.SaveChangesAsync(Ct);
            ownerId = owner.Id;
        }

        var admin = await fx.AddStaffAsync(Role.Admin);
        var staff = await fx.AddStaffAsync(Role.Staff);
        var demo = await fx.AddDemoAsync();
        var png = TestImages.Png(300, 200);

        using var byAdmin = await admin.Client.PutBytes(ApiRoutes.StaffPhoto, ownerId, png, "image/png");
        using var p1 = await byAdmin.Problem(403, "forbidden");
        using var byStaff = await staff.Client.PutBytes(ApiRoutes.StaffPhoto, staff.Id, png, "image/png");
        using var p2 = await byStaff.Problem(403, "forbidden");
        using var byDemo = await demo.Client.PutBytes(ApiRoutes.StaffPhoto, staff.Id, png, "image/png");
        using var p3 = await byDemo.Problem(403, ProblemCodes.DemoForbidden);
        using var ok = await admin.Client.PutBytes(ApiRoutes.StaffPhoto, staff.Id, png, "image/png");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}

public sealed class ForwardedHeaderTests(LowLimitApiFixture fx) : IClassFixture<LowLimitApiFixture>
{
    [Fact]
    public async Task UntrustedRemote_SpoofedForwardedFor_IsRateLimitedByItsRealAddress()
    {
        // The peer is not on a trusted network, so X-Forwarded-For must be ignored: rotating it must not buy a fresh limit.
        using var client = fx.NewClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthLogin))
            {
                Content = JsonContent.Create(new Lifestream.Contracts.Auth.LoginRequest("nobody", "wrong-password-1"), options: ApiFixture.Json),
            };
            request.Headers.Add("X-Test-Remote", "198.51.100.7");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task TrustedProxy_ForwardedFor_NamesTheClient()
    {
        using var client = fx.NewClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthLogin))
            {
                Content = JsonContent.Create(new Lifestream.Contracts.Auth.LoginRequest("nobody-" + i, "wrong-password-1"), options: ApiFixture.Json),
            };
            request.Headers.Add("X-Test-Remote", "10.40.1.9");
            request.Headers.Add("X-Forwarded-For", $"198.18.0.{i + 1}");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }
}
