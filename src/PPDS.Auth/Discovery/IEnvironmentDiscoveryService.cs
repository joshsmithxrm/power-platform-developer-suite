using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Common interface for services that discover Dataverse environments.
/// Implemented by both Global Discovery Service (delegated user auth) and BAP API (service principal auth).
/// </summary>
public interface IEnvironmentDiscoveryService
{
    /// <summary>
    /// Discovers all environments accessible to the authenticated principal.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of discovered environments.</returns>
    Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default);
}
