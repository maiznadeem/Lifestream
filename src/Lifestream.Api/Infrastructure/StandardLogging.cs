using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lifestream.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Infrastructure;

/// <summary>The fixed list of <c>event</c> values (OracleVMOps LOGGING.md): the shared request, exception and startup, plus Lifestream's own.</summary>
public static class LogEvents
{
    public static readonly EventId Request = new(1, "request");
    public static readonly EventId Exception = new(2, "exception");
    public static readonly EventId Startup = new(3, "startup");
    public static readonly EventId Database = new(4, "database");

    internal static readonly HashSet<string> Named = ["request", "exception", "startup", "database", "migrate", "origin", "warmup"];
}

public sealed class StandardJsonFormatterOptions : ConsoleFormatterOptions
{
    /// <summary><c>api</c> for the web host, <c>worker</c> for the migrate job.</summary>
    public string Part { get; set; } = "api";
}

/// <summary>
/// One JSON object per line in the shape of OracleVMOps LOGGING.md, which Argus reads from the Container Apps log workspace:
/// <c>time</c>, <c>level</c>, <c>app</c>, <c>part</c>, <c>event</c>, <c>ray</c> (when there is one) and <c>msg</c>; request lines add
/// <c>method</c>, <c>route</c>, <c>status</c> and <c>ms</c>, and lines about a signed-in user add the numeric <c>user</c> id.
/// <para>
/// Only fields on an allow-list leave the process: never the raw path, a header, a body or a scope it does not know (ASP.NET's own
/// RequestPath scope holds the raw path). An exception adds its type and stack, never its message, which can carry database values.
/// </para>
/// </summary>
public sealed class StandardJsonFormatter(IOptionsMonitor<StandardJsonFormatterOptions> options) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "lifestream";
    public const string App = "lifestream";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var fields = new Fields();
        if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> state)
        {
            foreach (var (key, value) in state)
            {
                fields.Take(key, value);
            }
        }

        scopeProvider?.ForEachScope(
            static (scope, f) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var (key, value) in pairs)
                    {
                        f.Take(key, value);
                    }
                }
            },
            fields);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("time", DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString("level", LevelOf(logEntry.LogLevel));
            json.WriteString("app", App);
            json.WriteString("part", options.CurrentValue.Part);
            json.WriteString("event", EventOf(logEntry.EventId, logEntry.Category, logEntry.Exception));
            if (fields.Ray is { } ray)
            {
                json.WriteString("ray", ray);
            }

            json.WriteString("msg", message);
            if (fields.Method is { } method)
            {
                json.WriteString("method", method);
            }

            if (fields.Route is { } route)
            {
                json.WriteString("route", route);
            }

            if (fields.Status is { } status)
            {
                json.WriteNumber("status", status);
            }

            if (fields.Ms is { } ms)
            {
                json.WriteNumber("ms", ms);
            }

            if (fields.User is { } user)
            {
                json.WriteString("user", user);
            }

            if (fields.Correlation is { } correlation)
            {
                json.WriteString("correlation", correlation);
            }

            json.WriteString("logger", logEntry.Category);
            if ((logEntry.Exception?.GetType().FullName ?? fields.Error) is { } error)
            {
                json.WriteString("error", error);
            }

            if ((logEntry.Exception?.StackTrace ?? fields.Stack) is { } stack)
            {
                json.WriteString("stack", stack);
            }

            json.WriteEndObject();
        }

        textWriter.Write(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.Write('\n');
    }

    public static string LevelOf(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        _ => "error",
    };

    /// <summary>An exception is always <c>exception</c>; otherwise a named event from the fixed list, then one per known logger.</summary>
    public static string EventOf(EventId id, string category, Exception? exception)
    {
        if (exception is not null)
        {
            return "exception";
        }

        if (id.Name is { } name && LogEvents.Named.Contains(name))
        {
            return name;
        }

        return category switch
        {
            "Lifestream.Migrate" => "migrate",
            "Microsoft.Hosting.Lifetime" => "startup",
            _ when category.EndsWith(".OriginAuthMiddleware", StringComparison.Ordinal) => "origin",
            _ when category.EndsWith(".SignInWarmUp", StringComparison.Ordinal) => "warmup",
            _ => "log",
        };
    }

    /// <summary>The allow-list: structured state and scope values that become fields. Everything else stays out of the line.</summary>
    private sealed class Fields
    {
        public string? Ray { get; private set; }

        public string? Method { get; private set; }

        public string? Route { get; private set; }

        public int? Status { get; private set; }

        public long? Ms { get; private set; }

        public string? User { get; private set; }

        public string? Correlation { get; private set; }

        public string? Error { get; private set; }

        public string? Stack { get; private set; }

        public void Take(string key, object? value)
        {
            switch (key)
            {
                case "Ray":
                    Ray ??= value as string;
                    break;
                case "Method":
                    Method ??= value as string;
                    break;
                case "Route":
                    Route ??= value as string;
                    break;
                case "Status" when value is int status:
                    Status ??= status;
                    break;
                case "ElapsedMs" when value is long ms:
                    Ms ??= ms;
                    break;
                case "UserId" when value is not null:
                    var user = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (User is null && user is { Length: > 0 } && user != "anonymous")
                    {
                        User = user;
                    }

                    break;
                case "CorrelationId":
                    Correlation ??= value as string;
                    break;
                case "ExceptionType":
                    Error ??= value as string;
                    break;
                case "StackTrace":
                    Stack ??= value as string;
                    break;
            }
        }
    }
}

