using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Cli.Plugins.Models;

/// <summary>
/// Root configuration for plugin registrations.
/// Serialized to/from registrations.json.
/// </summary>
public sealed class PluginRegistrationConfig
{
    /// <summary>
    /// JSON schema reference for validation.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Timestamp when the configuration was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>
    /// List of plugin assemblies in this configuration.
    /// </summary>
    [JsonPropertyName("assemblies")]
    public List<PluginAssemblyConfig> Assemblies { get; set; } = [];

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for a single plugin assembly.
/// </summary>
public sealed class PluginAssemblyConfig
{
    /// <summary>
    /// Assembly name (without extension).
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Assembly type: "Assembly" for classic DLL, "Nuget" for NuGet package.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Assembly";

    /// <summary>
    /// Relative path to the assembly DLL (from the config file location).
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// Relative path to the NuGet package (for Nuget type only).
    /// </summary>
    [JsonPropertyName("packagePath")]
    public string? PackagePath { get; set; }

    /// <summary>
    /// Solution unique name to add components to.
    /// Required for Nuget type, optional for Assembly type.
    /// </summary>
    [JsonPropertyName("solution")]
    public string? Solution { get; set; }

    /// <summary>
    /// All plugin type names in this assembly (for orphan detection).
    /// </summary>
    [JsonPropertyName("allTypeNames")]
    public List<string> AllTypeNames { get; set; } = [];

    /// <summary>
    /// Plugin types with their step registrations.
    /// </summary>
    [JsonPropertyName("types")]
    public List<PluginTypeConfig> Types { get; set; } = [];

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for a single plugin type (class).
/// </summary>
public sealed class PluginTypeConfig
{
    /// <summary>
    /// Fully qualified type name (namespace.classname).
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Step registrations for this plugin type.
    /// </summary>
    [JsonPropertyName("steps")]
    public List<PluginStepConfig> Steps { get; set; } = [];

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for a single plugin step registration.
/// </summary>
public sealed class PluginStepConfig
{
    /// <summary>
    /// Display name for the step.
    /// Auto-generated if not specified: "{TypeName}: {Message} of {Entity}".
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// SDK message name (Create, Update, Delete, Retrieve, etc.).
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Primary entity logical name. Use "none" for global messages.
    /// </summary>
    [JsonPropertyName("entity")]
    public string Entity { get; set; } = string.Empty;

    /// <summary>
    /// Secondary entity logical name for relationship messages (Associate, etc.).
    /// </summary>
    [JsonPropertyName("secondaryEntity")]
    public string? SecondaryEntity { get; set; }

    /// <summary>
    /// Pipeline stage: PreValidation, PreOperation, or PostOperation.
    /// </summary>
    [JsonPropertyName("stage")]
    public string Stage { get; set; } = string.Empty;

    /// <summary>
    /// Execution mode: Synchronous or Asynchronous.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "Synchronous";

    /// <summary>
    /// Execution order when multiple plugins handle the same event.
    /// </summary>
    [JsonPropertyName("executionOrder")]
    public int ExecutionOrder { get; set; } = 1;

    /// <summary>
    /// Comma-separated list of attributes that trigger this step (Update message only).
    /// </summary>
    [JsonPropertyName("filteringAttributes")]
    public string? FilteringAttributes { get; set; }

    /// <summary>
    /// Unsecure configuration string passed to plugin constructor.
    /// </summary>
    [JsonPropertyName("configuration")]
    public string? Configuration { get; set; }

    /// <summary>
    /// Deployment target: ServerOnly (default), Offline, or Both.
    /// </summary>
    [JsonPropertyName("deployment")]
    public string? Deployment { get; set; }

    /// <summary>
    /// User context to run the plugin as.
    /// Use "CallingUser" (default), "System", or a systemuser GUID.
    /// </summary>
    [JsonPropertyName("runAsUser")]
    public string? RunAsUser { get; set; }

    /// <summary>
    /// Description of what this step does.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// For async steps, whether to delete the async job on successful completion.
    /// Default is false (keep async job records).
    /// </summary>
    [JsonPropertyName("asyncAutoDelete")]
    public bool? AsyncAutoDelete { get; set; }

    /// <summary>
    /// Step identifier for associating images with specific steps on multi-step plugins.
    /// </summary>
    [JsonPropertyName("stepId")]
    public string? StepId { get; set; }

    /// <summary>
    /// Entity images registered for this step.
    /// </summary>
    [JsonPropertyName("images")]
    public List<PluginImageConfig> Images { get; set; } = [];

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for a plugin step image (pre-image or post-image).
/// </summary>
public sealed class PluginImageConfig
{
    /// <summary>
    /// Name used to access the image in plugin context.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Image type: PreImage, PostImage, or Both.
    /// </summary>
    [JsonPropertyName("imageType")]
    public string ImageType { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of attributes to include. Null means all attributes.
    /// </summary>
    [JsonPropertyName("attributes")]
    public string? Attributes { get; set; }

    /// <summary>
    /// Entity alias for the image. Defaults to Name if not specified.
    /// </summary>
    [JsonPropertyName("entityAlias")]
    public string? EntityAlias { get; set; }

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
