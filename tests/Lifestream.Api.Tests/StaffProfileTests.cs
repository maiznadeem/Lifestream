using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Staff;
using Lifestream.Data.Security;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lifestream.Api.Tests;

/// <summary>Staff management, the profile, passwords and photos (API-07, 15, 30-35, 36).</summary>
public sealed class StaffProfileTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, object?> NewStaff(string username, Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["displayName"] = "New Person",
            ["username"] = username,
            ["gender"] = "Female",
            ["dateOfBirth"] = "1992-03-04",
            ["designation"] = "Doctor",
            ["shift"] = "Morning",
            ["role"] = "Staff",
            ["salary"] = 51000,
            ["phone"] = "+1-555-0150",
            ["initialPassword"] = "a-long-initial-password",
        };
        change?.Invoke(body);
        return body;
    }

    private static string Json(object body) => JsonSerializer.Serialize(body);

    // ---- create / update / delete ----

    [Fact]
    public async Task Staff_Create_Returns201WithSavedResource_StoresAHash_AndTheNewPersonCanLogIn()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var username = fx.NextName("newbie");
        using var response = await admin.Client.PostRaw(ApiRoutes.Staff, Json(NewStaff(username)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("a-long-initial-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("assword", text, StringComparison.Ordinal);
        var detail = JsonSerializer.Deserialize<StaffDetail>(text, ApiFixture.Json)!;
        Assert.Equal(username, detail.Username);
        Assert.Equal(51000, detail.Salary);

        var hash = await fx.Db.ScalarAsync<string>($"SELECT PasswordHash FROM Staff WHERE Id = {detail.Id}", Ct);
        Assert.True(PasswordHashing.IsValidHash(hash));
        Assert.NotEqual("a-long-initial-password", hash);

        using var anonymous = fx.NewClient();
        using var login = await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, "a-long-initial-password"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Staff_Create_DuplicateUsername_409_CaseInsensitive()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var username = fx.NextName("dupe");
        (await admin.Client.PostRaw(ApiRoutes.Staff, Json(NewStaff(username)))).Dispose();
        using var again = await admin.Client.PostRaw(ApiRoutes.Staff, Json(NewStaff(username.ToUpperInvariant())));
        using var problem = await again.Problem(409, ProblemCodes.Conflict);
    }

    public static TheoryData<string, string, object?> StaffRules => new()
    {
        { "display name required", "displayName", "" },
        { "display name at most 100", "displayName", "EXTRA100" },
        { "username required", "username", "" },
        { "username at least 3 characters", "username", "ab" },
        { "username only safe characters", "username", "bad name!" },
        { "gender real value", "gender", "Robot" },
        { "designation real value", "designation", "Wizard" },
        { "shift real value", "shift", "Night" },
        { "role real value", "role", "Owner" },
        { "date of birth in the past", "dateOfBirth", "2999-01-01" },
        { "date of birth not DateTime.MinValue", "dateOfBirth", "0001-01-01" },
        { "staff at least 16", "dateOfBirth", "TOOYOUNG" },
        { "salary not negative", "salary", -1 },
        { "salary within range", "salary", 99_999_999 },
        { "phone shape", "phone", "nope" },
        { "initial password at least 12", "initialPassword", "short-pass" },
        { "initial password at most 128", "initialPassword", "EXTRA129" },
    };

    [Theory]
    [MemberData(nameof(StaffRules))]
    public async Task Staff_Create_InvalidInput_400(string rule, string field, object? value)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        value = value switch
        {
            "EXTRA100" => new string('d', 101),
            "EXTRA129" => new string('p', 129),
            "TOOYOUNG" => DateTime.UtcNow.AddYears(-10).ToString("yyyy-MM-dd"),
            _ => value,
        };
        using var response = await admin.Client.PostRaw(ApiRoutes.Staff, Json(NewStaff(fx.NextName("rule"), b => b[field] = value)));
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"{rule}: {problem.RootElement}");
    }

    [Fact]
    public async Task Staff_Patch_OnlySuppliedFields_SalaryDobAndPhotoUntouched_ExplicitNullClears()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var created = await (await admin.Client.PostRaw(ApiRoutes.Staff, Json(NewStaff(fx.NextName("partial"))))).Read<StaffDetail>();
        await admin.Client.PutBytes(ApiRoutes.StaffPhoto, created.Id, TestImages.Jpeg(300, 300, 60), "image/jpeg");
        var beforePhoto = await fx.Db.ScalarAsync<string>($"SELECT CONVERT(varchar(max), CAST(Content AS varbinary(max)), 2) FROM StaffPhoto WHERE StaffId = {created.Id}", Ct);
        var current = await (await admin.Client.GetApi(ApiRoutes.StaffById, created.Id)).Read<StaffDetail>();

        var renamed = await (await admin.Client.PatchRaw(ApiRoutes.StaffById, created.Id, Json(new { rowVersion = current.RowVersion, displayName = "Renamed Staff" }))).Read<StaffDetail>();
        Assert.Equal("Renamed Staff", renamed.DisplayName);
        Assert.Equal(created.Salary, renamed.Salary);
        Assert.Equal(created.DateOfBirth, renamed.DateOfBirth);
        Assert.Equal(created.Phone, renamed.Phone);
        Assert.True(renamed.HasPhoto);
        Assert.Equal(beforePhoto, await fx.Db.ScalarAsync<string>($"SELECT CONVERT(varchar(max), CAST(Content AS varbinary(max)), 2) FROM StaffPhoto WHERE StaffId = {created.Id}", Ct));

        var cleared = await (await admin.Client.PatchRaw(ApiRoutes.StaffById, created.Id, $"{{\"rowVersion\":\"{renamed.RowVersion}\",\"salary\":null,\"phone\":null}}")).Read<StaffDetail>();
        Assert.Null(cleared.Salary);
        Assert.Null(cleared.Phone);
        Assert.Equal("Renamed Staff", cleared.DisplayName);

        using var stale = await admin.Client.PatchRaw(ApiRoutes.StaffById, created.Id, Json(new { rowVersion = current.RowVersion, displayName = "Late" }));
        using var problem = await stale.Problem(409, ProblemCodes.Concurrency);
    }

    [Fact]
    public async Task Staff_UpdateOrDelete_UnknownId_404()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var patch = await admin.Client.PatchRaw(ApiRoutes.StaffById, 987654, Json(new { rowVersion = "AAAAAAAAAAE=", displayName = "x" }));
        using var p1 = await patch.Problem(404, ProblemCodes.NotFound);
        using var delete = await admin.Client.DeleteApi(ApiRoutes.StaffById, 987654);
        using var p2 = await delete.Problem(404, ProblemCodes.NotFound);
        using var reset = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, id: 987654);
        using var p3 = await reset.Problem(404, ProblemCodes.NotFound);
    }

    [Fact]
    public async Task Staff_Delete_SelfIs409_LastAdminCannotBeDemoted_OwnerCannotBeDeleted()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var self = await admin.Client.DeleteApi(ApiRoutes.StaffById, admin.Id);
        using var p1 = await self.Problem(409, ProblemCodes.Conflict);

        // Make this the only admin, then try to demote it.
        await fx.Db.ExecuteAsync($"UPDATE Staff SET Role = 'Staff' WHERE Role = 'Admin' AND Id <> {admin.Id}", Ct);
        var mine = await (await admin.Client.GetApi(ApiRoutes.StaffById, admin.Id)).Read<StaffDetail>();
        using var demote = await admin.Client.PatchRaw(ApiRoutes.StaffById, admin.Id, Json(new { rowVersion = mine.RowVersion, role = "Staff" }));
        using var p2 = await demote.Problem(409, ProblemCodes.Conflict);

        // A second admin appears: deleting the first admin's colleague works, and the protected owner never can be deleted.
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        using var ownerDelete = await admin.Client.DeleteApi(ApiRoutes.StaffById, owner.Id);
        using var p3 = await ownerDelete.Problem(409, ProblemCodes.Conflict);
    }

    [Fact]
    public async Task Staff_Delete_WithDependentRows_Returns409WithCode_NoSqlText()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var donor = await Seed.DonorAsync(fx);
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(80, 10));
        (await worker.Client.PostApi(ApiRoutes.AppointmentDonation, new Lifestream.Contracts.Appointments.RecordDonationRequest(1, null), appointment.Id)).Dispose();

        using var response = await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id);
        using var problem = await response.Problem(409, ProblemCodes.Conflict);
        var text = problem.RootElement.GetRawText();
        Assert.DoesNotContain("FK_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Receipt", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_Delete_Unreferenced_Works_AndTheirSessionStops()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        Assert.Equal(HttpStatusCode.OK, (await worker.Client.GetApi(ApiRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteApi(ApiRoutes.StaffById, worker.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await worker.Client.GetApi(ApiRoutes.Me)).StatusCode);
    }

    // ---- admin reset password (API-34) ----

    [Fact]
    public async Task ResetPassword_SetsANewTemporaryValue_StoredAsHash_EndsSessions_ClearsLock()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        await fx.Db.ExecuteAsync($"UPDATE Staff SET LockoutEndUtc = DATEADD(hour, 1, SYSUTCDATETIME()), FailedLoginCount = 3 WHERE Id = {worker.Id}", Ct);
        var oldHash = await fx.Db.ScalarAsync<string>($"SELECT PasswordHash FROM Staff WHERE Id = {worker.Id}", Ct);

        using var response = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, id: worker.Id);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var reset = JsonSerializer.Deserialize<ResetPasswordResponse>(text, ApiFixture.Json)!;
        Assert.Equal(16, reset.TemporaryPassword.Length);
        var newHash = await fx.Db.ScalarAsync<string>($"SELECT PasswordHash FROM Staff WHERE Id = {worker.Id}", Ct);
        Assert.NotEqual(oldHash, newHash);
        Assert.DoesNotContain(newHash, text, StringComparison.Ordinal);
        Assert.DoesNotContain(oldHash, text, StringComparison.Ordinal);
        Assert.NotEqual(reset.TemporaryPassword, newHash);
        Assert.True(PasswordHashing.Verify(newHash, reset.TemporaryPassword));
        Assert.False(PasswordHashing.Verify(newHash, ApiFixture.Password));

        using var anonymous = fx.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, reset.TemporaryPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await worker.Client.GetApi(ApiRoutes.Me)).StatusCode);

        // Two resets never give the same value.
        var second = await (await admin.Client.PostApi(ApiRoutes.StaffResetPassword, id: worker.Id)).Read<ResetPasswordResponse>();
        Assert.NotEqual(reset.TemporaryPassword, second.TemporaryPassword);
    }

    [Fact]
    public async Task ResetPassword_OfTheOwner_ByAnotherAdmin_403()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var owner = await fx.AddStaffAsync(Role.Admin, change: s => s.IsProtected = true);
        using var response = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, id: owner.Id);
        using var problem = await response.Problem(403, "forbidden");
        using var edit = await admin.Client.PatchRaw(ApiRoutes.StaffById, owner.Id, Json(new { rowVersion = "AAAAAAAAAAE=", displayName = "x" }));
        using var p2 = await edit.Problem(403, "forbidden");
    }

    // ---- profile and change password ----

    [Fact]
    public async Task ChangePassword_RequiresCurrent_Min12_StoresHash_EndsOtherSessions()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        const string newPassword = "a-brand-new-password-1";

        using var wrongCurrent = await user.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest("not-the-password", newPassword));
        using var p1 = await wrongCurrent.Problem(400, ProblemCodes.Validation);
        Assert.True(p1.RootElement.GetProperty("errors").TryGetProperty("currentPassword", out _));
        using var tooShort = await user.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(ApiFixture.Password, "short-11-ch"));
        using var p2 = await tooShort.Problem(400, ProblemCodes.Validation);
        Assert.True(p2.RootElement.GetProperty("errors").TryGetProperty("newPassword", out _));
        using var same = await user.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(ApiFixture.Password, ApiFixture.Password));
        using var p3 = await same.Problem(400, ProblemCodes.Validation);
        using var empty = await user.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest("", ""));
        using var p4 = await empty.Problem(400, ProblemCodes.Validation);

        // The same password still works after all the failed attempts that were rejected before verification.
        using var anonymous = fx.NewClient();
        var login = await (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, ApiFixture.Password))).Read<TokenResponse>();
        using var signedIn = fx.NewClient(login.AccessToken);
        using var ok = await signedIn.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(ApiFixture.Password, newPassword));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var hash = await fx.Db.ScalarAsync<string>($"SELECT PasswordHash FROM Staff WHERE Id = {user.Id}", Ct);
        Assert.True(PasswordHashing.Verify(hash, newPassword));
        Assert.False(PasswordHashing.Verify(hash, ApiFixture.Password));

        // The session that changed it keeps working; every other session of this person ended.
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetApi(ApiRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetApi(ApiRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, ApiFixture.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, newPassword))).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_FiveWrongCurrentPasswords_Lock_SixthWithTheRightOneIs429_AndTheSessionIsEnded()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var anonymous = fx.NewClient();
        var login = await (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, ApiFixture.Password))).Read<TokenResponse>();
        using var signedIn = fx.NewClient(login.AccessToken);

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await signedIn.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest("wrong-password-" + i, "another-new-password-1"));
            if (i < 4)
            {
                using var p = await wrong.Problem(400, ProblemCodes.Validation);
            }
        }

        // The lock has started and the caller was signed out (the access token and the refresh token both stop).
        using var refresh = await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        using var after = await signedIn.GetApi(ApiRoutes.Me);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // A fresh session is refused at login while locked, and so is a change-password attempt with the right password:
        // mint a new session row directly (the lock only blocks password checks).
        using var locked = await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, ApiFixture.Password));
        using var lockedProblem = await locked.Problem(429, ProblemCodes.LockedOut);
        await using var db = fx.Db.NewContext();
        var staff = await db.Staff.AsNoTracking().SingleAsync(s => s.Id == user.Id, Ct);
        var token = await fx.SessionAsync(db, staff.Id, staff.DisplayName, Role.Staff);
        using var viaNewSession = fx.NewClient(token);
        using var right = await viaNewSession.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(ApiFixture.Password, "another-new-password-1"));
        using var rightProblem = await right.Problem(429, ProblemCodes.LockedOut);
        Assert.True(right.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Profile_ReadAndEditOwn_NoImageBytes_NoCredentials()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var response = await user.Client.GetApi(ApiRoutes.Me);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("assword", text, StringComparison.Ordinal);
        var mine = JsonSerializer.Deserialize<ProfileResponse>(text, ApiFixture.Json)!;
        Assert.Equal(user.Id, mine.StaffId);
        var updated = await (await user.Client.PatchMe(new { rowVersion = mine.RowVersion, phone = "+1-555-0188" })).Read<ProfileResponse>();
        Assert.Equal("+1-555-0188", updated.Phone);
        Assert.Equal(mine.DisplayName, updated.DisplayName);
        using var bad = await user.Client.PatchMe(new { rowVersion = updated.RowVersion, displayName = "" });
        using var p = await bad.Problem(400, ProblemCodes.Validation);
        using var stale = await user.Client.PatchMe(new { rowVersion = mine.RowVersion, displayName = "Late Write" });
        using var p2 = await stale.Problem(409, ProblemCodes.Concurrency);
    }

    [Fact]
    public async Task SecurityRelevantWrites_FailAsErrorResponses_NeverASilentOk()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        var admin = await fx.AddStaffAsync(Role.Admin);
        foreach (var response in new[]
        {
            await user.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest("nope", "x")),
            await user.Client.PatchMe(new { rowVersion = "bad", displayName = "x" }),
            await admin.Client.PatchRaw(ApiRoutes.StaffById, user.Id, Json(new { rowVersion = "AAAAAAAAAAE=", displayName = "x" })),
            await admin.Client.PostRaw(ApiRoutes.Staff, "{}"),
        })
        {
            Assert.True((int)response.StatusCode is >= 400 and < 600, $"a failed security write returned {(int)response.StatusCode}");
            using var problem = await response.Problem((int)response.StatusCode);
        }
    }

    // ---- photos (API-31, 32) ----

    [Fact]
    public async Task PhotoUpload_Reencodes256Under30KB_ServedAsJpegOnly()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var upload = TestImages.Jpeg(700, 500, 70);
        Assert.InRange(upload.Length, 31_000, 1_000_000);

        using var put = await admin.Client.PutBytes(ApiRoutes.StaffPhoto, worker.Id, upload, "image/jpeg");
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var get = await admin.Client.GetApi(ApiRoutes.StaffPhoto, worker.Id);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/jpeg", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        var bytes = await get.Content.ReadAsByteArrayAsync(Ct);
        Assert.InRange(bytes.Length, 1, 30 * 1024);
        using var decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.True(decoded.Width <= 256 && decoded.Height <= 256, $"{decoded.Width}x{decoded.Height}");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        var etag = get.Headers.ETag!.ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.StaffPhoto, worker.Id));
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        using var cached = await admin.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);

        var detail = await (await admin.Client.GetApi(ApiRoutes.StaffById, worker.Id)).Read<StaffDetail>();
        Assert.True(detail.HasPhoto);
    }

    [Fact]
    public async Task PhotoUpload_PngWithTransparency_BecomesAnOpaqueJpeg()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var put = await user.Client.PutBytes(ApiRoutes.MePhoto, null, TestImages.Png(120, 120), "image/png");
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using var get = await user.Client.GetApi(ApiRoutes.StaffPhoto, user.Id);
        Assert.Equal("image/jpeg", get.Content.Headers.ContentType!.MediaType);
        var bytes = await get.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(0xFF, bytes[0]);
    }

    [Fact]
    public async Task PhotoUpload_Over1MB_413_WrongType_415_NotAnImage_400()
    {
        var user = await fx.AddStaffAsync(Role.Staff);
        using var big = await user.Client.PutBytes(ApiRoutes.MePhoto, null, new byte[1_100_000], "image/png");
        using var p1 = await big.Problem(413, "payload_too_large");

        foreach (var type in new[] { "text/html", "image/svg+xml", "application/x-msdownload", "application/octet-stream", "image/gif" })
        {
            using var wrongType = await user.Client.PutBytes(ApiRoutes.MePhoto, null, TestImages.Png(40, 40), type);
            using var p = await wrongType.Problem(415, ProblemCodes.Validation);
        }

        // The declared type is not trusted: HTML or an executable header sent as image/png is rejected on content.
        using var html = await user.Client.PutBytes(ApiRoutes.MePhoto, null, System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "image/png");
        using var p2 = await html.Problem(400, ProblemCodes.Validation);
        using var exe = await user.Client.PutBytes(ApiRoutes.MePhoto, null, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00], "image/jpeg");
        using var p3 = await exe.Problem(400, ProblemCodes.Validation);
        using var empty = await user.Client.PutBytes(ApiRoutes.MePhoto, null, [], "image/jpeg");
        using var p4 = await empty.Problem(400, ProblemCodes.Validation);

        using var none = await user.Client.GetApi(ApiRoutes.StaffPhoto, user.Id);
        using var p5 = await none.Problem(404, ProblemCodes.NotFound);
    }

    [Fact]
    public async Task Photo_OfAnotherPerson_NotReadableByPlainStaff()
    {
        var one = await fx.AddStaffAsync(Role.Staff);
        var two = await fx.AddStaffAsync(Role.Staff);
        using var response = await one.Client.GetApi(ApiRoutes.StaffPhoto, two.Id);
        using var problem = await response.Problem(403, "forbidden");
    }

    // ---- no credential ever leaves the API (API-30, 36) ----

    [Fact]
    public async Task NoResponse_ContainsPasswordHash()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var demo = await fx.AddDemoAsync();
        var donor = await Seed.DonorAsync(fx);
        var patient = await Seed.PatientAsync(fx);
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(90, 10));
        var request = await Seed.RequestAsync(fx, patient, 1);
        var receipt = await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddHours(-1));
        var hash = await fx.Db.ScalarAsync<string>($"SELECT PasswordHash FROM Staff WHERE Id = {worker.Id}", Ct);

        var gets = new (string Template, int? Id)[]
        {
            (ApiRoutes.Staff, null), (ApiRoutes.StaffById, worker.Id), (ApiRoutes.Me, null), (ApiRoutes.Donors, null), (ApiRoutes.DonorById, donor.Id),
            (ApiRoutes.Patients, null), (ApiRoutes.PatientById, patient.Id), (ApiRoutes.Appointments, null), (ApiRoutes.AppointmentById, appointment.Id),
            (ApiRoutes.Requests, null), (ApiRoutes.RequestById, request.Id), (ApiRoutes.Receipts, null), (ApiRoutes.ReceiptById, receipt.Id),
            (ApiRoutes.Stock, null), (ApiRoutes.Dashboard, null), (ApiRoutes.LookupStaff, null), (ApiRoutes.LookupDonors, null), (ApiRoutes.LookupPatients, null),
            (ApiRoutes.LookupAppointments, null), (ApiRoutes.LookupRequests, null), (ApiRoutes.LookupEnums, null),
        };
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            foreach (var client in new[] { admin.Client, demo.Client })
            {
                foreach (var (template, id) in gets)
                {
                    using var response = id is { } i ? await client.GetApi(template, i) : await client.GetApi(template, "?pageSize=200");
                    var text = await response.Content.ReadAsStringAsync(Ct);
                    Assert.DoesNotContain(hash, text, StringComparison.Ordinal);
                    Assert.DoesNotContain("AQAAAAI", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Writes that return a staff resource do not carry it either.
            using var created = await admin.Client.PostRaw(ApiRoutes.Staff, Json(NewStaff(fx.NextName("leak"))));
            Assert.DoesNotContain("AQAAAAI", await created.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            using var login = await fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, ApiFixture.Password));
            Assert.DoesNotContain("AQAAAAI", await login.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        // No query behind a read endpoint touches the credential column or uses SELECT *.
        var reads = fx.Commands.Commands.Where(c => !c.Contains("INSERT", StringComparison.Ordinal) && !c.Contains("UPDATE", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(reads, c => c.Contains("SELECT *", StringComparison.OrdinalIgnoreCase));
        var login2 = reads.Count(c => c.Contains("PasswordHash", StringComparison.Ordinal));
        Assert.Equal(1, login2);
    }
}

public sealed class PasswordRouteRateLimitTests(LowLimitApiFixture fx) : IClassFixture<LowLimitApiFixture>
{
    [Fact]
    public async Task ChangePasswordRoute_IsRateLimitedPerIp()
    {
        var user = await fx.AddStaffAsync(Role.Staff, ip: "203.0.113.77");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await user.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest("wrong-password-" + i, "another-new-password-1"));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.BadRequest));
    }
}
