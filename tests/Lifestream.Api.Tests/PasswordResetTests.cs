using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>API-30, API-34: an administrator's reset never returns a password, and the person must replace the temporary one before anything else is allowed.</summary>
public sealed partial class PasswordResetTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private const string Temporary = "typed-temporary-pass-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("password|temporary|hash|secret", RegexOptions.IgnoreCase)]
    private static partial Regex Sensitive();

    private async Task<TestUser> ResetAsync(TestUser admin, Role role = Role.Staff)
    {
        var worker = await fx.AddStaffAsync(role);
        using var reset = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest(Temporary), worker.Id);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        return worker;
    }

    private async Task<TokenResponse> SignInWithTemporaryAsync(string username)
    {
        using var anonymous = fx.NewClient();
        using var login = await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(username, Temporary));
        return await login.Read<TokenResponse>();
    }

    [Theory]
    [InlineData("short-11-ch")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResetPassword_TemporaryPasswordUnder12Characters_Is400_AndNothingChanges(string temporary)
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var before = await fx.Db.ScalarAsync<string>($"SELECT PasswordHash FROM StaffCredential WHERE StaffId = {worker.Id}", Ct);

        using var response = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest(temporary), worker.Id);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("temporaryPassword", out _));
        Assert.Equal(before, await fx.Db.ScalarAsync<string>($"SELECT PasswordHash FROM StaffCredential WHERE StaffId = {worker.Id}", Ct));
        Assert.Equal(HttpStatusCode.OK, (await worker.Client.GetApi(ApiRoutes.Me)).StatusCode);
    }

    [Fact]
    public async Task ResetPassword_WithoutABody_Is400()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        using var response = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, null, worker.Id);
        using var problem = await response.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task ResetPassword_TheResponseCarriesNoPasswordAtAll_ForAdminOrAnyoneElse()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        using var response = await admin.Client.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest(Temporary), worker.Id);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(body);
        Assert.DoesNotContain(Temporary, string.Join(' ', response.Headers.SelectMany(h => h.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAReset_SignInSaysMustChangePassword_EveryEndpointButAuthMeAndPasswordIs403_ThenChangingItOpensEverything()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await ResetAsync(admin);

        var tokens = await SignInWithTemporaryAsync(worker.Username);
        Assert.True(tokens.User.MustChangePassword);
        using var client = fx.NewClient(tokens.AccessToken);

        foreach (var route in new[] { ApiRoutes.Donors, ApiRoutes.Patients, ApiRoutes.Appointments, ApiRoutes.Requests, ApiRoutes.Receipts, ApiRoutes.Stock, ApiRoutes.Dashboard, ApiRoutes.LookupDonors, ApiRoutes.LookupEnums })
        {
            using var blocked = await client.GetApi(route);
            using var problem = await blocked.Problem(403, "password_change_required");
        }

        // What is needed to fix it stays open: read the profile, change the password, refresh the session, sign out.
        Assert.Equal(HttpStatusCode.OK, (await client.GetApi(ApiRoutes.Me)).StatusCode);
        using var anonymous = fx.NewClient();
        var refreshed = await (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(tokens.RefreshToken))).Read<TokenResponse>();
        Assert.True(refreshed.User.MustChangePassword);
        using var client2 = fx.NewClient(refreshed.AccessToken);
        using var wrongCurrent = await client2.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest("not-the-password", TestSecrets.BrandNew));
        using var p = await wrongCurrent.Problem(400, ProblemCodes.Validation);
        using var changed = await client2.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(Temporary, TestSecrets.BrandNew));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(0, await fx.Db.ScalarAsync<int>($"SELECT MustChangePassword FROM StaffCredential WHERE StaffId = {worker.Id}", Ct));

        // The same session now works everywhere, and a new sign-in no longer says it.
        Assert.Equal(HttpStatusCode.OK, (await client2.GetApi(ApiRoutes.Donors)).StatusCode);
        var fresh = await (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, TestSecrets.BrandNew))).Read<TokenResponse>();
        Assert.False(fresh.User.MustChangePassword);
    }

    [Fact]
    public async Task WhileMustChangePassword_EveryMappedEndpointExceptTheThreeAllowedOnesAnswers403PasswordChangeRequired()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await ResetAsync(admin, Role.Admin);
        var tokens = await SignInWithTemporaryAsync(worker.Username);
        using var client = fx.NewClient(tokens.AccessToken);

        var endpoints = fx.Factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("api/v1/", StringComparison.Ordinal) || e.RoutePattern.RawText.StartsWith("/api/v1/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (Pattern: e.RoutePattern.RawText!, Method: m)))
            .ToList();
        Assert.True(endpoints.Count > 40, $"only {endpoints.Count} endpoints found");

        var allowed = new[] { "GET api/v1/me", "PUT api/v1/me/password", "POST api/v1/auth/login", "POST api/v1/auth/demo", "POST api/v1/auth/refresh", "POST api/v1/auth/logout" };
        var checkedCount = 0;
        foreach (var (pattern, method) in endpoints)
        {
            var key = $"{method} {pattern.TrimStart('/')}";
            if (allowed.Contains(key) || pattern.EndsWith("demo/reset", StringComparison.Ordinal))
            {
                // demo/reset is DemoOnly: the policy refuses an administrator before this rule is reached.
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), "/" + pattern.TrimStart('/').Replace("{id}", "1", StringComparison.Ordinal))
            {
                Content = method is "GET" or "DELETE" ? null : new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(request, Ct);
            using var problem = await response.Problem(403, "password_change_required");
            checkedCount++;
        }

        Assert.True(checkedCount > 30, $"only {checkedCount} endpoints were checked");
    }

    [Fact]
    public async Task NoResponseOfAnyEndpoint_HasAFieldNamedLikePasswordOrTemporaryWithAValue()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var worker = await fx.AddStaffAsync(Role.Staff);
        var demo = await fx.AddDemoAsync();
        var donor = await Seed.DonorAsync(fx);
        var patient = await Seed.PatientAsync(fx);
        var appointment = await Seed.AppointmentAsync(fx, donor, Seed.Slot(90, 10));
        var request = await Seed.RequestAsync(fx, patient, 1);
        var receipt = await Seed.ReceiptAsync(fx, ReceiptKind.Donation, DateTime.UtcNow.AddHours(-1));

        var responses = new List<(string Label, HttpResponseMessage Response)>();
        foreach (var client in new[] { admin.Client, demo.Client })
        {
            foreach (var (template, id) in new (string, int?)[]
            {
                (ApiRoutes.Staff, null), (ApiRoutes.StaffById, worker.Id), (ApiRoutes.Me, null), (ApiRoutes.Donors, null), (ApiRoutes.DonorById, donor.Id),
                (ApiRoutes.DonorEligibility, donor.Id), (ApiRoutes.Patients, null), (ApiRoutes.PatientById, patient.Id), (ApiRoutes.Appointments, null),
                (ApiRoutes.AppointmentById, appointment.Id), (ApiRoutes.Requests, null), (ApiRoutes.RequestById, request.Id), (ApiRoutes.Receipts, null),
                (ApiRoutes.ReceiptById, receipt.Id), (ApiRoutes.Stock, null), (ApiRoutes.Dashboard, null), (ApiRoutes.LookupStaff, null),
                (ApiRoutes.LookupDonors, null), (ApiRoutes.LookupPatients, null), (ApiRoutes.LookupAppointments, null), (ApiRoutes.LookupRequests, null),
                (ApiRoutes.LookupEnums, null),
            })
            {
                responses.Add((template, id is { } i ? await client.GetApi(template, i) : await client.GetApi(template)));
            }
        }

        using var anonymous = fx.NewClient();
        responses.Add(("login", await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, ApiFixture.Password))));
        responses.Add(("demo", await anonymous.PostApi(ApiRoutes.AuthDemo)));
        var tokens = await (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(worker.Username, ApiFixture.Password))).Read<TokenResponse>();
        responses.Add(("refresh", await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(tokens.RefreshToken))));
        responses.Add(("create staff", await admin.Client.PostRaw(ApiRoutes.Staff, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["displayName"] = "Sweep Person", ["username"] = fx.NextName("sweep"), ["gender"] = "Female", ["dateOfBirth"] = "1992-03-04", ["designation"] = "Doctor",
            ["shift"] = "Morning", ["role"] = "Staff", ["salary"] = 51000, ["phone"] = "+1-555-0150", ["initialPassword"] = TestSecrets.Initial,
        }))));
        responses.Add(("reset", await admin.Client.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest(Temporary), worker.Id)));
        var me = await (await admin.Client.GetApi(ApiRoutes.Me)).Read<ProfileResponse>();
        responses.Add(("patch me", await admin.Client.PatchMe(new UpdateProfileRequest(me.RowVersion, DisplayName: "Sweep Admin"))));
        responses.Add(("change password", await admin.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(ApiFixture.Password, TestSecrets.BrandNew))));
        responses.Add(("photo", await admin.Client.PutBytes(ApiRoutes.StaffPhoto, worker.Id, TestImages.Png(100, 100), "image/png")));

        var offenders = new List<string>();
        foreach (var (label, response) in responses)
        {
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(Ct);
                if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true && text.Length > 0)
                {
                    Walk(label, JsonElement.Parse(text), offenders);
                }
            }
        }

        Assert.True(responses.Count > 30);
        Assert.True(offenders.Count == 0, "fields named like a password with a value: " + string.Join(", ", offenders));
    }

    private static void Walk(string path, JsonElement element, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // A true/false flag (mustChangePassword) says nothing a password could; text, numbers and nested values are what would leak one.
                    if (Sensitive().IsMatch(property.Name) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Object or JsonValueKind.Array && property.Value.ToString().Length > 0)
                    {
                        offenders.Add($"{path}.{property.Name}");
                    }

                    Walk($"{path}.{property.Name}", property.Value, offenders);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(path + "[]", item, offenders);
                }

                break;
        }
    }
}
