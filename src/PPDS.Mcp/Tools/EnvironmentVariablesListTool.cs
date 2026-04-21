using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists environment variable definitions and their current values.
/// </summary>
[McpServerToolType]
public sealed class EnvironmentVariablesListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentVariablesListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public EnvironmentVariablesListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists environment variable definitions and their current values.
    /// </summary>
    /// <param name="solutionId">Optional solution unique name to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of environment variable summaries.</returns>
    [McpServerTool(Name = "ppds_environment_variables_list")]
    [Description("List environment variable definitions and their current values. Shows default vs current values, type, and override status. Optionally filter by solution name.")]
    public async Task<EnvironmentVariablesListResult> ExecuteAsync(
        [Description("Solution unique name to filter by")]
        string? solutionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IEnvironmentVariableService>();

        var result = await service.ListAsync(solutionName: solutionId, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new EnvironmentVariablesListResult
        {
            TotalCount = result.TotalCount,
            EnvironmentVariables = result.Items.Select(v => new EnvironmentVariableSummary
            {
                SchemaName = v.SchemaName,
                DisplayName = v.DisplayName,
                Type = v.Type,
                DefaultValue = v.DefaultValue,
                CurrentValue = v.CurrentValue,
                IsManaged = v.IsManaged,
                IsRequired = v.IsRequired,
                HasOverride = v.CurrentValueId.HasValue,
                IsMissing = v.IsRequired && v.CurrentValue == null && v.DefaultValue == null
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the environment_variables_list tool.
/// </summary>
public sealed class EnvironmentVariablesListResult
{
    /// <summary>
    /// List of environment variable summaries.
    /// </summary>
    [JsonPropertyName("environmentVariables")]
    public List<EnvironmentVariableSummary> EnvironmentVariables { get; set; } = [];

    /// <summary>Total count of records.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Summary information about an environment variable.
/// </summary>
public sealed class EnvironmentVariableSummary
{
    /// <summary>
    /// The schema name of the environment variable.
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
    /// The variable type (String, Number, Boolean, JSON, DataSource, Secret).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// The default value defined on the variable.
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
    /// Whether the current value overrides the default (currentValue != null and differs from defaultValue).
    /// </summary>
    [JsonPropertyName("hasOverride")]
    public bool HasOverride { get; set; }

    /// <summary>
    /// Whether the variable is required but has no value (neither current nor default).
    /// </summary>
    [JsonPropertyName("isMissing")]
    public bool IsMissing { get; set; }
}
