using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that analyzes entity data.
/// </summary>
[McpServerToolType]
public sealed class DataAnalyzeTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataAnalyzeTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public DataAnalyzeTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Analyzes data for a Dataverse entity.
    /// </summary>
    /// <param name="entityName">Entity logical name to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis results including record count and sample data.</returns>
    [McpServerTool(Name = "ppds_data_analyze")]
    [Description("Analyze data for a Dataverse entity. Returns record count, primary attributes, and sample records. Use this to understand the data in an entity before querying.")]
    public async Task<DataAnalysisResult> ExecuteAsync(
        [Description("Entity logical name (e.g., 'account', 'contact')")]
        string entityName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            throw new ArgumentException("The 'entityName' parameter is required.", nameof(entityName));
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var metadataService = serviceProvider.GetRequiredService<IMetadataService>();
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        // Get entity metadata.
        var entity = await metadataService.GetEntityAsync(entityName, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Get record count.
        var countQuery = $@"
            <fetch aggregate=""true"">
                <entity name=""{entityName}"">
                    <attribute name=""{entity.PrimaryIdAttribute}"" alias=""count"" aggregate=""count"" />
                </entity>
            </fetch>";

        var countResult = await queryExecutor.ExecuteFetchXmlAsync(
            countQuery, null, null, false, cancellationToken).ConfigureAwait(false);

        var recordCount = 0;
        if (countResult.Records.Count > 0 && countResult.Records[0].TryGetValue("count", out var countVal))
        {
            if (countVal is QueryValue qv && qv.Value is int intVal)
            {
                recordCount = intVal;
            }
            else if (countVal is QueryValue qv2 && int.TryParse(qv2.Value?.ToString(), out var parsed))
            {
                recordCount = parsed;
            }
        }

        // Get sample records.
        var sampleAttributes = new List<string>();
        if (!string.IsNullOrEmpty(entity.PrimaryIdAttribute))
            sampleAttributes.Add(entity.PrimaryIdAttribute);
        if (!string.IsNullOrEmpty(entity.PrimaryNameAttribute))
            sampleAttributes.Add(entity.PrimaryNameAttribute);

        // Add a few more common attributes.
        var commonAttrs = new[] { "createdon", "modifiedon", "statecode", "statuscode", "ownerid" };
        foreach (var attr in commonAttrs)
        {
            if (entity.Attributes.Any(a => a.LogicalName == attr))
                sampleAttributes.Add(attr);
        }

        var attributeXml = string.Join("\n", sampleAttributes.Distinct().Select(a => $@"<attribute name=""{a}"" />"));
        var sampleQuery = $@"
            <fetch top=""5"">
                <entity name=""{entityName}"">
                    {attributeXml}
                    <order attribute=""createdon"" descending=""true"" />
                </entity>
            </fetch>";

        var sampleResult = await queryExecutor.ExecuteFetchXmlAsync(
            sampleQuery, null, null, false, cancellationToken).ConfigureAwait(false);

        return new DataAnalysisResult
        {
            EntityName = entityName,
            DisplayName = entity.DisplayName,
            RecordCount = recordCount,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            AttributeCount = entity.Attributes.Count,
            CustomAttributeCount = entity.Attributes.Count(a => a.IsCustomAttribute),
            SampleRecords = sampleResult.Records.Select(r =>
                r.ToDictionary(
                    kvp => kvp.Key,
                    kvp => MapValue(kvp.Value))).ToList()
        };
    }

    private static object? MapValue(object? value)
    {
        if (value is QueryValue qv)
        {
            if (qv.FormattedValue != null)
            {
                return new Dictionary<string, object?>
                {
                    ["value"] = qv.Value,
                    ["formatted"] = qv.FormattedValue
                };
            }
            return qv.Value;
        }
        return value;
    }
}

/// <summary>
/// Result of the data_analyze tool.
/// </summary>
public sealed class DataAnalysisResult
{
    /// <summary>
    /// Entity logical name.
    /// </summary>
    [JsonPropertyName("entityName")]
    public string EntityName { get; set; } = "";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Total number of records.
    /// </summary>
    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

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
    /// Total number of attributes.
    /// </summary>
    [JsonPropertyName("attributeCount")]
    public int AttributeCount { get; set; }

    /// <summary>
    /// Number of custom attributes.
    /// </summary>
    [JsonPropertyName("customAttributeCount")]
    public int CustomAttributeCount { get; set; }

    /// <summary>
    /// Sample records (5 most recent).
    /// </summary>
    [JsonPropertyName("sampleRecords")]
    public List<Dictionary<string, object?>> SampleRecords { get; set; } = [];
}
