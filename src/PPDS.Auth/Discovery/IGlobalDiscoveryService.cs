using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Service for discovering Dataverse environments via the Global Discovery Service.
/// </summary>
public interface IGlobalDiscoveryService
{
    /// <summary>
    /// Discovers all environments accessible to the authenticated user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of discovered environments.</returns>
    Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default);
}
