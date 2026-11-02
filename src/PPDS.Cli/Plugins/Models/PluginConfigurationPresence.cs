using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Cli.Plugins.Models;

/// <summary>The caller's optional-field contract.</summary>
public enum RegistrationOperationIntent { Patch, Deploy }

/// <summary>Tracks supplied fields without confusing an omitted property with an explicit reset.</summary>
public abstract class PluginConfigurationPresence
{
    private readonly HashSet<string> _specified = new(StringComparer.Ordinal);

    /// <summary>Imperative callers preserve legacy placeholders; config files use deploy intent.</summary>
    [JsonIgnore]
    public RegistrationOperationIntent OperationIntent { get; set; } = RegistrationOperationIntent.Patch;

    /// <summary>Explicit nonsecret reset requests for partial callers.</summary>
    [JsonPropertyName("resetFields")]
    public List<string> ResetFields { get; set; } = [];

    /// <summary>Whether a JSON-named property was supplied, including an explicit null.</summary>
    public bool IsSpecified(string propertyName) => _specified.Contains(propertyName);

    internal void PreserveLegacyPlaceholders()
    {
        foreach (var property in GetType().GetProperties())
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (name == null) continue;
            var value = property.GetValue(this);
            if (value == null || value is string text && text.Length == 0) _specified.Remove(name);
        }
    }

    protected void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        field = value;
        _specified.Add(JsonNamingPolicy.CamelCase.ConvertName(propertyName));
    }
}

// A type-local converter keeps presence semantics independent of the serializer options used by
// CLI, RPC tests, extract, and callers. In particular, WhenWritingNull must not erase a reset.
internal abstract class PluginPresenceJsonConverter<T> : JsonConverter<T> where T : PluginConfigurationPresence, new()
{
    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(T).GetProperties()
        .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() != null)
        .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name, StringComparer.Ordinal);
    private static readonly PropertyInfo ExtensionData = typeof(T).GetProperty("ExtensionData")!;
    private static readonly HashSet<string> OptionalFields = new(StringComparer.Ordinal)
    {
        "filteringAttributes", "unsecureConfiguration", "runAsUser", "description", "asyncAutoDelete",
        "canBeBypassed", "canUseReadOnlyConnection", "attributes"
    };

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("A registration must be an object.");
        var result = new T { OperationIntent = RegistrationOperationIntent.Deploy };
        Dictionary<string, JsonElement>? unknown = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var member = Properties.GetValueOrDefault(property.Name);
            if (member == null && options.PropertyNameCaseInsensitive)
                member = Properties.FirstOrDefault(p => string.Equals(p.Key, property.Name, StringComparison.OrdinalIgnoreCase)).Value;
            if (member != null)
                member.SetValue(result, property.Value.Deserialize(member.PropertyType, options));
            else
                (unknown ??= new(StringComparer.Ordinal))[property.Name] = property.Value.Clone();
        }
        ExtensionData.SetValue(result, unknown);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, property) in Properties)
        {
            // Secure values are write-only input. Serialization is used by extract and JSON output.
            if (name == "secureConfiguration") continue;
            if (OptionalFields.Contains(name) && !value.IsSpecified(name)) continue;
            if (name == "resetFields" && value.ResetFields?.Count == 0) continue;
            var propertyValue = property.GetValue(value);
            if (propertyValue == null && !value.IsSpecified(name)) continue;
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, propertyValue, property.PropertyType, options);
        }
        if (ExtensionData.GetValue(value) is Dictionary<string, JsonElement> extension)
            foreach (var (name, element) in extension)
            {
                if (Properties.ContainsKey(name) || string.Equals(name, "secureConfiguration", StringComparison.OrdinalIgnoreCase)) continue;
                writer.WritePropertyName(name);
                element.WriteTo(writer);
            }
        writer.WriteEndObject();
    }
}

internal sealed class PluginStepConfigJsonConverter : PluginPresenceJsonConverter<PluginStepConfig>;
internal sealed class PluginImageConfigJsonConverter : PluginPresenceJsonConverter<PluginImageConfig>;
