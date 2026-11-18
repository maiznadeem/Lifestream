using System.Text.Json;

namespace Lifestream.Client.Api;

/// <summary>Reads a string property from a server body of any shape: a body that is null, an array or a string, or a property of another type, yields null instead of throwing (SHELL-02).</summary>
internal static class JsonText
{
    /// <summary>Reads the problem-details <c>errors</c> object: field name to messages. Anything that is not an object of strings, or of arrays of strings, is skipped.</summary>
    public static IReadOnlyDictionary<string, string[]>? Errors(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in errors.EnumerateObject())
        {
            var messages = field.Value.ValueKind switch
            {
                JsonValueKind.String => [field.Value.GetString()!],
                JsonValueKind.Array => field.Value.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToArray(),
                _ => [],
            };
            if (messages.Length > 0)
            {
                result[field.Name] = messages;
            }
        }

        return result.Count == 0 ? null : result;
    }

    public static string? Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
