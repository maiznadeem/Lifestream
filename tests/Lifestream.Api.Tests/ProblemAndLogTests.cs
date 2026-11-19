using System.Net;
using System.Text.Json;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;

namespace Lifestream.Api.Tests;

public sealed class LoggingApiFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings) => settings["Logging:LogLevel:Default"] = "Information";
}

/// <summary>RFC 7807 everywhere, a correlation id on every response, and logs without secrets (API-12, 43).</summary>
public sealed class ProblemTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Forbidden =
    [
        "Exception", "System.", "Microsoft.", "SqlClient", "EntityFramework", "   at ", "StackTrace",
        "[Donor]", "[Staff]", "[Receipt]", "FK_", "PK_", "IX_", "CK_", "UNIQUE", "SELECT", "INSERT", "localhost", "Lifestream_Test_",
    ];

    [Fact]
    public async Task Errors_AreProblemDetails_WithCorrelationId_NoExceptionText()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var donor = await Seed.DonorAsync(fx);
        await Seed.AppointmentAsync(fx, donor, Seed.Slot(100, 10));
        using var anonymous = fx.NewClient();

        var responses = new List<(string Name, HttpResponseMessage Response, int Status, string? Code)>
        {
            ("validation", await admin.Client.PostRaw(ApiRoutes.Donors, "{\"name\":\"\"}"), 400, "validation"),
            ("bad json", await admin.Client.PostRaw(ApiRoutes.Donors, "{{{"), 400, "validation"),
            ("unauthenticated", await anonymous.GetApi(ApiRoutes.Donors), 401, "unauthorized"),
            ("login", await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest("nobody", "wrong-password-1")), 401, "invalid_credentials"),
            ("forbidden", await (await fx.AddStaffAsync(Role.Staff)).Client.GetApi(ApiRoutes.Staff), 403, "forbidden"),
            ("missing entity", await admin.Client.GetApi(ApiRoutes.DonorById, 987654), 404, "not_found"),
            ("missing route", await admin.Client.GetAsync("/api/v1/nothing-here", Ct), 404, "not_found"),
            ("wrong method", await admin.Client.PostApi(ApiRoutes.ReceiptById, new { }, 1), 405, "method_not_allowed"),
            ("conflict", await admin.Client.DeleteApi(ApiRoutes.DonorById, donor.Id), 409, "conflict"),
            ("bad enum in query", await admin.Client.GetApi(ApiRoutes.Donors, "?bloodType=Nope"), 400, null),
            ("bad sort", await admin.Client.GetApi(ApiRoutes.Donors, "?sort=password"), 400, "validation"),
        };

        foreach (var (name, response, status, code) in responses)
        {
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(Ct);
                Assert.True((int)response.StatusCode == status, $"{name}: expected {status} got {(int)response.StatusCode}: {text}");
                Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                Assert.Equal(status, root.GetProperty("status").GetInt32());
                Assert.False(string.IsNullOrEmpty(root.GetProperty("title").GetString()));
                Assert.False(string.IsNullOrEmpty(root.GetProperty("type").GetString()));
                var codeValue = root.GetProperty("code").GetString();
                Assert.False(string.IsNullOrEmpty(codeValue));
                if (code is not null)
                {
                    Assert.Equal(code, codeValue);
                }

                // The correlation id in the body is the one in the header.
                Assert.Equal(response.Headers.GetValues(ApiHeaders.CorrelationId).Single(), root.GetProperty("correlationId").GetString());
                foreach (var word in Forbidden)
                {
                    Assert.DoesNotContain(word, text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task CorrelationId_IsEchoedWhenSafe_AndReplacedWhenNot()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var good = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.DonorById, 987654));
        good.Headers.Add(ApiHeaders.CorrelationId, "trace-12345_abc");
        using var goodResponse = await admin.Client.SendAsync(good, Ct);
        Assert.Equal("trace-12345_abc", goodResponse.Headers.GetValues(ApiHeaders.CorrelationId).Single());
        using var doc = await goodResponse.Problem(404);
        Assert.Equal("trace-12345_abc", doc.RootElement.GetProperty("correlationId").GetString());

        using var bad = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.DonorById, 987654));
        bad.Headers.Add(ApiHeaders.CorrelationId, "<script>alert(1)</script>");
        using var badResponse = await admin.Client.SendAsync(bad, Ct);
        var replaced = badResponse.Headers.GetValues(ApiHeaders.CorrelationId).Single();
        Assert.DoesNotContain("<", replaced, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{32}$", replaced);
    }

    [Fact]
    public async Task Post_WithRepeatedIdempotencyKey_ReturnsOriginalResult()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var key = Guid.NewGuid().ToString();
        var body = new { name = "Idempotent Donor", dateOfBirth = "1988-08-08", gender = "Male", bloodType = "APos", phone = "+1-555-0111", address = "x", frequency = "Regular" };

        using var first = await admin.Client.PostApi(ApiRoutes.Donors, body, key: key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstText = await first.Content.ReadAsStringAsync(Ct);

        using var retry = await admin.Client.PostApi(ApiRoutes.Donors, body, key: key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(firstText, await retry.Content.ReadAsStringAsync(Ct));
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(first.Headers.Location, retry.Headers.Location);

        // Only one donor was created, however often the same key came in.
        Assert.Equal(1, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor WHERE Name = 'Idempotent Donor'", Ct));

        // A different key is a different request.
        using var other = await admin.Client.PostApi(ApiRoutes.Donors, body, key: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(2, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor WHERE Name = 'Idempotent Donor'", Ct));
    }

    [Fact]
    public async Task Post_WithRepeatedIdempotencyKey_ConcurrentDuplicates_CreateOneRecord()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var key = Guid.NewGuid().ToString();
        var body = new { name = "Concurrent Idempotent", dateOfBirth = "1988-08-08", gender = "Male", bloodType = "APos", phone = "+1-555-0112", address = "x", frequency = "Regular" };
        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var response = await admin.Client.PostApi(ApiRoutes.Donors, body, key: key);
            return response.StatusCode;
        }));
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Created, s));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor WHERE Name = 'Concurrent Idempotent'", Ct));
    }

    [Fact]
    public async Task Idempotency_KeysAreScopedPerUser()
    {
        var one = await fx.AddStaffAsync(Role.Admin);
        var two = await fx.AddStaffAsync(Role.Admin);
        var key = Guid.NewGuid().ToString();
        var body = new { name = "Scoped Key Donor", dateOfBirth = "1988-08-08", gender = "Male", bloodType = "APos", phone = "+1-555-0113", address = "x", frequency = "Regular" };
        (await one.Client.PostApi(ApiRoutes.Donors, body, key: key)).Dispose();
        (await two.Client.PostApi(ApiRoutes.Donors, body, key: key)).Dispose();
        Assert.Equal(2, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor WHERE Name = 'Scoped Key Donor'", Ct));
    }
}

