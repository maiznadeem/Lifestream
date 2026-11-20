using System.Text.Json;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>One entry of a drop-down in a form: the text shown and the typed value. A form combo box is bound to a list of these, never to bare strings.</summary>
public sealed record Choice<T>(string Display, T Value)
{
    public override string ToString() => Display;
}

/// <summary>One label and value line of a detail pane.</summary>
public sealed record DetailFact(string Label, string Value);

/// <summary>Plain-language text for a failed call, naming the record (FEAT-06): never a status code or exception text.</summary>
public static class ProblemText
{
    public static string For(ApiProblem problem, string recordType, string? recordName = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var subject = recordName is null ? $"The {recordType}" : $"{Capitalise(recordType)} '{recordName}'";
        return problem.Code switch
        {
            ProblemCodes.NotFound => $"{subject} was not found. It may already have been deleted.",
            ProblemCodes.Conflict => $"{subject} can't be changed because it is in use by other records.",
            ProblemCodes.Concurrency => $"{subject} was changed by someone else. Reload to see the latest version.",
            ProblemCodes.DemoForbidden => "The demo account can't make this change.",
            ProblemCodes.DatabaseWaking => "The server is still starting up. Try again in a moment.",
            ProblemCodes.DatabaseResting => WakingTexts.Resting,
            ProblemCodes.RateLimited => "Too many requests. Wait a moment and try again.",
            ClientProblemCodes.Unreachable => "Can't reach the server. Check your internet connection and try again.",
            ClientProblemCodes.SessionExpired => "Your session has expired. Sign in again.",
            _ => string.IsNullOrWhiteSpace(problem.Title) ? "The request failed. Try again." : problem.Title,
        };
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>Reads the API's field-level validation errors (an "errors" object of field name to messages) so a form can show them under the fields (FEAT-07).</summary>
public static class ProblemFieldErrors
{
    /// <summary>
    /// The first message per field. The problem's detail may be the whole problem body (with an "errors" object) or the errors object
    /// itself, each field mapping to a message or a list of messages; empty when the problem carries none.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(ApiProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (problem.Errors is { Count: > 0 } parsed)
        {
            // The API client already parsed the problem's "errors" object: use it as it is.
            foreach (var (field, messages) in parsed)
            {
                if (messages.FirstOrDefault(m => !string.IsNullOrEmpty(m)) is { } message)
                {
                    result[field] = message;
                }
            }

            return result;
        }

        if (string.IsNullOrWhiteSpace(problem.Detail))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(problem.Detail);
            var root = document.RootElement;
            var errors = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var inner) ? inner : root;
            if (errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in errors.EnumerateObject())
                {
                    var message = property.Value.ValueKind switch
                    {
                        JsonValueKind.Array => property.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).FirstOrDefault(m => !string.IsNullOrEmpty(m)),
                        JsonValueKind.String => property.Value.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(message))
                    {
                        result[property.Name] = message;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // The detail is plain text: no field errors.
        }

        return result;
    }
}
