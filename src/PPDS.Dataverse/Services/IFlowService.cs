using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for cloud flow (Power Automate) operations.
/// </summary>
public interface IFlowService
{
    /// <summary>
    /// Lists cloud flows (Modern Flows and Desktop Flows).
    /// </summary>
    /// <param name="solutionName">Optional solution filter (unique name).</param>
    /// <param name="state">Optional state filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of cloud flows.</returns>
    Task<List<FlowInfo>> ListAsync(
        string? solutionName = null,
        FlowState? state = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific flow by unique name.
    /// </summary>
    /// <param name="uniqueName">The unique name of the flow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow info, or null if not found.</returns>
    Task<FlowInfo?> GetAsync(
        string uniqueName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific flow by ID.
    /// </summary>
    /// <param name="id">The workflow ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow info, or null if not found.</returns>
    Task<FlowInfo?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Cloud flow information.
/// </summary>
public sealed record FlowInfo
{
    /// <summary>Gets the workflow ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the unique name (schema name).</summary>
    public required string UniqueName { get; init; }

    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the flow state (Draft, Activated, Suspended).</summary>
    public required FlowState State { get; init; }

    /// <summary>Gets the flow category.</summary>
    public required FlowCategory Category { get; init; }

    /// <summary>Gets whether the flow is managed.</summary>
    public bool IsManaged { get; init; }

    /// <summary>
    /// Gets the connection reference logical names extracted from client data.
    /// These are the connection references that the flow depends on.
    /// </summary>
    public required List<string> ConnectionReferenceLogicalNames { get; init; }

    /// <summary>Gets the created date.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>Gets the modified date.</summary>
    public DateTime? ModifiedOn { get; init; }

    /// <summary>Gets the owner ID.</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>Gets the owner name.</summary>
    public string? OwnerName { get; init; }
}

/// <summary>
/// Cloud flow state.
/// </summary>
public enum FlowState
{
    /// <summary>Flow is in draft/design mode.</summary>
    Draft = 0,

    /// <summary>Flow is active and running.</summary>
    Activated = 1,

    /// <summary>Flow has been suspended.</summary>
    Suspended = 2
}

/// <summary>
/// Cloud flow category. Only cloud flow types are exposed.
/// </summary>
public enum FlowCategory
{
    /// <summary>Power Automate cloud flow.</summary>
    ModernFlow = 5,

    /// <summary>Power Automate desktop flow.</summary>
    DesktopFlow = 6
}
