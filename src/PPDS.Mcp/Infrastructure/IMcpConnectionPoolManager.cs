using PPDS.Auth.Credentials;
using PPDS.Dataverse.Pooling;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Manages cached connection pools for the MCP server, keyed by profile+environment combination.
/// Pools are long-lived and reused across MCP tool invocations.
/// </summary>
public interface IMcpConnectionPoolManager : IAsyncDisposable
{
    /// <summary>
    /// Gets or creates a connection pool for the given profiles and environment.
    /// </summary>
    /// <param name="profileNames">List of profile names (for multi-profile pooling). Order does not matter.</param>
    /// <param name="environmentUrl">Environment URL (will be normalized).</param>
    /// <param name="deviceCodeCallback">Optional callback for device code flow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A cached connection pool.</returns>
    Task<IDataverseConnectionPool> GetOrCreatePoolAsync(
        IReadOnlyList<string> profileNames,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates all pools that use the specified profile.
    /// Call after profile is modified or deleted.
    /// </summary>
    /// <param name="profileName">The profile name to invalidate.</param>
    void InvalidateProfile(string profileName);

    /// <summary>
    /// Invalidates all pools for a specific environment.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to invalidate.</param>
    void InvalidateEnvironment(string environmentUrl);
}
