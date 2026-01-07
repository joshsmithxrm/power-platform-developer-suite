using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Default implementation of <see cref="IServiceProviderFactory"/> that uses
/// <see cref="ProfileServiceFactory"/> to create service providers.
/// </summary>
public sealed class ProfileBasedServiceProviderFactory : IServiceProviderFactory
{
    /// <inheritdoc />
    public Task<ServiceProvider> CreateAsync(
        string? profileName,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        return ProfileServiceFactory.CreateFromProfileAsync(
            profileName,
            environmentUrl,
            deviceCodeCallback: deviceCodeCallback,
            cancellationToken: cancellationToken);
    }
}
