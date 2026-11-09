using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Cli.Plugins.Models;

/// <summary>The caller's optional-field contract.</summary>
public enum RegistrationOperationIntent { Patch = 0, Deploy = 1, Reconcile = 2, Update = 3 }

/// <summary>Tracks supplied fields without confusing an omitted property with an explicit reset.</summary>
public abstract class PluginConfigurationPresence
{
    private readonly HashSet<string> _specified = new(StringComparer.Ordinal);

    /// <summary>Imperative callers preserve legacy placeholders; file boundaries apply an explicit versioned intent.</summary>
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
