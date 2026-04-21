using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
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
        try
        {
            await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
            var service = serviceProvider.GetRequiredService<IDataProviderService>();

            // Delegate to the service layer — same path as CLI `data-providers list` (Constitution A2).
            var dataSources = await service.ListDataSourcesAsync(cancellationToken).ConfigureAwait(false);
            var dataProviders = await service.ListDataProvidersAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var dataSourceSummaries = dataSources.Select(ds => new DataSourceSummary
            {
                Id = ds.Id,
                Name = ds.Name
            }).ToList();

            var dataProviderSummaries = dataProviders.Select(dp => new DataProviderSummary
            {
                Id = dp.Id,
                Name = dp.Name,
                DataSourceName = dp.DataSourceName,
                RetrievePlugin = dp.RetrievePlugin,
                RetrieveMultiplePlugin = dp.RetrieveMultiplePlugin,
                CreatePlugin = dp.CreatePlugin,
                UpdatePlugin = dp.UpdatePlugin,
                DeletePlugin = dp.DeletePlugin,
                IsManaged = dp.IsManaged
            }).ToList();

            return new DataProvidersListResult
            {
                DataSources = dataSourceSummaries,
                DataProviders = dataProviderSummaries,
                DataSourceCount = dataSourceSummaries.Count,
                DataProviderCount = dataProviderSummaries.Count
            };
        }
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
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
}
