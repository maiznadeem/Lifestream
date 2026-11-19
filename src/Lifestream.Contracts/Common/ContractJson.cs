using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Lifestream.Contracts;

/// <summary>The one JSON configuration used by the API and the client, so both sides agree on the wire format.</summary>
public static class ContractJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitAbsentOptionals } },
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void OmitAbsentOptionals(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            var type = property.PropertyType;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>))
            {
                var getter = property.Get;
                property.ShouldSerialize = (obj, _) => getter?.Invoke(obj) is IOptional { HasValue: true };
            }
        }
    }
}
