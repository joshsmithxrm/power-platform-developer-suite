using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for connection reference operations in Dataverse.
/// </summary>
/// <remarks>
/// Connection references are Dataverse entities that reference Power Platform connections.
/// This service provides CRUD operations and relationship analysis with flows.
/// </remarks>
public interface IConnectionReferenceService
{
    /// <summary>
    /// Lists connection references.
    /// </summary>
    /// <param name="solutionName">Optional solution filter (unique name).</param>
    /// <param name="unboundOnly">If true, only return connection references without a bound connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of connection references.</returns>
    Task<List<ConnectionReferenceInfo>> ListAsync(
        string? solutionName = null,
        bool unboundOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific connection reference by logical name.
    /// </summary>
    /// <param name="logicalName">The connection reference logical name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connection reference info, or null if not found.</returns>
    Task<ConnectionReferenceInfo?> GetAsync(
        string logicalName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific connection reference by ID.
    /// </summary>
    /// <param name="id">The connection reference ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connection reference info, or null if not found.</returns>
    Task<ConnectionReferenceInfo?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all flows that use a specific connection reference.
    /// </summary>
    /// <param name="logicalName">The connection reference logical name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of flows that reference this connection reference.</returns>
    Task<List<FlowInfo>> GetFlowsUsingAsync(
        string logicalName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes flow-connection reference relationships within a solution.
    /// Detects orphaned flows (referencing missing CRs) and orphaned CRs (not used by any flow).
    /// </summary>
    /// <param name="solutionName">Optional solution filter (unique name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of relationships including orphan detection.</returns>
    Task<FlowConnectionAnalysis> AnalyzeAsync(
        string? solutionName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Connection reference information from Dataverse.
/// </summary>
public sealed record ConnectionReferenceInfo
{
    /// <summary>Gets the connection reference ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the logical name (schema name).</summary>
    public required string LogicalName { get; init; }

    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the bound connection ID (from Power Apps API).</summary>
    public string? ConnectionId { get; init; }

    /// <summary>Gets the connector ID.</summary>
    public string? ConnectorId { get; init; }

    /// <summary>Gets whether this is a managed component.</summary>
    public bool IsManaged { get; init; }

    /// <summary>Gets whether a connection is bound (ConnectionId is set).</summary>
    public bool IsBound => !string.IsNullOrEmpty(ConnectionId);

    /// <summary>Gets the created date.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>Gets the modified date.</summary>
    public DateTime? ModifiedOn { get; init; }
}

/// <summary>
/// Result of analyzing flow-connection reference relationships.
/// </summary>
public sealed record FlowConnectionAnalysis
{
    /// <summary>Gets all relationship entries.</summary>
    public required List<FlowConnectionRelationship> Relationships { get; init; }

    /// <summary>Gets the count of valid flow-to-CR relationships.</summary>
    public int ValidCount => Relationships.Count(r => r.Type == RelationshipType.FlowToConnectionReference);

    /// <summary>Gets the count of orphaned flows (referencing missing CRs).</summary>
    public int OrphanedFlowCount => Relationships.Count(r => r.Type == RelationshipType.OrphanedFlow);

    /// <summary>Gets the count of orphaned CRs (not used by any flow).</summary>
    public int OrphanedConnectionReferenceCount => Relationships.Count(r => r.Type == RelationshipType.OrphanedConnectionReference);

    /// <summary>Gets whether any orphans were detected.</summary>
    public bool HasOrphans => OrphanedFlowCount > 0 || OrphanedConnectionReferenceCount > 0;
}

/// <summary>
/// Represents a relationship between a flow and a connection reference.
/// </summary>
public sealed record FlowConnectionRelationship
{
    /// <summary>Gets the relationship type.</summary>
    public required RelationshipType Type { get; init; }

    /// <summary>Gets the flow unique name (null for OrphanedConnectionReference).</summary>
    public string? FlowUniqueName { get; init; }

    /// <summary>Gets the flow display name.</summary>
    public string? FlowDisplayName { get; init; }

    /// <summary>Gets the connection reference logical name (null for OrphanedFlow).</summary>
    public string? ConnectionReferenceLogicalName { get; init; }

    /// <summary>Gets the connection reference display name.</summary>
    public string? ConnectionReferenceDisplayName { get; init; }

    /// <summary>Gets the connector ID.</summary>
    public string? ConnectorId { get; init; }

    /// <summary>Gets whether the connection reference is bound to a connection.</summary>
    public bool? IsBound { get; init; }
}

/// <summary>
/// Types of flow-connection reference relationships.
/// </summary>
public enum RelationshipType
{
    /// <summary>Valid relationship: flow uses an existing connection reference.</summary>
    FlowToConnectionReference,

    /// <summary>Orphaned flow: references a connection reference that doesn't exist.</summary>
    OrphanedFlow,

    /// <summary>Orphaned connection reference: exists but not used by any flow.</summary>
    OrphanedConnectionReference
}
