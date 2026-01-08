using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that retrieves entity schema information.
/// </summary>
[McpServerToolType]
public sealed class DataSchemaTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataSchemaTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public DataSchemaTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Gets the schema (fields/attributes) for a Dataverse entity.
    /// </summary>
    /// <param name="entityName">Logical name of the entity (e.g., 'account', 'contact').</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Entity schema with attributes.</returns>
    [McpServerTool(Name = "ppds_data_schema")]
    [Description("Get the schema (fields/attributes) for a Dataverse entity. Returns attribute names, types, and metadata. Use this to understand entity structure before querying.")]
    public async Task<DataSchemaResult> ExecuteAsync(
        [Description("Entity logical name (e.g., 'account', 'contact', 'opportunity')")]
        string entityName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            throw new ArgumentException("The 'entityName' parameter is required.", nameof(entityName));
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var metadataService = serviceProvider.GetRequiredService<IMetadataService>();

        var entity = await metadataService.GetEntityAsync(entityName, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DataSchemaResult
        {
            EntityLogicalName = entity.LogicalName,
            DisplayName = entity.DisplayName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            Attributes = entity.Attributes.Select(a => new SchemaAttributeInfo
            {
                LogicalName = a.LogicalName,
                DisplayName = a.DisplayName,
                AttributeType = a.AttributeType,
                IsCustomAttribute = a.IsCustomAttribute,
                IsPrimaryId = a.LogicalName == entity.PrimaryIdAttribute,
                IsPrimaryName = a.LogicalName == entity.PrimaryNameAttribute,
                MaxLength = a.MaxLength,
                MinValue = a.MinValue,
                MaxValue = a.MaxValue,
                Precision = a.Precision,
                TargetEntities = a.Targets?.ToList(),
                OptionSetValues = a.Options?.Select(o => new OptionSetValue
                {
                    Value = o.Value,
                    Label = o.Label
                }).ToList()
            }).OrderBy(a => a.LogicalName).ToList()
        };
    }
}

/// <summary>
/// Result of the data_schema tool.
/// </summary>
public sealed class DataSchemaResult
{
    /// <summary>
    /// Entity logical name.
    /// </summary>
    [JsonPropertyName("entityLogicalName")]
    public string EntityLogicalName { get; set; } = "";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Primary ID attribute name.
    /// </summary>
    [JsonPropertyName("primaryIdAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryIdAttribute { get; set; }

    /// <summary>
    /// Primary name attribute name.
    /// </summary>
    [JsonPropertyName("primaryNameAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryNameAttribute { get; set; }

    /// <summary>
    /// List of attributes.
    /// </summary>
    [JsonPropertyName("attributes")]
    public List<SchemaAttributeInfo> Attributes { get; set; } = [];
}

/// <summary>
/// Information about an entity attribute.
/// </summary>
public sealed class SchemaAttributeInfo
{
    /// <summary>
    /// Attribute logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Attribute type (String, Integer, Money, Lookup, OptionSet, etc.).
    /// </summary>
    [JsonPropertyName("attributeType")]
    public string AttributeType { get; set; } = "";

    /// <summary>
    /// Whether this is a custom attribute.
    /// </summary>
    [JsonPropertyName("isCustomAttribute")]
    public bool IsCustomAttribute { get; set; }

    /// <summary>
    /// Whether this is the primary ID attribute.
    /// </summary>
    [JsonPropertyName("isPrimaryId")]
    public bool IsPrimaryId { get; set; }

    /// <summary>
    /// Whether this is the primary name attribute.
    /// </summary>
    [JsonPropertyName("isPrimaryName")]
    public bool IsPrimaryName { get; set; }

    /// <summary>
    /// Maximum length for string attributes.
    /// </summary>
    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    /// <summary>
    /// Minimum value for numeric attributes.
    /// </summary>
    [JsonPropertyName("minValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MinValue { get; set; }

    /// <summary>
    /// Maximum value for numeric attributes.
    /// </summary>
    [JsonPropertyName("maxValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MaxValue { get; set; }

    /// <summary>
    /// Decimal precision for money/decimal attributes.
    /// </summary>
    [JsonPropertyName("precision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Precision { get; set; }

    /// <summary>
    /// Target entities for lookup attributes.
    /// </summary>
    [JsonPropertyName("targetEntities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TargetEntities { get; set; }

    /// <summary>
    /// Option set values for picklist attributes.
    /// </summary>
    [JsonPropertyName("optionSetValues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OptionSetValue>? OptionSetValues { get; set; }
}

/// <summary>
/// Option set value information.
/// </summary>
public sealed class OptionSetValue
{
    /// <summary>
    /// Numeric value.
    /// </summary>
    [JsonPropertyName("value")]
    public int Value { get; set; }

    /// <summary>
    /// Display label.
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}
