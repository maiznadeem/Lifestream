using System.Text.Json;

namespace Lifestream.Client.Api;

/// <summary>Reads a string property from a server body of any shape: a body that is null, an array or a string, or a property of another type, yields null instead of throwing (SHELL-02).</summary>
internal static class JsonText
{
    public static string? Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
