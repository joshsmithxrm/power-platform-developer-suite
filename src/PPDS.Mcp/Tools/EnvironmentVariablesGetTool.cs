using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that gets full details of a specific environment variable.
/// </summary>
[McpServerToolType]
public sealed class EnvironmentVariablesGetTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentVariablesGetTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public EnvironmentVariablesGetTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Gets full details of a specific environment variable including description, type, and values.
    /// </summary>
    /// <param name="schemaName">The schema name of the environment variable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Environment variable details.</returns>
    [McpServerTool(Name = "ppds_environment_variables_get")]
    [Description("Get full details of a specific environment variable including description, type, and values. Use the schemaName from ppds_environment_variables_list.")]
    public async Task<EnvironmentVariablesGetResult> ExecuteAsync(
        [Description("Schema name of the environment variable")]
        string schemaName,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken, (nameof(schemaName), schemaName)).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IEnvironmentVariableService>();

        var variable = await service.GetAsync(schemaName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Environment variable '{schemaName}' not found.");

        return new EnvironmentVariablesGetResult
        {
            SchemaName = variable.SchemaName,
            DisplayName = variable.DisplayName,
            Description = variable.Description,
            Type = variable.Type,
            DefaultValue = variable.DefaultValue,
            CurrentValue = variable.CurrentValue,
            IsManaged = variable.IsManaged,
            IsRequired = variable.IsRequired,
            SecretStore = variable.SecretStore,
            HasOverride = variable.CurrentValueId.HasValue,
            IsMissing = variable.IsRequired && variable.CurrentValue == null && variable.DefaultValue == null,
            CreatedOn = variable.CreatedOn?.ToString("o"),
            ModifiedOn = variable.ModifiedOn?.ToString("o")
        };
    }
}

/// <summary>
/// Result of the environment_variables_get tool.
/// </summary>
public sealed class EnvironmentVariablesGetResult
{
    /// <summary>
    /// The schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    /// <summary>
    /// The display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// The variable type (String, Number, Boolean, JSON, DataSource, Secret).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// The default value.
    /// </summary>
    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultValue { get; set; }

    /// <summary>
    /// The current value (from EnvironmentVariableValue record).
    /// </summary>
    [JsonPropertyName("currentValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentValue { get; set; }

    /// <summary>
    /// Whether this is a managed variable.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// Whether the variable is required.
    /// </summary>
    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }

    /// <summary>
    /// The secret store for secret-type variables.
    /// </summary>
    [JsonPropertyName("secretStore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecretStore { get; set; }

    /// <summary>
    /// Whether the current value overrides the default.
    /// </summary>
    [JsonPropertyName("hasOverride")]
    public bool HasOverride { get; set; }

    /// <summary>
    /// Whether the variable is required but has no value.
    /// </summary>
    [JsonPropertyName("isMissing")]
    public bool IsMissing { get; set; }

    /// <summary>
    /// When the variable was created (ISO 8601).
    /// </summary>
    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    /// <summary>
    /// When the variable was last modified (ISO 8601).
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}
