using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that creates a new Dataverse table (entity) in a solution.
/// </summary>
[McpServerToolType]
public sealed class MetadataCreateTableTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCreateTableTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataCreateTableTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Creates a new Dataverse table (entity) in the specified solution.
    /// </summary>
    /// <param name="solution">The unique name of the solution to add the table to.</param>
    /// <param name="schemaName">The schema name for the new table (e.g., 'new_MyTable').</param>
    /// <param name="displayName">The display name of the table.</param>
    /// <param name="pluralName">The plural display name of the table.</param>
    /// <param name="description">A description of the table.</param>
    /// <param name="ownershipType">Ownership type: 'UserOwned' or 'OrganizationOwned'.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the logical name and metadata ID of the created table.</returns>
    [McpServerTool(Name = "ppds_metadata_create_table")]
    [Description("Creates a new Dataverse table (entity) in the specified solution. Supports dry-run mode for validation without persisting changes. Use ppds_metadata_entities to verify the table was created.")]
    public async Task<MetadataCreateTableResult> ExecuteAsync(
        [Description("Unique name of the solution to add the table to.")] string solution,
        [Description("Schema name for the new table (e.g., 'new_MyTable'). Prefix is validated against the solution publisher.")] string schemaName,
        [Description("Display name of the table.")] string displayName,
        [Description("Plural display name of the table.")] string pluralName,
        [Description("Description of the table.")] string description,
        [Description("Ownership type: 'UserOwned' or 'OrganizationOwned'.")] string ownershipType,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (Context.IsReadOnly)
            throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

        await using var serviceProvider = await CreateScopeAsync(cancellationToken,
            (nameof(solution), solution),
            (nameof(schemaName), schemaName),
            (nameof(displayName), displayName),
            (nameof(pluralName), pluralName),
            (nameof(description), description),
            (nameof(ownershipType), ownershipType)).ConfigureAwait(false);

        var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

        var result = await service.CreateTableAsync(new CreateTableRequest
        {
            SolutionUniqueName = solution,
            SchemaName = schemaName,
            DisplayName = displayName,
            PluralDisplayName = pluralName,
            Description = description,
            OwnershipType = ownershipType,
            DryRun = dryRun
        }, ct: cancellationToken).ConfigureAwait(false);

        return new MetadataCreateTableResult
        {
            LogicalName = result.LogicalName,
            MetadataId = result.MetadataId == Guid.Empty ? null : result.MetadataId.ToString(),
            WasDryRun = result.WasDryRun,
            ValidationMessages = result.ValidationMessages.Select(v => new ValidationMessageDto
            {
                Field = v.Field,
                Rule = v.Rule,
                Message = v.Message
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the metadata_create_table tool.
/// </summary>
public sealed class MetadataCreateTableResult
{
    /// <summary>The logical name of the created table.</summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>The metadata identifier of the created table.</summary>
    [JsonPropertyName("metadataId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataId { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }

    /// <summary>Validation messages produced during the operation.</summary>
    [JsonPropertyName("validationMessages")]
    public List<ValidationMessageDto> ValidationMessages { get; set; } = [];
}

/// <summary>
/// A validation message DTO for MCP tool results.
/// </summary>
public sealed class ValidationMessageDto
{
    /// <summary>The name of the field that triggered the validation.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    /// <summary>The validation rule identifier.</summary>
    [JsonPropertyName("rule")]
    public string Rule { get; set; } = "";

    /// <summary>The human-readable validation message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
