using Microsoft.Extensions.DependencyInjection;
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
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<EnvironmentConfigStore>();
        services.AddSingleton<ISecureCredentialStore, NativeCredentialStore>();

        return services;
    }
}