public static partial class StandardLogging
{
    /// <summary>
    /// Console output in the LOGGING.md shape for <paramref name="part"/>, and the level from <c>LOG_LEVEL</c> (debug, info, warn or
    /// error; default info, from appsettings). Called by the web host and by the migrate job.
    /// </summary>
    public static void Configure(ILoggingBuilder logging, IConfigurationManager configuration, string part)
    {
        if (LevelFromEnvironment(configuration["LOG_LEVEL"]) is { } level)
        {
            configuration.AddInMemoryCollection([new("Logging:LogLevel:Default", level.ToString())]);
        }

        logging.AddConsole(o => o.FormatterName = StandardJsonFormatter.FormatterName);
        logging.AddConsoleFormatter<StandardJsonFormatter, StandardJsonFormatterOptions>(o => o.Part = part);
    }

    public static LogLevel? LevelFromEnvironment(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => null,
    };

    /// <summary>The level of a request line: health and readiness checks debug, any other 5xx error, everything else info.</summary>
    public static LogLevel LevelFor(string route, int status) =>
        route is ApiRoutes.Healthz or ApiRoutes.Readyz ? LogLevel.Debug : status >= 500 ? LogLevel.Error : LogLevel.Information;

    /// <summary>The route pattern in LOGGING.md form: <c>/api/v1/donors/{id:int}</c> becomes <c>/api/v1/donors/:id</c>.</summary>
    public static string RouteOf(Endpoint? endpoint) =>
        endpoint is RouteEndpoint { RoutePattern.RawText: { } raw } ? Parameter().Replace(raw.StartsWith('/') ? raw : "/" + raw, ":$1") : "unmatched";

    /// <summary>
    /// The visitor's Cloudflare ray id: X-CF-Ray, which the edge Worker sets from the request it received (Cloudflare gives the
    /// Worker's own call to the origin a new CF-Ray), falling back to CF-Ray. Only when it looks like one: letters, digits, dashes.
    /// </summary>
    public static string? RayOf(HttpRequest request)
    {
        foreach (var header in (string[])["X-CF-Ray", "CF-Ray"])
        {
            var ray = request.Headers[header].ToString();
            if (ray.Length is > 0 and <= 40 && ray.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return ray;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\{\*{0,2}([A-Za-z0-9_]+)[^}]*\}")]
    private static partial Regex Parameter();
}
