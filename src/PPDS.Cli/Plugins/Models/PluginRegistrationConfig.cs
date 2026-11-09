using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Cli.Plugins.Models;

/// <summary>
/// Converts DateTimeOffset to/from Zulu time format (yyyy-MM-ddTHH:mm:ssZ).
/// </summary>
public sealed class ZuluTimeConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return string.IsNullOrEmpty(value) ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

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
    /// Registration contract: 1.0 preserves legacy optional omissions; 2.0 reconciles nonsecret defaults.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Timestamp when the configuration was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    [JsonConverter(typeof(ZuluTimeConverter))]
    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>
    /// List of plugin assemblies in this configuration.
    /// </summary>
    [JsonPropertyName("assemblies")]
    public List<PluginAssemblyConfig> Assemblies { get; set; } = [];

    /// <summary>
    /// Custom API definitions extracted from annotated plugin types.
    /// Null when no custom APIs are present (omitted from JSON output).
    /// </summary>
    [JsonPropertyName("customApis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomApiConfig>? CustomApis { get; set; }

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
    public void Validate()
    {
        var errors = new List<string>();

        foreach (var assembly in Assemblies)
        {
            foreach (var type in assembly.Types)
            {
                // Track step names (resolved — explicit or auto-generated) to detect collisions
                // within this plugin type. Collisions would silently overwrite each other during
                // deployment (see GH issue #781).
                var stepNameUsages = new Dictionary<string, List<string>>(StringComparer.Ordinal);

                foreach (var step in type.Steps)
                {
                    if (step.ExecutionOrder is < PluginStepConfig.MinExecutionOrder or > PluginStepConfig.MaxExecutionOrder)
                    {
                        errors.Add($"Step '{step.Name ?? type.TypeName}' has invalid executionOrder {step.ExecutionOrder}. " +
                            $"Must be between {PluginStepConfig.MinExecutionOrder} and {PluginStepConfig.MaxExecutionOrder}.");
                    }

                    // Resolve name the same way DeployCommand/DiffCommand do:
                    // "{TypeName}: {Message} of {Entity}" when no explicit Name is provided.
                    var resolvedName = !string.IsNullOrWhiteSpace(step.Name)
                        ? step.Name!
                        : $"{type.TypeName}: {step.Message} of {step.Entity}";

                    var origin = !string.IsNullOrWhiteSpace(step.Name)
                        ? "explicit name"
                        : $"auto-generated from message '{step.Message}' on '{step.Entity}'";

                    if (!stepNameUsages.TryGetValue(resolvedName, out var origins))
                    {
                        origins = [];
                        stepNameUsages[resolvedName] = origins;
                    }
                    origins.Add(origin);
                }

                foreach (var (name, origins) in stepNameUsages)
                {
                    if (origins.Count > 1)
                    {
                        errors.Add(
                            $"Plugin type '{type.TypeName}' has {origins.Count} steps resolving to the same name '{name}'. " +
                            $"Conflicting sources: {string.Join("; ", origins)}. " +
                            $"Provide a unique explicit Name on at least {origins.Count - 1} of these steps, " +
                            "or change the Message/Entity combination so the auto-generated names differ.");
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Plugin registration configuration is invalid:\n{string.Join("\n", errors)}");
        }
    }
}

/// <summary>
/// Configuration for a single plugin assembly.
/// </summary>
public sealed class PluginAssemblyConfig
{
    /// <summary>Validated file operation intent, including assemblies with no configured steps.</summary>
    [JsonIgnore]
    public RegistrationOperationIntent OperationIntent { get; set; } = RegistrationOperationIntent.Patch;

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
    /// SHA-256 of the immutable NuGet package snapshot used during extraction. This lets deploy
    /// preserve --reference-dir portability while detecting changed content whose primary
    /// plug-in identity cannot be proven from package-local metadata alone.
    /// </summary>
    [JsonPropertyName("packageContentSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageContentSha256 { get; set; }

    /// <summary>
    /// Solution unique name to add components to.
    /// Required for Nuget type, optional for Assembly type.
    /// </summary>
    [JsonPropertyName("solution")]
    public string? Solution { get; set; }

    /// <summary>
    /// Concrete runtime IPlugin implementations and PPDS-annotated plugin type names in this
    /// assembly (for orphan detection). Runtime types are included even when they have no steps.
    /// </summary>
    [JsonPropertyName("allTypeNames")]
    public List<string> AllTypeNames { get; set; } = [];

    /// <summary>
    /// Runtime IPlugin implementations discovered from assembly metadata. This inspection detail
    /// is not part of registrations.json; <see cref="AllTypeNames"/> is the serialized contract.
    /// </summary>
    [JsonIgnore]
    internal List<string> RuntimePluginTypeNames { get; set; } = [];

    /// <summary>
    /// Plugin types with their step registrations.
    /// </summary>
    [JsonPropertyName("types")]
    public List<PluginTypeConfig> Types { get; set; } = [];

    /// <summary>
    /// Custom API definitions extracted from annotated plugin types in this assembly.
    /// Null when no custom APIs are present (omitted from JSON output).
    /// </summary>
    [JsonPropertyName("customApis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomApiConfig>? CustomApis { get; set; }

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
[JsonConverter(typeof(PPDS.Cli.Services.Plugins.PluginStepConfigJsonConverter))]
public sealed class PluginStepConfig : PluginConfigurationPresence
{
    private string? _name;
    private string _message = string.Empty;
    private string _entity = string.Empty;
    private string? _secondaryEntity;
    private string _stage = string.Empty;
    private string _mode = "Synchronous";
    private int _executionOrder = 1;
    private string? _filteringAttributes;
    private string? _unsecureConfiguration;
    private string? _deployment;
    private string? _runAsUser;
    private bool? _canBeBypassed;
    private bool? _canUseReadOnlyConnection;
    private string? _invocationSource;
    private string? _secureConfiguration;
    private bool _enabled = true;
    private string? _description;
    private bool? _asyncAutoDelete;
    private string? _stepId;
    private List<PluginImageConfig> _images = [];

    /// <summary>
    /// Minimum valid execution order value in Dataverse.
    /// </summary>
    public const int MinExecutionOrder = 1;

    /// <summary>
    /// Maximum valid execution order value in Dataverse.
    /// </summary>
    public const int MaxExecutionOrder = 999999;

    /// <summary>
    /// Display name for the step.
    /// Auto-generated if not specified: "{TypeName}: {Message} of {Entity}".
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// SDK message name (Create, Update, Delete, Retrieve, etc.).
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get => _message; set => Set(ref _message, value); }

    /// <summary>
    /// Primary entity logical name. Use "none" for global messages.
    /// </summary>
    [JsonPropertyName("entity")]
    public string Entity { get => _entity; set => Set(ref _entity, value); }

    /// <summary>
    /// Secondary entity logical name for relationship messages (Associate, etc.).
    /// </summary>
    [JsonPropertyName("secondaryEntity")]
    public string? SecondaryEntity { get => _secondaryEntity; set => Set(ref _secondaryEntity, value); }

    /// <summary>
    /// Pipeline stage: PreValidation, PreOperation, or PostOperation.
    /// </summary>
    [JsonPropertyName("stage")]
    public string Stage { get => _stage; set => Set(ref _stage, value); }

    /// <summary>
    /// Execution mode: Synchronous or Asynchronous.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>
    /// Execution order when multiple plugins handle the same event.
    /// Lower numbers execute first. Must be between 1 and 999999.
    /// Default: 1.
    /// </summary>
    [JsonPropertyName("executionOrder")]
    public int ExecutionOrder { get => _executionOrder; set => Set(ref _executionOrder, value); }

    /// <summary>
    /// Comma-separated list of attributes that trigger this step (Update message only).
    /// </summary>
    [JsonPropertyName("filteringAttributes")]
    public string? FilteringAttributes { get => _filteringAttributes; set => Set(ref _filteringAttributes, value); }

    /// <summary>
    /// Unsecure configuration string passed to plugin constructor.
    /// Safe for source control (never contains secrets).
    /// </summary>
    [JsonPropertyName("unsecureConfiguration")]
    public string? UnsecureConfiguration { get => _unsecureConfiguration; set => Set(ref _unsecureConfiguration, value); }

    /// <summary>
    /// Deployment target: ServerOnly (default), Offline, or Both.
    /// </summary>
    [JsonPropertyName("deployment")]
    public string? Deployment { get => _deployment; set => Set(ref _deployment, value); }

    /// <summary>
    /// User context to run the plugin as.
    /// Use "CallingUser" to clear impersonation, a systemuser GUID,
    /// a domain name (e.g., "user@domain.com"), or an email address.
    /// </summary>
    [JsonPropertyName("runAsUser")]
    public string? RunAsUser { get => _runAsUser; set => Set(ref _runAsUser, value); }

    /// <summary>
    /// Whether this step can be bypassed via BypassBusinessLogicExecution.
    /// Omission preserves an existing value. Explicit null resets to true; both boolean values are written.
    /// </summary>
    [JsonPropertyName("canBeBypassed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanBeBypassed { get => _canBeBypassed; set => Set(ref _canBeBypassed, value); }

    /// <summary>
    /// Whether this step can use a read-only database connection for improved performance.
    /// Omission preserves an existing value. Explicit null resets to false; both boolean values are written.
    /// </summary>
    [JsonPropertyName("canUseReadOnlyConnection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanUseReadOnlyConnection { get => _canUseReadOnlyConnection; set => Set(ref _canUseReadOnlyConnection, value); }

    /// <summary>
    /// Pipeline invocation source: Parent (default) or Child.
    /// Deploy always manages this field; omitted/null defaults to Parent.
    /// </summary>
    [JsonPropertyName("invocationSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvocationSource { get => _invocationSource; set => Set(ref _invocationSource, value); }

    /// <summary>
    /// Secure configuration string passed to plugin constructor.
    /// Write-only at runtime — never extracted from assemblies, never stored in source control.
    /// Omitted/null preserves the existing value; any non-null string requests a write.
    /// </summary>
    [JsonPropertyName("secureConfiguration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecureConfiguration { get => _secureConfiguration; set => Set(ref _secureConfiguration, value); }

    /// <summary>
    /// Whether the step is enabled. Default is true.
    /// When false, the step is registered but disabled (won't execute).
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>
    /// Description of what this step does.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get => _description; set => Set(ref _description, value); }

    /// <summary>
    /// For async steps, whether to delete the async job on successful completion.
    /// Default is false (keep async job records).
    /// </summary>
    [JsonPropertyName("asyncAutoDelete")]
    public bool? AsyncAutoDelete { get => _asyncAutoDelete; set => Set(ref _asyncAutoDelete, value); }

    /// <summary>
    /// Step identifier for associating images with specific steps on multi-step plugins.
    /// </summary>
    [JsonPropertyName("stepId")]
    public string? StepId { get => _stepId; set => Set(ref _stepId, value); }

    /// <summary>
    /// Entity images registered for this step.
    /// </summary>
    [JsonPropertyName("images")]
    public List<PluginImageConfig> Images { get => _images; set => Set(ref _images, value); }

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for a Custom API registration derived from a <c>CustomApiAttribute</c>-annotated type.
/// </summary>
public sealed class CustomApiConfig
{
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("pluginTypeName")]
    public string PluginTypeName { get; set; } = "";

    [JsonPropertyName("bindingType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BindingType { get; set; }

    [JsonPropertyName("boundEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundEntity { get; set; }

    [JsonPropertyName("isFunction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsFunction { get; set; }

    [JsonPropertyName("isPrivate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("executePrivilegeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutePrivilegeName { get; set; }

    [JsonPropertyName("allowedProcessingStepType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AllowedProcessingStepType { get; set; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CustomApiParameterConfig>? Parameters { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for a Custom API request or response parameter.
/// </summary>
public sealed class CustomApiParameterConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("logicalEntityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalEntityName { get; set; }

    [JsonPropertyName("isOptional")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsOptional { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "Input";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for a plugin step image (pre-image or post-image).
/// </summary>
public sealed class PluginImageConfig : PluginConfigurationPresence
{
    private string _name = string.Empty;
    private string _imageType = string.Empty;
    private string? _attributes;
    private string? _entityAlias;
    private string? _description;
    private string? _messagePropertyName;

    /// <summary>
    /// Name used to access the image in plugin context.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// Image type: PreImage, PostImage, or Both.
    /// </summary>
    [JsonPropertyName("imageType")]
    public string ImageType { get => _imageType; set => Set(ref _imageType, value); }

    /// <summary>
    /// Comma-separated list of attributes to include. Omission preserves existing values; explicit null/empty resets to all attributes in deploy.
    /// </summary>
    [JsonPropertyName("attributes")]
    public string? Attributes { get => _attributes; set => Set(ref _attributes, value); }

    /// <summary>
    /// Entity alias for the image. Defaults to Name if not specified.
    /// </summary>
    [JsonPropertyName("entityAlias")]
    public string? EntityAlias { get => _entityAlias; set => Set(ref _entityAlias, value); }

    /// <summary>
    /// Description of this image's purpose. Stored as metadata in Dataverse.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get => _description; set => Set(ref _description, value); }

    /// <summary>
    /// The message property name that carries the entity.
    /// If null, auto-inferred from the message name (e.g., "Target" for Create/Update).
    /// </summary>
    [JsonPropertyName("messagePropertyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessagePropertyName { get => _messagePropertyName; set => Set(ref _messagePropertyName, value); }

    /// <summary>
    /// Preserves unknown JSON properties during round-trip serialization.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
