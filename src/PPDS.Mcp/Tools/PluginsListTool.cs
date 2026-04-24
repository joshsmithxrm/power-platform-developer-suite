using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists registered plugins in the environment.
/// </summary>
[McpServerToolType]
public sealed class PluginsListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginsListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginsListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists registered plugin assemblies in the environment.
    /// </summary>
    /// <param name="nameFilter">Filter by assembly name (contains).</param>
    /// <param name="includeHidden">Include hidden system steps.</param>
    /// <param name="includeMicrosoft">Include Microsoft assemblies.</param>
    /// <param name="maxRows">Maximum assemblies to return (default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of plugin assemblies with types and steps.</returns>
    [McpServerTool(Name = "ppds_plugins_list")]
    [Description("List registered plugin assemblies in the Dataverse environment. Shows plugin types and their registered steps (message/entity/stage combinations). By default excludes hidden system assemblies and Microsoft.* assemblies.")]
    public async Task<PluginsListResult> ExecuteAsync(
        [Description("Filter by assembly name (partial match)")]
        string? nameFilter = null,
        [Description("Include hidden system steps (default false)")]
        bool includeHidden = false,
        [Description("Include Microsoft assemblies (default false)")]
        bool includeMicrosoft = false,
        [Description("Maximum assemblies to return (default 50, max 200)")]
        int maxRows = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            maxRows = Math.Clamp(maxRows, 1, 200);

            await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            var listOptions = new PluginListOptions(
                IncludeHidden: includeHidden,
                IncludeMicrosoft: includeMicrosoft);

            // Delegate to the service layer — same path as CLI `plugins list` (Constitution A2).
            var assemblies = await registrationService.ListAssembliesAsync(nameFilter, listOptions, cancellationToken)
                .ConfigureAwait(false);

            // Apply maxRows cap after retrieval (service does not support top-N directly).
            var pagedAssemblies = assemblies.Take(maxRows).ToList();

            var results = new List<PluginAssemblyResult>(pagedAssemblies.Count);
            foreach (var assembly in pagedAssemblies)
            {
                var assemblyResult = new PluginAssemblyResult
                {
                    Id = assembly.Id,
                    Name = assembly.Name,
                    Version = assembly.Version,
                    PublicKeyToken = assembly.PublicKeyToken,
                    IsolationMode = MapIsolationMode(assembly.IsolationMode),
                    SourceType = MapSourceType(assembly.SourceType),
                    Types = []
                };

                var types = await registrationService.ListTypesForAssemblyAsync(assembly.Id, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var type in types)
                {
                    var typeResult = new PluginTypeResult
                    {
                        Id = type.Id,
                        TypeName = type.TypeName,
                        FriendlyName = type.FriendlyName,
                        Steps = []
                    };

                    var steps = await registrationService.ListStepsForTypeAsync(type.Id, listOptions, cancellationToken)
                        .ConfigureAwait(false);

                    foreach (var step in steps)
                    {
                        typeResult.Steps.Add(new PluginStepResult
                        {
                            Name = step.Name,
                            Message = step.Message,
                            Entity = step.PrimaryEntity,
                            Stage = step.Stage,
                            IsEnabled = step.IsEnabled
                        });
                    }

                    assemblyResult.Types.Add(typeResult);
                }

                results.Add(assemblyResult);
            }

            return new PluginsListResult
            {
                Assemblies = results,
                Count = results.Count,
                TotalCount = assemblies.Count
            };
        }
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
    }

    private static string MapIsolationMode(int value) => value switch
    {
        1 => "None",
        2 => "Sandbox",
        _ => value.ToString()
    };

    private static string MapSourceType(int value) => value switch
    {
        0 => "Database",
        1 => "Disk",
        2 => "Normal",
        _ => value.ToString()
    };

}

/// <summary>
/// Result of the plugins_list tool.
/// </summary>
public sealed class PluginsListResult
{
    /// <summary>
    /// List of plugin assemblies.
    /// </summary>
    [JsonPropertyName("assemblies")]
    public List<PluginAssemblyResult> Assemblies { get; set; } = [];

    /// <summary>
    /// Number of assemblies returned.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>Total count of assemblies matching the filter (before top limit).</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Plugin assembly information.
/// </summary>
public sealed class PluginAssemblyResult
{
    /// <summary>
    /// Assembly ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Assembly name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Assembly version.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>
    /// Public key token.
    /// </summary>
    [JsonPropertyName("publicKeyToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKeyToken { get; set; }

    /// <summary>
    /// Isolation mode (None, Sandbox).
    /// </summary>
    [JsonPropertyName("isolationMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IsolationMode { get; set; }

    /// <summary>
    /// Source type (Database, Disk, GAC).
    /// </summary>
    [JsonPropertyName("sourceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceType { get; set; }

    /// <summary>
    /// Plugin types in this assembly.
    /// </summary>
    [JsonPropertyName("types")]
    public List<PluginTypeResult> Types { get; set; } = [];
}

/// <summary>
/// Plugin type information.
/// </summary>
public sealed class PluginTypeResult
{
    /// <summary>
    /// Plugin type ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Full type name (namespace.classname).
    /// </summary>
    [JsonPropertyName("typeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    /// <summary>
    /// Friendly name.
    /// </summary>
    [JsonPropertyName("friendlyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FriendlyName { get; set; }

    /// <summary>
    /// Registered steps.
    /// </summary>
    [JsonPropertyName("steps")]
    public List<PluginStepResult> Steps { get; set; } = [];
}

/// <summary>
/// Plugin step information.
/// </summary>
public sealed class PluginStepResult
{
    /// <summary>
    /// Step name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Message name (Create, Update, etc.).
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>
    /// Primary entity.
    /// </summary>
    [JsonPropertyName("entity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity { get; set; }

    /// <summary>
    /// Execution stage (PreValidation, PreOperation, PostOperation).
    /// </summary>
    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; set; }

    /// <summary>
    /// Whether the step is enabled.
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }
}
