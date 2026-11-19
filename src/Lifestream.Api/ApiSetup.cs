using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Features;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Health;
using Lifestream.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lifestream.Api;

/// <summary>Opens one connection for the readiness probe. Replaceable so tests can simulate a database that is resuming.</summary>
public interface IDatabaseProbe
{
    Task CheckAsync(CancellationToken ct);
}

internal sealed class SqlDatabaseProbe(IConfiguration configuration, IOptions<DatabaseSettings> settings) : IDatabaseProbe
{
    public async Task CheckAsync(CancellationToken ct)
    {
        var text = configuration.GetConnectionString("Lifestream") ?? throw new InvalidOperationException("ConnectionStrings:Lifestream is not configured.");
        var builder = new SqlConnectionStringBuilder(text) { ConnectTimeout = Math.Max(1, settings.Value.ReadinessTimeoutSeconds) };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(ct);
    }
}

public static class ApiSetup
{
    private static readonly string[] DefaultTrustedNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "127.0.0.0/8", "::1/128"];

    public static WebApplicationBuilder AddLifestreamApi(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;

        // All settings are bound lazily (IOptions), so configuration supplied by a test host or the environment is always seen.
        services.Configure<JwtSettings>(config.GetSection(JwtSettings.Section));
        services.Configure<AuthSettings>(config.GetSection(AuthSettings.Section));
        services.Configure<DemoSettings>(config.GetSection(DemoSettings.Section));
        services.Configure<ClinicSettings>(config.GetSection(ClinicSettings.Section));
        services.Configure<StockSettings>(config.GetSection(StockSettings.Section));
        services.Configure<DashboardSettings>(config.GetSection(DashboardSettings.Section));
        services.Configure<DatabaseSettings>(config.GetSection(DatabaseSettings.Section));
        services.Configure<LimitSettings>(config.GetSection(LimitSettings.Section));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DatabaseSettings>>().Value);

        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        services.ConfigureHttpJsonOptions(o =>
        {
            var contract = ContractJson.CreateOptions();
            o.SerializerOptions.TypeInfoResolver = contract.TypeInfoResolver;
            // The contract's lenient enum converter (numbers, flag lists) is replaced by the strict one: names only.
            foreach (var converter in contract.Converters.Where(c => c is not System.Text.Json.Serialization.JsonStringEnumConverter))
            {
                o.SerializerOptions.Converters.Add(converter);
            }

            o.SerializerOptions.Converters.Add(new StrictEnumConverterFactory());

            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });

        // Only a proxy on a trusted network may set X-Forwarded-For; anything else is judged by its real address.
        // ForwardedHeaders:KnownNetworks (CIDR list) defaults to the private ranges (the Container Apps ingress hop comes from one) plus loopback.
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((o, configuration) =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            var networks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() is { Length: > 0 } configured
                ? configured
                : DefaultTrustedNetworks;
            foreach (var network in networks)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        services.AddResponseCompression(o => o.EnableForHttps = true);

        services.AddDbContext<LifestreamDbContext>((sp, options) =>
        {
            var connection = sp.GetRequiredService<IConfiguration>().GetConnectionString("Lifestream")
                ?? throw new InvalidOperationException("ConnectionStrings:Lifestream is not configured.");
            var database = sp.GetRequiredService<DatabaseSettings>();
            options.UseSqlServer(connection, sql => sql.ExecutionStrategy(d => new WakeAwareExecutionStrategy(d, database)));
            options.AddInterceptors(sp.GetServices<IInterceptor>());
            options.AddInterceptors(new SearchPlanInterceptor());
        });
        services.AddSingleton<IDatabaseProbe, SqlDatabaseProbe>();

        services.AddSingleton<TokenService>();
        services.AddSingleton<DemoDirectory>();
        services.AddScoped<UnknownLoginStore>();
        services.AddScoped<DemoSessionDenylist>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<AuthService>();
        services.AddScoped<StaffService>();
        services.AddSingleton<PhotoProcessor>();
        services.AddSingleton<ClinicSchedule>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService, IMemoryCache, TimeProvider>((o, tokens, cache, time) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = tokens.AccessParameters;
                o.Events = new JwtBearerEvents
                {
                    // Server-side revocation: a logged-out or rotated-away session stops working at once, not when the token expires.
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        if (principal.HasClaim(Claims.Demo, "1"))
                        {
                            // A demo session has no refresh-token row; its sign-out is remembered in the DemoRevocation table.
                            var demoFamily = Guid.TryParseExact(principal.FindFirst(Claims.Family)?.Value, "N", out var df) ? df : Guid.Empty;
                            if (demoFamily == Guid.Empty || await context.HttpContext.RequestServices.GetRequiredService<DemoSessionDenylist>().IsRevokedAsync(demoFamily, context.HttpContext.RequestAborted))
                            {
                                context.Fail("The session has ended.");
                            }

                            return;
                        }

                        var db = context.HttpContext.RequestServices.GetRequiredService<LifestreamDbContext>();
                        var family = Guid.TryParseExact(principal.FindFirst(Claims.Family)?.Value, "N", out var f) ? f : Guid.Empty;
                        var state = family == Guid.Empty ? null : await RevocationCache.StateAsync(cache, db, time, family, context.HttpContext.RequestAborted);
                        if (state is not { Active: true })
                        {
                            context.Fail("The session has ended.");
                            return;
                        }

                        // The database decides the role, whatever the token says.
                        if (principal.Identity is System.Security.Claims.ClaimsIdentity identity && !principal.HasClaim(Claims.Role, state.Role.ToString()))
                        {
                            foreach (var stale in identity.FindAll(Claims.Role).ToList())
                            {
                                identity.RemoveClaim(stale);
                            }

                            identity.AddClaim(new System.Security.Claims.Claim(Claims.Role, state.Role.ToString()));
                        }

                        // Whether an administrator-set password still has to be replaced is also the database's call, on every request.
                        if (state.MustChangePassword && principal.Identity is System.Security.Claims.ClaimsIdentity mustChange)
                        {
                            mustChange.AddClaim(new System.Security.Claims.Claim(Claims.MustChangePassword, "1"));
                        }
                    },
                };
            });
        services.AddAuthorization(PolicyNames.Register);
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationHandler>();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<LimitSettings>>((o, limits) => RateLimitPolicies.Configure(o, limits.Value));

        services.AddLogging(l => l.AddJsonConsole(o => o.IncludeScopes = true));
        return builder;
    }

    public static WebApplication UseLifestreamApi(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseMiddleware<ProblemMiddleware>();
        app.UseResponseCompression();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseMiddleware<BodyLimitMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<PasswordChangeRequiredMiddleware>();
        app.UseMiddleware<IdentityMiddleware>();
        app.UseMiddleware<DemoWriteCapMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        return app;
    }

    public static WebApplication MapLifestreamEndpoints(this WebApplication app)
    {
        // Liveness only: never opens a database connection (the startup probe and the client warm-up call it).
        app.MapGet(ApiRoutes.Healthz, () => Results.Ok(new { status = "healthy" }));

        // Readiness: the only health route that touches the database; "waking" and "resting" are distinct, visible states. Not ready is
        // an error like any other: RFC 7807 problem details with a code and the correlation id, plus the state as a "database" member.
        app.MapGet(ApiRoutes.Readyz, async (IDatabaseProbe probe, CancellationToken ct) =>
        {
            try
            {
                await probe.CheckAsync(ct);
                return Results.Ok(new ReadinessResponse(DatabaseStates.Ready));
            }
            catch (Exception exception) when (DatabaseErrors.IsResting(exception))
            {
                return new ProblemResult(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemCodes.DatabaseResting,
                    DatabaseProblems.RestingTitle,
                    DatabaseProblems.RestingRetryAfter(),
                    new Dictionary<string, object?> { ["database"] = DatabaseStates.Resting });
            }
            catch (Exception exception) when (DatabaseErrors.IsWaking(exception) || exception is SqlException)
            {
                return new ProblemResult(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemCodes.DatabaseWaking,
                    DatabaseProblems.WakingTitle,
                    DatabaseProblems.WakingRetryAfter,
                    new Dictionary<string, object?> { ["database"] = DatabaseStates.Waking });
            }
        });

        var api = app.MapGroup(ApiRoutes.Prefix).RequireAuthorization(PolicyNames.Staff);
        EnumQueryFilter.AddTo(api);
        AuthEndpoints.Map(api);
        DonorEndpoints.Map(api);
        PatientEndpoints.Map(api);
        AppointmentEndpoints.Map(api);
        RequestEndpoints.Map(api);
        ReceiptEndpoints.Map(api);
        StockEndpoints.Map(api);
        DashboardEndpoints.Map(api);
        StaffEndpoints.Map(api);
        ProfileEndpoints.Map(api);
        LookupEndpoints.Map(api);
        DemoEndpoints.Map(api);
        return app;
    }
}
