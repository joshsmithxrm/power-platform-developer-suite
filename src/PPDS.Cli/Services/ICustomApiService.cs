using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services;

/// <summary>
/// Service for managing Dataverse Custom APIs and their request/response parameters.
/// </summary>
public interface ICustomApiService
{
    /// <summary>
    /// Lists all Custom APIs in the environment.
    /// </summary>
    Task<List<CustomApiInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a Custom API by unique name or ID.
    /// </summary>
    /// <param name="uniqueNameOrId">The unique name or GUID (as string) of the Custom API.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<CustomApiInfo?> GetAsync(string uniqueNameOrId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new Custom API, optionally with request/response parameters.
    /// </summary>
    /// <param name="registration">Registration parameters.</param>
    /// <param name="progressReporter">Optional progress reporter for batch parameter creation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new Custom API ID.</returns>
    Task<Guid> RegisterAsync(
        CustomApiRegistration registration,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates mutable properties of an existing Custom API.
    /// All fields are optional; only non-null values are applied.
    /// </summary>
    Task UpdateAsync(Guid id, CustomApiUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters a Custom API and optionally cascade-deletes its parameters.
    /// </summary>
    /// <param name="id">The Custom API ID.</param>
    /// <param name="force">If true, cascade-delete parameters. If false, fails when parameters exist.</param>
    /// <param name="progressReporter">Optional progress reporter used during cascade delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnregisterAsync(
        Guid id,
        bool force = false,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a request parameter or response property to an existing Custom API.
    /// </summary>
    /// <param name="apiId">The Custom API ID.</param>
    /// <param name="parameter">Parameter registration details (Direction = "Request" or "Response").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new parameter/property ID.</returns>
    Task<Guid> AddParameterAsync(
        Guid apiId,
        CustomApiParameterRegistration parameter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates mutable properties of a request parameter or response property.
    /// </summary>
    /// <param name="parameterId">The parameter/property ID.</param>
    /// <param name="request">Update request (DisplayName, Description).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateParameterAsync(
        Guid parameterId,
        CustomApiParameterUpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a request parameter or response property by ID.
    /// </summary>
    Task RemoveParameterAsync(Guid parameterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read model for a Dataverse Custom API.
/// </summary>
public record CustomApiInfo
{
    /// <summary>The entity ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Unique API name (message name).</summary>
    public string UniqueName { get; init; } = "";

    /// <summary>Display name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Locale-independent name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Plugin type ID backing this API (if any).</summary>
    public Guid? PluginTypeId { get; init; }

    /// <summary>Friendly name of the backing plugin type.</summary>
    public string? PluginTypeName { get; init; }

    /// <summary>Binding type: Global, Entity, or EntityCollection.</summary>
    public string BindingType { get; init; } = "Global";

    /// <summary>Logical name of the bound entity (Entity/EntityCollection bindings only).</summary>
    public string? BoundEntity { get; init; }

    /// <summary>Allowed processing step type: None, AsyncOnly, or SyncAndAsync.</summary>
    public string AllowedProcessingStepType { get; init; } = "None";

    /// <summary>True when this API is a function (returns a value).</summary>
    public bool IsFunction { get; init; }

    /// <summary>True when this API is private.</summary>
    public bool IsPrivate { get; init; }

    /// <summary>Optional privilege name required to execute the API.</summary>
    public string? ExecutePrivilegeName { get; init; }

    /// <summary>True when this API is managed (part of a managed solution).</summary>
    public bool IsManaged { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTime? ModifiedOn { get; init; }

    /// <summary>Request parameters.</summary>
    public List<CustomApiParameterInfo> RequestParameters { get; init; } = [];

    /// <summary>Response properties.</summary>
    public List<CustomApiParameterInfo> ResponseProperties { get; init; } = [];
}

/// <summary>
/// Read model for a Custom API request parameter or response property.
/// </summary>
public record CustomApiParameterInfo
{
    /// <summary>The parameter/property ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Unique parameter name.</summary>
    public string UniqueName { get; init; } = "";

    /// <summary>Display name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Locale-independent name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Data type: Boolean, DateTime, Decimal, Entity, EntityCollection, EntityReference,
    /// Float, Integer, Money, Picklist, String, StringArray, or Guid.
    /// </summary>
    public string Type { get; init; } = "";

    /// <summary>Logical entity name (Entity/EntityCollection/EntityReference types only).</summary>
    public string? LogicalEntityName { get; init; }

    /// <summary>True when the parameter is optional (request parameters only).</summary>
    public bool IsOptional { get; init; }

    /// <summary>True when this parameter is managed.</summary>
    public bool IsManaged { get; init; }
}

/// <summary>
/// Parameters for registering a new Custom API.
/// </summary>
/// <param name="UniqueName">Unique message name (used as the API identifier).</param>
/// <param name="DisplayName">Display name shown in the UI.</param>
/// <param name="Name">Optional locale-independent name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="PluginTypeId">Plugin type ID that implements the API logic.</param>
/// <param name="BindingType">Binding: Global, Entity, or EntityCollection.</param>
/// <param name="BoundEntity">Bound entity logical name (required when BindingType = Entity or EntityCollection).</param>
/// <param name="IsFunction">True if the API returns a value (function).</param>
/// <param name="IsPrivate">True if the API is private.</param>
/// <param name="ExecutePrivilegeName">Optional privilege required to call the API.</param>
/// <param name="AllowedProcessingStepType">Allowed step type: None, AsyncOnly, or SyncAndAsync.</param>
/// <param name="Parameters">Optional initial parameters/properties to create alongside the API.</param>
public record CustomApiRegistration(
    string UniqueName,
    string DisplayName,
    string? Name,
    string? Description,
    Guid PluginTypeId,
    string? BindingType,
    string? BoundEntity,
    bool IsFunction,
    bool IsPrivate,
    string? ExecutePrivilegeName,
    string? AllowedProcessingStepType,
    List<CustomApiParameterRegistration>? Parameters = null);

/// <summary>
/// Request for updating an existing Custom API.
/// All fields are optional; only non-null values are applied.
/// </summary>
public record CustomApiUpdateRequest(
    string? DisplayName = null,
    string? Description = null,
    Guid? PluginTypeId = null,
    bool? IsFunction = null,
    bool? IsPrivate = null,
    string? ExecutePrivilegeName = null,
    string? AllowedProcessingStepType = null);

/// <summary>
/// Parameters for registering a request parameter or response property.
/// </summary>
/// <param name="UniqueName">Unique parameter name.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Name">Optional locale-independent name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Type">
/// Data type: Boolean, DateTime, Decimal, Entity, EntityCollection, EntityReference,
/// Float, Integer, Money, Picklist, String, StringArray, or Guid.
/// </param>
/// <param name="LogicalEntityName">Logical entity name (required for Entity/EntityCollection/EntityReference types).</param>
/// <param name="IsOptional">True when the parameter is optional (request parameters only).</param>
/// <param name="Direction">Direction: "Request" or "Response".</param>
public record CustomApiParameterRegistration(
    string UniqueName,
    string DisplayName,
    string? Name,
    string? Description,
    string Type,
    string? LogicalEntityName,
    bool IsOptional,
    string Direction);

/// <summary>
/// Request for updating an existing parameter or response property.
/// All fields are optional; only non-null values are applied.
/// </summary>
public record CustomApiParameterUpdateRequest(
    string? DisplayName = null,
    string? Description = null);
