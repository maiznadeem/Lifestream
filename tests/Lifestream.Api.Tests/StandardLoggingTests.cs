using System.Text.Json;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Tests;

/// <summary>The console line shape of OracleVMOps LOGGING.md, which Argus reads from the Container Apps log workspace.</summary>
public sealed class StandardJsonFormatterTests
{
    private static JsonElement Line(
        LogLevel level,
        EventId id,
        string category,
        string template,
        object?[] args,
        Exception? exception = null,
        Dictionary<string, object?>? scope = null,
        string part = "api")
    {
        var options = new StandardJsonFormatterOptions { Part = part };
        var formatter = new StandardJsonFormatter(new StaticOptions(options));
        var scopes = new LoggerExternalScopeProvider();
        using var pushed = scope is null ? null : scopes.Push(scope);
        var state = new FormattedValues(template, args);
        var entry = new LogEntry<FormattedValues>(level, category, id, state, exception, static (s, _) => s.ToString());
        using var writer = new StringWriter();
        formatter.Write(entry, scopes, writer);
        var text = writer.ToString();
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
        return JsonElement.Parse(text);
    }

    [Fact]
    public void ARequestLine_HasEveryStandardField_AndTheRequestFields()
    {
        var line = Line(
            LogLevel.Information,
            LogEvents.Request,
            "Lifestream.Api.Infrastructure.CorrelationMiddleware",
            "HTTP {Method} {Route} {Status} {ElapsedMs}ms user {UserId}",
            ["GET", "/api/v1/donors/:id", 404, 12L, "42"],
            scope: new() { ["CorrelationId"] = "abc123", ["Ray"] = "8a1b2c3d4e5f6a7b-LHR", ["RequestPath"] = "/api/v1/donors/7?search=Zephyrina" });

        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", line.GetProperty("time").GetString());
        Assert.Equal("info", line.GetProperty("level").GetString());
        Assert.Equal("lifestream", line.GetProperty("app").GetString());
        Assert.Equal("api", line.GetProperty("part").GetString());
        Assert.Equal("request", line.GetProperty("event").GetString());
        Assert.Equal("8a1b2c3d4e5f6a7b-LHR", line.GetProperty("ray").GetString());
        Assert.Equal("HTTP GET /api/v1/donors/:id 404 12ms user 42", line.GetProperty("msg").GetString());
        Assert.Equal("GET", line.GetProperty("method").GetString());
        Assert.Equal("/api/v1/donors/:id", line.GetProperty("route").GetString());
        Assert.Equal(404, line.GetProperty("status").GetInt32());
        Assert.Equal(12, line.GetProperty("ms").GetInt64());
        Assert.Equal("42", line.GetProperty("user").GetString());
        Assert.Equal("abc123", line.GetProperty("correlation").GetString());

        // Only allow-listed fields: ASP.NET's RequestPath scope holds the raw path and query, which can carry names.
        Assert.DoesNotContain("Zephyrina", line.GetRawText(), StringComparison.Ordinal);
        Assert.False(line.TryGetProperty("RequestPath", out _));
    }

    [Fact]
    public void AnAnonymousRequest_HasNoUser_AndALineWithoutARay_HasNoRayField()
    {
        var line = Line(
            LogLevel.Information,
            LogEvents.Request,
            "Lifestream.Api.Infrastructure.CorrelationMiddleware",
            "HTTP {Method} {Route} {Status} {ElapsedMs}ms user {UserId}",
            ["POST", "/api/v1/auth/login", 401, 3L, "anonymous"]);

        Assert.False(line.TryGetProperty("user", out _));
        Assert.False(line.TryGetProperty("ray", out _));
    }

    [Fact]
    public void AnException_IsAnErrorWithEventException_TypeAndStack_NeverItsMessage()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("secret-detail Zephyrina");
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        var line = Line(LogLevel.Error, default, "Microsoft.AspNetCore.Server.Kestrel", "Request failed", [], thrown);

