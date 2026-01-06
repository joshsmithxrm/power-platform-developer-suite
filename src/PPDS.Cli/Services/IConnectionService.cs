using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Cli.Services;

/// <summary>
/// Service for Power Platform connection operations via the Power Apps Admin API.
/// </summary>
/// <remarks>
/// <para>
/// Connections are managed through the Power Apps Admin API, not Dataverse.
/// Connection References (in Dataverse) reference Connections (in Power Apps API).
/// </para>
/// <para>
/// <strong>SPN Limitations:</strong> Service principals have limited access to the Connections API.
/// User-delegated authentication (interactive/device code) provides full functionality.
/// </para>
/// <para>
/// <strong>Note:</strong> This service lives in PPDS.Cli rather than PPDS.Dataverse because
/// it requires IPowerPlatformTokenProvider from PPDS.Auth, and PPDS.Dataverse does not
/// reference PPDS.Auth (to avoid circular dependencies).
/// </para>
/// </remarks>
public interface IConnectionService
{
    /// <summary>
    /// Lists connections from the Power Apps Admin API.
    /// </summary>
    /// <param name="connectorFilter">Optional filter by connector ID (e.g., "shared_commondataserviceforapps").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of connections.</returns>
    Task<List<ConnectionInfo>> ListAsync(
        string? connectorFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific connection by ID.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connection info, or null if not found.</returns>
    Task<ConnectionInfo?> GetAsync(
        string connectionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Power Platform connection information from the Power Apps Admin API.
/// </summary>
public sealed record ConnectionInfo
{
    /// <summary>Gets the connection ID (GUID format but stored as string).</summary>
    public required string ConnectionId { get; init; }

    /// <summary>Gets the display name of the connection.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the connector ID (e.g., "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps").</summary>
    public required string ConnectorId { get; init; }

    /// <summary>Gets the connector display name.</summary>
    public string? ConnectorDisplayName { get; init; }

    /// <summary>Gets the environment ID where the connection exists.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Gets the connection status.</summary>
    public ConnectionStatus Status { get; init; }

    /// <summary>Gets whether the connection is shared or personal.</summary>
    public bool IsShared { get; init; }

    /// <summary>Gets the creation date.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>Gets the last modified date.</summary>
    public DateTime? ModifiedOn { get; init; }

    /// <summary>Gets the creator's email address.</summary>
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Connection status.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>Connection is healthy and working.</summary>
    Connected,

    /// <summary>Connection has an error (credentials expired, etc.).</summary>
    Error,

    /// <summary>Connection status is unknown or not provided.</summary>
    Unknown
}
