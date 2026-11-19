using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lifestream.Api.Infrastructure;

namespace Lifestream.Api.Common;

/// <summary>
/// Enums travel as their names and nothing else: a JSON number, a numeric string, a name that is not defined, or several names joined
/// with commas (what <c>JsonStringEnumConverter</c> and <c>Enum.Parse</c> read as flags) are all refused, so an undefined value can never
/// be stored and "Male, Female" can never turn into one of them. A refusal is a 400 naming the field (see <see cref="BodyReader"/>).
/// </summary>
public sealed class StrictEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class StrictEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private static readonly Dictionary<string, T> ByName = Enum.GetValues<T>().ToDictionary(v => v.ToString(), v => v, StringComparer.OrdinalIgnoreCase);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } text && ByName.TryGetValue(text, out var value)
            ? value
            : throw new JsonException($"Not a defined {typeof(T).Name}.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>
/// Query-string enums (<c>?bloodType=APos</c>) are bound by the framework, which also accepts numbers and comma lists. This endpoint
/// filter, added to every route, refuses anything that is not exactly one defined name, spelled as defined (400, naming the parameter). It finds the enum
/// parameters from the handler itself (plain parameters and the properties of <c>[AsParameters]</c> records), so a new filter is covered
/// the moment it is declared.
/// </summary>
public static class EnumQueryFilter
{
    private static readonly ConcurrentDictionary<Type, string[]> NamesByType = new();

    public static void AddTo(RouteGroupBuilder group) => group.AddEndpointFilterFactory(Create);

    /// <summary>The query parameter names (as bound) and enum types a handler reads from the query string.</summary>
    public static IReadOnlyList<(string Name, Type Enum)> EnumParameters(MethodInfo handler)
    {
        var found = new List<(string, Type)>();
        foreach (var parameter in handler.GetParameters())
        {
            if (parameter.GetCustomAttribute<Microsoft.AspNetCore.Http.AsParametersAttribute>() is not null)
            {
                var constructor = parameter.ParameterType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                found.AddRange(constructor.GetParameters().Select(p => (p.Name!, EnumOf(p.ParameterType))).Where(p => p.Item2 is not null)!);
            }
            else if (EnumOf(parameter.ParameterType) is { } type && parameter.Name is { } name)
            {
                found.Add((name, type));
            }
        }

        return found;
    }

    private static Type? EnumOf(Type type) => Nullable.GetUnderlyingType(type) is { IsEnum: true } inner ? inner : type.IsEnum ? type : null;

    private static EndpointFilterDelegate Create(EndpointFilterFactoryContext context, EndpointFilterDelegate next)
    {
        var parameters = EnumParameters(context.MethodInfo).ToArray();
        if (parameters.Length == 0)
        {
            return next;
        }

        return invocation =>
        {
            var query = invocation.HttpContext.Request.Query;
            foreach (var (name, type) in parameters)
            {
                if (!query.TryGetValue(name, out var values))
                {
                    continue;
                }

                var defined = NamesByType.GetOrAdd(type, t => Enum.GetNames(t));
                if (values.Count > 1 || values.Any(v => !defined.Contains(v, StringComparer.Ordinal)))
                {
                    throw ApiException.Validation(name, $"must be one of: {string.Join(", ", defined)}");
                }
            }

            return next(invocation);
        };
    }
}
