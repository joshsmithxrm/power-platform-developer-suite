using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that sets the current value of an environment variable.
/// </summary>
[McpServerToolType]
public sealed class EnvironmentVariablesSetTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentVariablesSetTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public EnvironmentVariablesSetTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Sets the current value of an environment variable.
    /// </summary>
    /// <param name="schemaName">The schema name of the environment variable.</param>
    /// <param name="value">The new value to set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success or failure.</returns>
    [McpServerTool(Name = "ppds_environment_variables_set")]
    [Description("Set the current value of an environment variable. Creates the value record if none exists. AI agents can use this to fix misconfigurations during deployment troubleshooting.")]
    public async Task<EnvironmentVariablesSetResult> ExecuteAsync(
        [Description("Schema name of the environment variable")]
        string schemaName,
        [Description("New value to set")]
        string value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new ArgumentException("schemaName is required.", nameof(schemaName));
        }

        if (_context.IsReadOnly)
        {
            throw new InvalidOperationException(
                "Cannot set environment variable value: this MCP session is read-only.");
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IEnvironmentVariableService>();

        var success = await service.SetValueAsync(schemaName, value, cancellationToken).ConfigureAwait(false);

        return new EnvironmentVariablesSetResult
        {
            Success = success,
            SchemaName = schemaName,
            Message = success
                ? $"Environment variable '{schemaName}' value updated successfully."
                : $"Environment variable '{schemaName}' not found."
        };
    }
}

/// <summary>
/// Result of the environment_variables_set tool.
/// </summary>
public sealed class EnvironmentVariablesSetResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// The schema name of the variable that was set.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    /// <summary>
    /// Human-readable result message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