public sealed class LogTests(LoggingApiFixture fx) : IClassFixture<LoggingApiFixture>
{
    [Fact]
    public async Task Logs_ContainNoPasswordsOrTokens_OrPersonalData_AndCarryUserIdAndCorrelationId()
    {
        var user = await fx.AddStaffAsync(Role.Admin);
        using var anonymous = fx.NewClient();
        using var wrong = await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, "the-wrong-password-xyz"));
        var login = await (await anonymous.PostApi(ApiRoutes.AuthLogin, new LoginRequest(user.Username, ApiFixture.Password))).Read<TokenResponse>();
        using var signedIn = fx.NewClient(login.AccessToken);
        var donorName = "Logleak Zephyrina Quill";
        using var created = await signedIn.PostRaw(ApiRoutes.Donors, $"{{\"name\":\"{donorName}\",\"dateOfBirth\":\"1990-01-01\",\"gender\":\"Female\",\"bloodType\":\"APos\",\"phone\":\"+1-555-0177\",\"address\":\"77 Leak Lane\",\"frequency\":\"Regular\"}}");
        (await signedIn.GetApi(ApiRoutes.Donors, "?search=logleak")).Dispose();
        using var failing = await signedIn.GetApi(ApiRoutes.DonorById, 987654);
        (await anonymous.PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(login.RefreshToken))).Dispose();

        var lines = fx.Logs.Lines.ToList();
        Assert.NotEmpty(lines);
        var all = string.Join('\n', lines);
        foreach (var secret in new[]
        {
            ApiFixture.Password, "the-wrong-password-xyz", login.AccessToken, login.RefreshToken, "Bearer ", donorName, "Zephyrina", "+1-555-0177", "77 Leak Lane", "logleak", "AQAAAAI",
        })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }

        // The signed-in requests are logged with the numeric user id and the correlation id (structured, not in the message text).
        var userId = user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var access = lines.Where(l => l.Contains("HTTP POST", StringComparison.Ordinal) && l.Contains("donors", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(access);
        Assert.All(access, l =>
        {
            Assert.Contains($"UserId={userId}", l, StringComparison.Ordinal);
            Assert.Contains("CorrelationId=", l, StringComparison.Ordinal);
            Assert.DoesNotContain("?", l.Split('|')[0], StringComparison.Ordinal);
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