        Assert.Equal("error", line.GetProperty("level").GetString());
        Assert.Equal("exception", line.GetProperty("event").GetString());
        Assert.Equal("System.InvalidOperationException", line.GetProperty("error").GetString());
        Assert.Contains("AnException_IsAnErrorWithEventException", line.GetProperty("stack").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-detail", line.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheApisOwnExceptionLine_KeepsMsgShort_AndCarriesTypeAndStackAsFields()
    {
        var line = Line(
            LogLevel.Error,
            LogEvents.Exception,
            "Lifestream.Api.Infrastructure.ProblemMiddleware",
            "Unhandled {ExceptionType} (sql {SqlNumber})",
            ["System.InvalidOperationException", null],
            scope: new() { ["StackTrace"] = "   at Lifestream.Api.Somewhere()" });

        Assert.Equal("exception", line.GetProperty("event").GetString());
        Assert.Equal("Unhandled System.InvalidOperationException (sql )", line.GetProperty("msg").GetString());
        Assert.Equal("System.InvalidOperationException", line.GetProperty("error").GetString());
        Assert.Equal("   at Lifestream.Api.Somewhere()", line.GetProperty("stack").GetString());
    }

    [Fact]
    public void TheMigrateJob_IsPartWorker_WithEventMigrate()
    {
        var line = Line(LogLevel.Information, default, "Lifestream.Migrate", "Applying migrations.", [], part: "worker");

        Assert.Equal("worker", line.GetProperty("part").GetString());
        Assert.Equal("migrate", line.GetProperty("event").GetString());
        Assert.Equal("Applying migrations.", line.GetProperty("msg").GetString());
    }

    [Theory]
    [InlineData(LogLevel.Trace, "debug")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Critical, "error")]
    public void Levels_AreTheFourStandardNames(LogLevel level, string name) => Assert.Equal(name, StandardJsonFormatter.LevelOf(level));

