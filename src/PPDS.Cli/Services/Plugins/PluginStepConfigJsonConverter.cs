using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Preserves image-list authority in ordinary model JSON without changing the public List contract.</summary>
internal sealed class PluginStepConfigJsonConverter : JsonConverter<PluginStepConfig>
{
    private static readonly (string Name, PropertyInfo Property)[] Properties = typeof(PluginStepConfig).GetProperties()
        .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
        .Select(p => (p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name, p)).ToArray();

    public PluginStepConfigJsonConverter() { }

    public override PluginStepConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        var result = new PluginStepConfig();
        var imagesSeen = false;
        foreach (var input in document.RootElement.EnumerateObject())
        {
            if (string.Equals(input.Name, "images", StringComparison.OrdinalIgnoreCase))
            {
                if (imagesSeen) throw Invalid();
                imagesSeen = true;
            }
            var comparison = options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var property = Properties.FirstOrDefault(p => string.Equals(p.Name, input.Name, comparison)).Property;
            if (property is not null)
                property.SetValue(result, input.Value.Deserialize(property.PropertyType, options));
            else
            {
                result.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                result.ExtensionData[input.Name] = input.Value.Clone();
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, PluginStepConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, property) in Properties)
        {
            // Reading Images never opts into clearing. Nonempty imperative lists remain serializable.
            if (name == "images" && !value.IsSpecified(name) && value.Images is { Count: 0 }) continue;
            var item = property.GetValue(value);
            var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition ?? options.DefaultIgnoreCondition;
            if (ignore == JsonIgnoreCondition.Always || item is null && ignore == JsonIgnoreCondition.WhenWritingNull) continue;
            if (ignore == JsonIgnoreCondition.WhenWritingDefault &&
                (item is null || property.PropertyType.IsValueType && Equals(item, Activator.CreateInstance(property.PropertyType)))) continue;
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, item, property.PropertyType, options);
        }
        if (value.ExtensionData is not null)
            foreach (var (name, item) in value.ExtensionData)
            {
                // A shadow property must never manufacture or override deletion authority.
                if (string.Equals(name, "images", StringComparison.OrdinalIgnoreCase)) throw Invalid();
                writer.WritePropertyName(name);
                item.WriteTo(writer);
            }
        writer.WriteEndObject();
    }

    private static PpdsException Invalid() => new(ErrorCodes.Validation.InvalidValue,
        "Registration step JSON requires one unambiguous image-list property.");
}
