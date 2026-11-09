using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Explicit file input and redacted output; ordinary model JSON remains a legacy data round trip.</summary>
internal static class PluginRegistrationJson
{
    private static readonly JsonSerializerOptions InputOptions = new() { PropertyNameCaseInsensitive = true };

    internal static PluginRegistrationConfig Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidJson();
            // Do not let duplicate or non-string version selectors silently choose a contract.
            var versions = document.RootElement.EnumerateObject()
                .Where(p => string.Equals(p.Name, "version", StringComparison.OrdinalIgnoreCase)).ToList();
            if (versions.Count > 1 || versions.Count == 1 && versions[0].Value.ValueKind != JsonValueKind.String)
                throw InvalidJson();
            if (versions.Count == 1) PluginRegistrationContract.ResolveVersion(versions[0].Value.GetString());
            var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(json, InputOptions) ?? throw InvalidJson();
            PluginRegistrationContract.Apply(config);
            return config;
        }
        catch (PpdsException) { throw; }
        catch (Exception)
        {
            // Parser messages/inner exceptions may contain supplied configuration, including secrets.
            throw InvalidJson();
        }
    }

    internal static string SerializeForOutput(PluginRegistrationConfig config, bool writeIndented = true)
    {
        try
        {
            PluginRegistrationContract.Apply(config);
            return JsonSerializer.Serialize(config, OutputOptions(writeIndented));
        }
        catch (PpdsException) { throw; }
        catch (Exception) { throw InvalidJson(); }
    }

    internal static JsonElement ToOutputElement(PluginRegistrationConfig config)
    {
        using var document = JsonDocument.Parse(SerializeForOutput(config, false));
        return document.RootElement.Clone();
    }

    internal static int CountExcludedSecrets(PluginRegistrationConfig config)
    {
        PluginRegistrationContract.Apply(config);
        return config.Assemblies.SelectMany(a => a.Types).SelectMany(t => t.Steps)
            .Count(s => s.SecureConfiguration != null);
    }

    private static JsonSerializerOptions OutputOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new RegistrationOutputConverter<PluginStepConfig>());
        options.Converters.Add(new RegistrationOutputConverter<PluginImageConfig>());
        return options;
    }

    private static PpdsException InvalidJson() => new(ErrorCodes.Validation.InvalidValue,
        "Registration JSON is invalid or cannot be serialized safely.");

    private sealed class RegistrationOutputConverter<T> : JsonConverter<T> where T : PluginConfigurationPresence
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

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("This converter is only used for explicit registration output.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (name, property) in Properties)
            {
                if (name == "secureConfiguration") continue;
                if (name == "images" && !value.IsSpecified(name) &&
                    property.GetValue(value) is System.Collections.ICollection { Count: 0 }) continue;
                if (OptionalFields.Contains(name) && !value.IsSpecified(name)) continue;
                if (name == "resetFields" && value.ResetFields.Count == 0) continue;
                var propertyValue = property.GetValue(value);
                if (propertyValue is null && !value.IsSpecified(name)) continue;
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, propertyValue, property.PropertyType, options);
            }
            if (ExtensionData.GetValue(value) is Dictionary<string, JsonElement> extension)
                foreach (var (name, element) in extension)
                {
                    if (Properties.ContainsKey("images") && string.Equals(name, "images", StringComparison.OrdinalIgnoreCase)) throw InvalidJson();
                    if (Properties.ContainsKey(name) || string.Equals(name, "secureConfiguration", StringComparison.OrdinalIgnoreCase)) continue;
                    writer.WritePropertyName(name);
                    element.WriteTo(writer);
                }
            writer.WriteEndObject();
        }
    }
}
