using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Service for discovering Dataverse environments via the Global Discovery Service.
/// </summary>
// The `new` redeclaration is preserved intentionally: the method is part of the shipped
// public API surface for IGlobalDiscoveryService. Removing it now would emit a *REMOVED*
// PublicAPI entry and is a breaking change for the analyzer even though binding is unchanged.
public interface IGlobalDiscoveryService : IEnvironmentDiscoveryService
{
    /// <summary>
    /// Discovers all environments accessible to the authenticated user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of discovered environments.</returns>
    new Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default);
}
