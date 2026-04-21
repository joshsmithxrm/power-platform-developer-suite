using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.DependencyInjection;

/// <summary>
/// Extension methods for registering PPDS Auth services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers PPDS Auth connection-independent services as singletons.
    /// Call this once per DI container — stores are file-based singletons
    /// that should be shared across the application lifetime.
    /// </summary>
    /// <remarks>
    /// Uses TryAdd semantics so callers that pre-register a specialized
    /// <see cref="ISecureCredentialStore"/> (e.g., the daemon's per-profile
    /// instance, or an MCP tool context's captured store) keep their
    /// registration even when AddCliApplicationServices later chains into this.
    /// </remarks>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ProfileStore>();
        services.TryAddSingleton<EnvironmentConfigStore>();
        services.TryAddSingleton<ISecureCredentialStore, NativeCredentialStore>();

        return services;
    }
}
