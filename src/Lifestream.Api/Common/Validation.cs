using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lifestream.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Common;

/// <summary>Collects field errors, then throws one 400 problem with all of them.</summary>
public sealed partial class Check
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.OrdinalIgnoreCase);

    public bool Ok => _errors.Count == 0;

    public Check Fail(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
        return this;
    }

    public void ThrowIfInvalid()
    {
        if (!Ok)
        {
            throw ApiException.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }

    /// <summary>A required text of 1..max characters once trimmed.</summary>
    public Check Text(string field, string? value, int max, bool required = true, int min = 1)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return required ? Fail(field, "is required") : this;
        }

        if (trimmed.Length < min)
        {
            Fail(field, $"must be at least {min} characters");
        }

        if (trimmed.Length > max)
        {
            Fail(field, $"must be at most {max} characters");
        }

        return this;
    }

    public Check Phone(string field, string? value) =>
        string.IsNullOrWhiteSpace(value) || PhonePattern().IsMatch(value.Trim()) ? this : Fail(field, "must be a phone number of up to 20 digits, spaces, + ( ) or -");

    public Check Enum<T>(string field, T value)
        where T : struct, Enum =>
        System.Enum.IsDefined(value) ? this : Fail(field, "is not a valid value");

    /// <summary>A date of birth: a real date in the past, not earlier than 1900.</summary>
    public Check DateOfBirth(string field, DateOnly value, DateOnly today)
    {
        if (value == default || value.Year < 1900)
        {
            return Fail(field, "is required");
        }

        return value >= today ? Fail(field, "must be in the past") : this;
    }

    public Check Range(string field, int value, int min, int max) =>
        value >= min && value <= max ? this : Fail(field, $"must be between {min} and {max}");

    public Check Positive(string field, int value) => value > 0 ? this : Fail(field, "must be a valid id");

    public Check NotDefault(string field, DateTime value) =>
        value == default || value.Year < 2000 ? Fail(field, "is required") : this;

    [GeneratedRegex(@"^\+?[0-9 ()\-]{5,20}$")]
    private static partial Regex PhonePattern();
}

/// <summary>Reads a JSON body, rejecting missing or null required fields (so an absent dropdown can never become the first enum value).</summary>
public static class BodyReader
{
    private static readonly ConcurrentDictionary<Type, string[]> RequiredByType = new();

    public static async Task<T> ReadBodyAsync<T>(this HttpRequest request, CancellationToken ct)
        where T : class
    {
        var options = request.HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await request.Body.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        if (bytes.Length == 0)
        {
            throw ApiException.Validation("body", "is required");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw ApiException.Validation("body", "must be a JSON object");
            }

            var check = new Check();
            foreach (var name in RequiredByType.GetOrAdd(typeof(T), RequiredNames))
            {
                var present = document.RootElement.EnumerateObject()
                    .Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null);
                if (!present)
                {
                    check.Fail(name, "is required");
                }
            }

            check.ThrowIfInvalid();
            return JsonSerializer.Deserialize<T>(bytes, options) ?? throw ApiException.Validation("body", "is required");
        }
        catch (JsonException exception)
        {
            // Name the field that could not be read (from the JSON path), never the parser's own message.
            var path = exception.Path ?? "";
            var field = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..].Split('.', '[')[0] : "body";
            throw ApiException.Validation(field.Length > 0 ? field : "body", "is not a valid value");
        }
    }

    // Constructor parameters without a default whose type is not nullable are required.
    private static string[] RequiredNames(Type type)
    {
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var context = new NullabilityInfoContext();
        return
        [
            .. constructor.GetParameters()
                .Where(p => !p.HasDefaultValue && !IsNullable(context, p))
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name!)),
        ];
    }

    private static bool IsNullable(NullabilityInfoContext context, ParameterInfo parameter) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null || context.Create(parameter).WriteState == NullabilityState.Nullable;
}

/// <summary>Row version round trip: base64 text in the contract, bytes in the entity.</summary>
public static class RowVersions
{
    public static string ToText(byte[] version) => Convert.ToBase64String(version);

    public static byte[] FromText(string? text, string field = "rowVersion")
    {
        try
        {
            var bytes = Convert.FromBase64String(text ?? "");
            return bytes.Length == 8 ? bytes : throw ApiException.Validation(field, "is not valid");
        }
        catch (FormatException)
        {
            throw ApiException.Validation(field, "is not valid");
        }
    }
}

public static class Dates
{
    /// <summary>Normalises any parsed timestamp to UTC (a value without an offset is taken as UTC).</summary>
    public static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}
