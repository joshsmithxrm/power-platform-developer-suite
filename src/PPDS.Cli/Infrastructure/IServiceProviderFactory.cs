using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Factory for creating service providers with Dataverse connection pools.
/// Abstracted to enable mock injection for TUI testing.
/// </summary>
/// <remarks>
/// See ADR-0028 for rationale on TUI testability architecture.
/// </remarks>
public interface IServiceProviderFactory
{
    /// <summary>
    /// Creates a service provider configured for the specified profile and environment.
    /// </summary>
    /// <param name="profileName">Profile name (null for active profile).</param>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="beforeInteractiveAuth">Callback invoked before browser opens for interactive auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured service provider with connection pool.</returns>
    Task<ServiceProvider> CreateAsync(
        string? profileName,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null,
        CancellationToken cancellationToken = default);
}
