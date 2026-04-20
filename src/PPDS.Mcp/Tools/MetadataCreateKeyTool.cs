using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that creates an alternate key on a Dataverse table.
/// </summary>
[McpServerToolType]
public sealed class MetadataCreateKeyTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCreateKeyTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataCreateKeyTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Creates an alternate key on a Dataverse table.
    /// </summary>
    /// <param name="solution">The unique name of the solution containing the table.</param>
    /// <param name="entityName">The logical name of the entity to add the key to.</param>
    /// <param name="schemaName">The schema name for the key.</param>
    /// <param name="displayName">The display name of the key.</param>
    /// <param name="attributesJson">Comma-separated column logical names or JSON array of strings (e.g., 'col1,col2' or '["col1","col2"]').</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the schema name of the created key.</returns>
    [McpServerTool(Name = "ppds_metadata_create_key")]
    [Description("Creates an alternate key on a Dataverse table. Attributes can be provided as a comma-separated list or a JSON array of column logical names. Supports dry-run mode.")]
    public async Task<MetadataCreateKeyResult> ExecuteAsync(
        [Description("Unique name of the solution containing the table.")] string solution,
        [Description("Logical name of the entity to add the key to.")] string entityName,
        [Description("Schema name for the key.")] string schemaName,
        [Description("Display name of the key.")] string displayName,
        [Description("Comma-separated column logical names or JSON array of strings (e.g., 'col1,col2' or '[\"col1\",\"col2\"]').")] string attributesJson,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (Context.IsReadOnly)
            throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

        await using var serviceProvider = await CreateScopeAsync(cancellationToken,
            (nameof(solution), solution),
            (nameof(entityName), entityName),
            (nameof(schemaName), schemaName),
            (nameof(displayName), displayName),
            (nameof(attributesJson), attributesJson)).ConfigureAwait(false);

        string[] attributes;

        var trimmed = attributesJson.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                attributes = JsonSerializer.Deserialize<string[]>(trimmed) ?? [];
            }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"Invalid attributesJson: {ex.Message}. Expected a JSON array of strings or comma-separated values.",
                    nameof(attributesJson), ex);
            }
        }
        else
        {
            attributes = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (attributes.Length == 0)
        {
            throw new ArgumentException(
                "At least one key attribute must be specified.",
                nameof(attributesJson));
        }

        var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

        var result = await service.CreateKeyAsync(new CreateKeyRequest
        {
            SolutionUniqueName = solution,
            EntityLogicalName = entityName,
            SchemaName = schemaName,
            DisplayName = displayName,
            KeyAttributes = attributes,
            DryRun = dryRun
        }, ct: cancellationToken).ConfigureAwait(false);

        return new MetadataCreateKeyResult
        {
            SchemaName = result.SchemaName,
            WasDryRun = result.WasDryRun
        };
    }
}

/// <summary>
/// Result of the metadata_create_key tool.
/// </summary>
public sealed class MetadataCreateKeyResult
{
    /// <summary>The schema name of the created key.</summary>
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }
}
