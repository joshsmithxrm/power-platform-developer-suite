namespace PPDS.Cli.Services;

/// <summary>
/// Service for managing Dataverse virtual entity data providers and data sources.
/// </summary>
public interface IDataProviderService
{
    // Data Sources

    /// <summary>
    /// Lists all data source entities in the environment.
    /// </summary>
    Task<List<DataSourceInfo>> ListDataSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a data source by name or ID.
    /// </summary>
    /// <param name="nameOrId">The logical name or GUID (as string) of the data source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DataSourceInfo?> GetDataSourceAsync(string nameOrId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new data source entity.
    /// </summary>
    /// <param name="registration">Registration parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new data source ID.</returns>
    Task<Guid> RegisterDataSourceAsync(DataSourceRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates mutable properties of an existing data source.
    /// All fields are optional; only non-null values are applied.
    /// </summary>
    Task UpdateDataSourceAsync(Guid id, DataSourceUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters a data source and cascade-deletes all child data providers.
    /// </summary>
    /// <param name="id">The data source ID.</param>
    /// <param name="force">If true, cascade delete all child data providers. If false, fails when providers exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnregisterDataSourceAsync(Guid id, bool force = false, CancellationToken cancellationToken = default);

    // Data Providers

    /// <summary>
    /// Lists all data providers, optionally filtered by data source.
    /// </summary>
    /// <param name="dataSourceId">Optional data source ID to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<DataProviderInfo>> ListDataProvidersAsync(Guid? dataSourceId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a data provider by name or ID.
    /// </summary>
    /// <param name="nameOrId">The name or GUID (as string) of the data provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DataProviderInfo?> GetDataProviderAsync(string nameOrId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new data provider with plugin operation bindings.
    /// </summary>
    /// <param name="registration">Registration parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new data provider ID.</returns>
    Task<Guid> RegisterDataProviderAsync(DataProviderRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates plugin bindings on an existing data provider.
    /// All fields are optional; only non-null values are applied.
    /// </summary>
    Task UpdateDataProviderAsync(Guid id, DataProviderUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters a data provider.
    /// </summary>
    Task UnregisterDataProviderAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read model for a Dataverse data source entity.
/// </summary>
public record DataSourceInfo
{
    /// <summary>The entity ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Logical name (e.g., <c>prefix_name</c>).</summary>
    public string Name { get; init; } = "";

    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>True when part of a managed solution.</summary>
    public bool IsManaged { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTime? ModifiedOn { get; init; }
}

/// <summary>
/// Read model for a Dataverse data provider.
/// </summary>
public record DataProviderInfo
{
    /// <summary>The entity ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Display name of the data provider.</summary>
    public string Name { get; init; } = "";

    /// <summary>Logical name of the associated data source.</summary>
    public string? DataSourceName { get; init; }

    /// <summary>Plugin type ID for Retrieve operations.</summary>
    public Guid? RetrievePlugin { get; init; }

    /// <summary>Plugin type ID for RetrieveMultiple operations.</summary>
    public Guid? RetrieveMultiplePlugin { get; init; }

    /// <summary>Plugin type ID for Create operations.</summary>
    public Guid? CreatePlugin { get; init; }

    /// <summary>Plugin type ID for Update operations.</summary>
    public Guid? UpdatePlugin { get; init; }

    /// <summary>Plugin type ID for Delete operations.</summary>
    public Guid? DeletePlugin { get; init; }

    /// <summary>True when part of a managed solution.</summary>
    public bool IsManaged { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTime? ModifiedOn { get; init; }
}

/// <summary>
/// Parameters for registering a new data source entity.
/// </summary>
/// <param name="Name">Logical name in the format <c>{prefix}_{name}</c>.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Description">Optional description.</param>
public record DataSourceRegistration(string Name, string DisplayName, string? Description);

/// <summary>
/// Request for updating an existing data source.
/// All fields are optional; only non-null values are applied.
/// </summary>
public record DataSourceUpdateRequest(string? DisplayName = null, string? Description = null);

/// <summary>
/// Parameters for registering a new data provider.
/// </summary>
/// <param name="Name">Display name of the data provider.</param>
/// <param name="DataSourceId">ID of the parent data source entity.</param>
/// <param name="RetrievePlugin">Optional plugin type ID for Retrieve operations.</param>
/// <param name="RetrieveMultiplePlugin">Optional plugin type ID for RetrieveMultiple operations.</param>
/// <param name="CreatePlugin">Optional plugin type ID for Create operations.</param>
/// <param name="UpdatePlugin">Optional plugin type ID for Update operations.</param>
/// <param name="DeletePlugin">Optional plugin type ID for Delete operations.</param>
public record DataProviderRegistration(
    string Name,
    Guid DataSourceId,
    Guid? RetrievePlugin,
    Guid? RetrieveMultiplePlugin,
    Guid? CreatePlugin,
    Guid? UpdatePlugin,
    Guid? DeletePlugin);

/// <summary>
/// Request for updating plugin bindings on an existing data provider.
/// All fields are optional; only non-null values are applied.
/// </summary>
public record DataProviderUpdateRequest(
    Guid? RetrievePlugin = null,
    Guid? RetrieveMultiplePlugin = null,
    Guid? CreatePlugin = null,
    Guid? UpdatePlugin = null,
    Guid? DeletePlugin = null);
