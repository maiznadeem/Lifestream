using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lifestream.Contracts;

/// <summary>
/// A PATCH field that is either absent (leave unchanged) or present, possibly with an explicit null (clear it).
/// <c>default(Optional&lt;T&gt;)</c> is absent. Use <see cref="ContractJson.CreateOptions"/> so absent fields are omitted when writing.
/// </summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Optional<T> is the specified contract name; the VB keyword clash is irrelevant (C# only).")]
public readonly record struct Optional<T>(bool HasValue, T? Value) : IOptional
{
    public static implicit operator Optional<T>(T? value) => Optional.Of(value);
}

/// <summary>Factory for <see cref="Optional{T}"/>: <c>Optional.Of&lt;string?&gt;(null)</c> means "clear this field"; <c>default</c> means "leave it".</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Optional is the specified contract name; the VB keyword clash is irrelevant (C# only).")]
public static class Optional
{
    public static Optional<T> Of<T>(T? value) => new(true, value);
}

/// <summary>Non-generic view of an Optional so serializer hooks can test for presence.</summary>
public interface IOptional
{
    bool HasValue { get; }
}

internal sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var inner = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(typeof(OptionalJsonConverter<>).MakeGenericType(inner))!;
    }
}

internal sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    // A JSON null must reach Read so it becomes "present with null" rather than "absent".
    public override bool HandleNull => true;

    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Optional.Of<T>(default);
        }

        return Optional.Of(JsonSerializer.Deserialize<T>(ref reader, options));
    }

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (value.Value is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value.Value, options);
    }
}