    [Theory]
    [InlineData("request", "Anything", "request")]
    [InlineData("startup", "Lifestream.Api", "startup")]
    [InlineData("database", "Lifestream.Api.Infrastructure.ProblemMiddleware", "database")]
    [InlineData(null, "Microsoft.Hosting.Lifetime", "startup")]
    [InlineData(null, "Lifestream.Api.Infrastructure.OriginAuthMiddleware", "origin")]
    [InlineData(null, "Lifestream.Api.Infrastructure.SignInWarmUp", "warmup")]
    [InlineData("SomethingElse", "Microsoft.AspNetCore.Routing", "log")]
    public void Events_ComeFromTheFixedList(string? name, string category, string expected) =>
        Assert.Equal(expected, StandardJsonFormatter.EventOf(new EventId(0, name), category, null));

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("INFO", LogLevel.Information)]
    [InlineData(" warn ", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("verbose", null)]
    [InlineData(null, null)]
    public void LogLevel_ReadsTheFourStandardNames(string? value, LogLevel? expected) =>
        Assert.Equal(expected, StandardLogging.LevelFromEnvironment(value));

    [Theory]
    [InlineData(ApiRoutes.Healthz, 200, LogLevel.Debug)]
    [InlineData(ApiRoutes.Readyz, 503, LogLevel.Debug)]
    [InlineData("/api/v1/donors", 200, LogLevel.Information)]
    [InlineData("/api/v1/donors", 404, LogLevel.Information)]
    [InlineData("/api/v1/donors", 500, LogLevel.Error)]
    [InlineData("/api/v1/donors", 503, LogLevel.Error)]
    public void RequestLevels_HealthChecksDebug_Any5xxError(string route, int status, LogLevel expected) =>
        Assert.Equal(expected, StandardLogging.LevelFor(route, status));

    [Theory]
    [InlineData("/api/v1/donors/{id:int}", "/api/v1/donors/:id")]
    [InlineData("api/v1/receipts/{id}/count", "/api/v1/receipts/:id/count")]
    [InlineData("/files/{**rest}", "/files/:rest")]
    [InlineData("/api/v1/donors", "/api/v1/donors")]
    public void Routes_ArePatternsWithColonParameters(string pattern, string expected)
    {
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, null, null);
        Assert.Equal(expected, StandardLogging.RouteOf(endpoint));
        Assert.Equal("unmatched", StandardLogging.RouteOf(null));
    }

    [Theory]
    [InlineData("8a1b2c3d4e5f6a7b-LHR", "9999999999999999-AMS", "8a1b2c3d4e5f6a7b-LHR")] // the Worker's X-CF-Ray wins over its own call's CF-Ray
    [InlineData(null, "9999999999999999-AMS", "9999999999999999-AMS")]
    [InlineData("<script>", null, null)]
    [InlineData("<script>", "9999999999999999-AMS", "9999999999999999-AMS")]
    [InlineData(null, null, null)]
    public void TheRay_IsXCfRay_ThenCfRay_OnlyWhenItLooksLikeOne(string? xCfRay, string? cfRay, string? expected)
    {
        var context = new DefaultHttpContext();
        if (xCfRay is not null)
        {
            context.Request.Headers["X-CF-Ray"] = xCfRay;
        }

        if (cfRay is not null)
        {
            context.Request.Headers["CF-Ray"] = cfRay;
        }

        Assert.Equal(expected, StandardLogging.RayOf(context.Request));
    }

    private sealed class StaticOptions(StandardJsonFormatterOptions value) : IOptionsMonitor<StandardJsonFormatterOptions>
    {
        public StandardJsonFormatterOptions CurrentValue => value;

        public StandardJsonFormatterOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<StandardJsonFormatterOptions, string?> listener) => null;
    }

    /// <summary>The structured state LoggerMessage and the Log* extensions pass: the values by name plus the formatted message.</summary>
    private sealed class FormattedValues(string template, object?[] args) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _pairs = Names(template).Zip(args, (n, v) => new KeyValuePair<string, object?>(n, v)).ToList();

        public int Count => _pairs.Count;

        public KeyValuePair<string, object?> this[int index] => _pairs[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _pairs.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            var text = template;
            foreach (var (name, value) in _pairs)
            {
                text = text.Replace("{" + name + "}", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }

            return text;
        }

        private static IEnumerable<string> Names(string template) =>
            System.Text.RegularExpressions.Regex.Matches(template, @"\{(\w+)\}").Select(m => m.Groups[1].Value);
    }
}

/// <summary>The request line through the real pipeline: the ray is in its scope, health checks stay out at info.</summary>
public sealed class RequestLogLineTests(LoggingApiFixture fx) : IClassFixture<LoggingApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARequest_CarriesTheVisitorsRay_AndItsRoutePattern()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.DonorById, 987654));
        request.Headers.Add("X-CF-Ray", "7f00aa11bb22cc33-LHR");
        request.Headers.Add("CF-Ray", "1234567890abcdef-AMS");
        using var response = await admin.Client.SendAsync(request, Ct);

        var line = Assert.Single(fx.Logs.Lines, l => l.Contains("HTTP GET", StringComparison.Ordinal) && l.Contains("7f00aa11bb22cc33-LHR", StringComparison.Ordinal));
        Assert.StartsWith("[Information]", line, StringComparison.Ordinal);
        Assert.Contains("HTTP GET /api/v1/donors/:id 404", line, StringComparison.Ordinal);
        Assert.Contains("Ray=7f00aa11bb22cc33-LHR", line, StringComparison.Ordinal);
        Assert.DoesNotContain("987654", line.Split('|')[0], StringComparison.Ordinal); // the message has the pattern, not the id
        Assert.DoesNotContain(fx.Logs.Lines, l => l.Contains("1234567890abcdef-AMS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HealthChecks_AreDebug_SoTheyAreNotLoggedAtInfo()
    {
        using var client = fx.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Healthz);
        request.Headers.Add("X-CF-Ray", "0000healthcheck0-LHR");
        using var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();

        Assert.DoesNotContain(fx.Logs.Lines, l => l.Contains("0000healthcheck0-LHR", StringComparison.Ordinal));
    }
}
