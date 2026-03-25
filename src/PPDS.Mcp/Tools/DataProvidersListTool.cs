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
public sealed class DataProvidersListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DataProvidersListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public DataProvidersListTool(McpToolContext context) : base(context) { }

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

        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        // Execute queries sequentially (parallel would require .Result which is blocked by PPDS012)
        var dataSourceResult = await queryExecutor.ExecuteFetchXmlAsync(dataSourceFetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
        var dataProviderResult = await queryExecutor.ExecuteFetchXmlAsync(dataProviderFetchXml, null, null, false, cancellationToken).ConfigureAwait(false);

        var dataSources = dataSourceResult.Records.Select(record => new DataSourceSummary
        {
            Id = record.GetGuid("entitydatasourceid"),
            Name = record.GetString("name") ?? ""
        }).ToList();

        var dataProviders = dataProviderResult.Records.Select(record => new DataProviderSummary
        {
            Id = record.GetGuid("entitydataproviderid"),
            Name = record.GetString("name") ?? "",
            DataSourceName = record.GetString("datasourcelogicalname"),
            RetrievePlugin = record.GetGuidNullable("retrieveplugin"),
            RetrieveMultiplePlugin = record.GetGuidNullable("retrievemultipleplugin"),
            CreatePlugin = record.GetGuidNullable("createplugin"),
            UpdatePlugin = record.GetGuidNullable("updateplugin"),
            DeletePlugin = record.GetGuidNullable("deleteplugin"),
            IsManaged = record.GetBool("ismanaged"),
            CreatedOn = record.GetDateTime("createdon"),
            ModifiedOn = record.GetDateTime("modifiedon")
        }).ToList();

        return new DataProvidersListResult
        {
            DataSources = dataSources,
            DataProviders = dataProviders,
            DataSourceCount = dataSources.Count,
            DataProviderCount = dataProviders.Count
        };
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
/// Note: entitydatasource is a metadata entity with limited queryable attributes (only name).
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
