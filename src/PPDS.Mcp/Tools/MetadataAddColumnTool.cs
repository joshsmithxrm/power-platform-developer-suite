using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that adds a new column (attribute) to a Dataverse table.
/// </summary>
[McpServerToolType]
public sealed class MetadataAddColumnTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataAddColumnTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataAddColumnTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Adds a new column (attribute) to a Dataverse table.
    /// </summary>
    /// <param name="solution">The unique name of the solution containing the table.</param>
    /// <param name="entityName">The logical name of the entity to add the column to.</param>
    /// <param name="schemaName">The schema name for the new column.</param>
    /// <param name="displayName">The display name of the column.</param>
    /// <param name="type">The column type (String, Integer, Decimal, Double, Money, Boolean, DateTime, Choice, Choices, Memo, BigInt, Image, File, Lookup).</param>
    /// <param name="description">Description of the column.</param>
    /// <param name="maxLength">Maximum length for String/Memo columns.</param>
    /// <param name="minValue">Minimum value for numeric columns (Integer, Decimal, Double, Money).</param>
    /// <param name="maxValue">Maximum value for numeric columns (Integer, Decimal, Double, Money).</param>
    /// <param name="precision">Precision for Decimal, Double, and Money columns.</param>
    /// <param name="format">Format for String, Integer, or DateTime columns.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the logical name and metadata ID of the created column.</returns>
    [McpServerTool(Name = "ppds_metadata_add_column")]
    [Description("Adds a new column (attribute) to an existing Dataverse table. Supports all standard column types. Use dry-run to validate before committing.")]
    public async Task<MetadataAddColumnResult> ExecuteAsync(
        [Description("Unique name of the solution containing the table.")] string solution,
        [Description("Logical name of the entity to add the column to.")] string entityName,
        [Description("Schema name for the new column.")] string schemaName,
        [Description("Display name of the column.")] string displayName,
        [Description("Column type: String, Integer, Decimal, Double, Money, Boolean, DateTime, Choice, Choices, Memo, BigInt, Image, File, Lookup.")] string type,
        [Description("Description of the column (optional).")] string? description = null,
        [Description("Maximum length for String/Memo columns (optional).")] int? maxLength = null,
        [Description("Minimum value for numeric columns (optional).")] double? minValue = null,
        [Description("Maximum value for numeric columns (optional).")] double? maxValue = null,
        [Description("Precision for Decimal, Double, and Money columns (optional).")] int? precision = null,
        [Description("Format for String, Integer, or DateTime columns (optional).")] string? format = null,
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
            (nameof(type), type)).ConfigureAwait(false);

        if (!Enum.TryParse<SchemaColumnType>(type, ignoreCase: true, out var columnType))
        {
            throw new ArgumentException(
                $"Invalid column type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<SchemaColumnType>())}",
                nameof(type));
        }

        var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

        var result = await service.CreateColumnAsync(new CreateColumnRequest
        {
            SolutionUniqueName = solution,
            EntityLogicalName = entityName,
            SchemaName = schemaName,
            DisplayName = displayName,
            Description = description ?? "",
            ColumnType = columnType,
            MaxLength = maxLength,
            MinValue = minValue,
            MaxValue = maxValue,
            Precision = precision,
            Format = format,
            DryRun = dryRun
        }, ct: cancellationToken).ConfigureAwait(false);

        return new MetadataAddColumnResult
        {
            LogicalName = result.LogicalName,
            MetadataId = result.MetadataId == Guid.Empty ? null : result.MetadataId.ToString(),
            WasDryRun = result.WasDryRun
        };
    }
}

/// <summary>
/// Result of the metadata_add_column tool.
/// </summary>
public sealed class MetadataAddColumnResult
{
    /// <summary>The logical name of the created column.</summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>The metadata identifier of the created column.</summary>
    [JsonPropertyName("metadataId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataId { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }
}
