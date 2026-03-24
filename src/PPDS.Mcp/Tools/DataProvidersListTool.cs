using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists virtual entity data sources and data providers.
/// </summary>
[McpServerToolType]
public sealed class DataProvidersListTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataProvidersListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public DataProvidersListTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Lists all virtual entity data sources and data providers in the environment.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of data sources and data providers.</returns>
    [McpServerTool(Name = "ppds_data_providers_list")]
    [Description("List all virtual entity data sources and data providers registered in the Dataverse environment. Data providers implement plugin-based virtual entity adapters that connect Dataverse virtual entities to external data sources. Returns both data source definitions and their associated data provider plugin bindings.")]
    public async Task<DataProvidersListResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        var dataSourceFetchXml = @"
            <fetch>
                <entity name=""entitydatasource"">
                    <attribute name=""entitydatasourceid"" />
                    <attribute name=""name"" />
                    <attribute name=""displayname"" />
                    <attribute name=""description"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <order attribute=""name"" />
                </entity>
            </fetch>";

        var dataProviderFetchXml = @"
            <fetch>
                <entity name=""entitydataprovider"">
                    <attribute name=""entitydataproviderid"" />
                    <attribute name=""name"" />
                    <attribute name=""datasourcelogicalname"" />
                    <attribute name=""retrieveplugin"" />
                    <attribute name=""retrievemultipleplugin"" />
                    <attribute name=""createplugin"" />
                    <attribute name=""updateplugin"" />
                    <attribute name=""deleteplugin"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <order attribute=""name"" />
                </entity>
            </fetch>";

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        // Execute queries sequentially (parallel would require .Result which is blocked by PPDS012)
        var dataSourceResult = await queryExecutor.ExecuteFetchXmlAsync(dataSourceFetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
        var dataProviderResult = await queryExecutor.ExecuteFetchXmlAsync(dataProviderFetchXml, null, null, false, cancellationToken).ConfigureAwait(false);

        var dataSources = dataSourceResult.Records.Select(record => new DataSourceSummary
        {
            Id = GetGuid(record, "entitydatasourceid"),
            Name = GetString(record, "name") ?? "",
            DisplayName = GetString(record, "displayname"),
            Description = GetString(record, "description"),
            IsManaged = GetBool(record, "ismanaged"),
            CreatedOn = GetDateTime(record, "createdon"),
            ModifiedOn = GetDateTime(record, "modifiedon")
        }).ToList();

        var dataProviders = dataProviderResult.Records.Select(record => new DataProviderSummary
        {
            Id = GetGuid(record, "entitydataproviderid"),
            Name = GetString(record, "name") ?? "",
            DataSourceName = GetString(record, "datasourcelogicalname"),
            RetrievePlugin = GetGuidNullable(record, "retrieveplugin"),
            RetrieveMultiplePlugin = GetGuidNullable(record, "retrievemultipleplugin"),
            CreatePlugin = GetGuidNullable(record, "createplugin"),
            UpdatePlugin = GetGuidNullable(record, "updateplugin"),
            DeletePlugin = GetGuidNullable(record, "deleteplugin"),
            IsManaged = GetBool(record, "ismanaged"),
            CreatedOn = GetDateTime(record, "createdon"),
            ModifiedOn = GetDateTime(record, "modifiedon")
        }).ToList();

        return new DataProvidersListResult
        {
            DataSources = dataSources,
            DataProviders = dataProviders,
            DataSourceCount = dataSources.Count,
            DataProviderCount = dataProviders.Count
        };
    }

    // Value extraction helpers

    private static Guid GetGuid(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is Guid g) return g;
            if (Guid.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return Guid.Empty;
    }

    private static Guid? GetGuidNullable(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is Guid g) return g;
            if (Guid.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static string? GetString(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv))
            return qv.Value?.ToString();
        return null;
    }

    private static bool GetBool(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is bool b) return b;
            if (bool.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return false;
    }

    private static DateTime? GetDateTime(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is DateTime dt) return dt;
            if (DateTime.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }
}

/// <summary>
/// Result of the ppds_data_providers_list tool.
/// </summary>
public sealed class DataProvidersListResult
{
    /// <summary>
    /// Data source entity definitions.
    /// </summary>
    [JsonPropertyName("dataSources")]
    public List<DataSourceSummary> DataSources { get; set; } = [];

    /// <summary>
    /// Data provider plugin bindings.
    /// </summary>
    [JsonPropertyName("dataProviders")]
    public List<DataProviderSummary> DataProviders { get; set; } = [];

    /// <summary>
    /// Number of data sources returned.
    /// </summary>
    [JsonPropertyName("dataSourceCount")]
    public int DataSourceCount { get; set; }

    /// <summary>
    /// Number of data providers returned.
    /// </summary>
    [JsonPropertyName("dataProviderCount")]
    public int DataProviderCount { get; set; }
}

/// <summary>
/// Summary information about a virtual entity data source.
/// </summary>
public sealed class DataSourceSummary
{
    /// <summary>
    /// Data source ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Logical name (e.g., prefix_name).
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// True when part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// UTC creation timestamp.
    /// </summary>
    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    /// <summary>
    /// UTC last-modified timestamp.
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Summary information about a virtual entity data provider.
/// </summary>
public sealed class DataProviderSummary
{
    /// <summary>
    /// Data provider ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Display name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Logical name of the associated data source entity.
    /// </summary>
    [JsonPropertyName("dataSourceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataSourceName { get; set; }

    /// <summary>
    /// Plugin type ID for Retrieve operations.
    /// </summary>
    [JsonPropertyName("retrievePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RetrievePlugin { get; set; }

    /// <summary>
    /// Plugin type ID for RetrieveMultiple operations.
    /// </summary>
    [JsonPropertyName("retrieveMultiplePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RetrieveMultiplePlugin { get; set; }

    /// <summary>
    /// Plugin type ID for Create operations.
    /// </summary>
    [JsonPropertyName("createPlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CreatePlugin { get; set; }

    /// <summary>
    /// Plugin type ID for Update operations.
    /// </summary>
    [JsonPropertyName("updatePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? UpdatePlugin { get; set; }

    /// <summary>
    /// Plugin type ID for Delete operations.
    /// </summary>
    [JsonPropertyName("deletePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DeletePlugin { get; set; }

    /// <summary>
    /// True when part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// UTC creation timestamp.
    /// </summary>
    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    /// <summary>
    /// UTC last-modified timestamp.
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}
