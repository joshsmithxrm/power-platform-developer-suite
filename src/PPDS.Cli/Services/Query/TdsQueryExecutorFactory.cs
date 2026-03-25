using Microsoft.Extensions.Logging;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Creates <see cref="TdsQueryExecutor"/> instances from an <see cref="AuthProfile"/>.
/// Shared between daemon and CLI DI registration to avoid duplicating the token provider wiring.
/// </summary>
public static class TdsQueryExecutorFactory
{
    /// <summary>
    /// Creates a <see cref="TdsQueryExecutor"/> for the given profile and environment.
    /// </summary>
    /// <param name="profile">The auth profile to use for token acquisition.</param>
    /// <param name="environmentUrl">The Dataverse environment URL.</param>
    /// <param name="credentialStore">Credential store for client secret lookup.</param>
    /// <param name="logger">Optional logger.</param>
    public static TdsQueryExecutor Create(
        AuthProfile profile,
        string environmentUrl,
        ISecureCredentialStore credentialStore,
        ILogger<TdsQueryExecutor>? logger = null)
    {
        IPowerPlatformTokenProvider tokenProvider;
        if (profile.AuthMethod == AuthMethod.ClientSecret)
        {
#pragma warning disable PPDS012
            var storedCredential = credentialStore.GetAsync(profile.ApplicationId ?? "").GetAwaiter().GetResult();
#pragma warning restore PPDS012
            tokenProvider = PowerPlatformTokenProvider.FromProfileWithSecret(profile, storedCredential?.ClientSecret ?? "");
        }
        else
        {
            tokenProvider = PowerPlatformTokenProvider.FromProfile(profile);
        }

        Func<CancellationToken, Task<string>> tdsTokenFunc = async ct =>
        {
            var token = await tokenProvider.GetTokenForResourceAsync(environmentUrl, ct)
                .ConfigureAwait(false);
            return token.AccessToken;
        };

        return new TdsQueryExecutor(environmentUrl, tdsTokenFunc, logger);
    }
}
