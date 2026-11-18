using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Dataverse.Pooling;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Local-only connection for in-process live tests through an existing PPDS auth profile (PPDS_TEST_PROFILE) instead of
/// a service-principal secret. Not the product's <c>PPDS.Auth.Pooling.ProfileConnectionSource</c>.
/// <para>
/// Only InteractiveBrowser profiles are accepted, because only that provider has a pre-authentication hook that can
/// refuse a prompt: the hook returns Cancel, so when silent acquisition fails (for the first token or for any later
/// refresh the client makes) the provider throws instead of opening a browser, and connecting fails with a clear
/// message. A device-code profile is refused because its provider would print a code and wait with no way to cancel.
/// </para>
/// <para>
/// The profile must be bound to DATAVERSE_URL (unless PPDS_TEST_ENVIRONMENT_OVERRIDE=1, see <see cref="EnvironmentOverride"/>),
/// and the organisation the client actually connected to must equal
/// DATAVERSE_URL before the source is handed out, so a test can never write to another environment. Nothing checks
/// the environment's type: pointing DATAVERSE_URL and the profile at a development environment is the caller's job.
/// </para>
/// <para>
/// The product code this uses loads the developer's real profile store and MSAL token cache and may refresh (rewrite)
/// that cache, exactly as the CLI does. This class never reads, prints or logs a token, the cache or a secret.
/// </para>
/// </summary>
public sealed class LiveTestProfileConnectionSource : IConnectionSource
{
    private readonly ServiceClientSource _inner;
    private readonly ICredentialProvider _provider;

    private LiveTestProfileConnectionSource(ServiceClientSource inner, ICredentialProvider provider)
    {
        _inner = inner;
        _provider = provider;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public int MaxPoolSize => _inner.MaxPoolSize;

    /// <inheritdoc />
    public ServiceClient GetSeedClient() => _inner.GetSeedClient();

    /// <inheritdoc />
    public void InvalidateSeed() => _inner.InvalidateSeed();

    /// <summary>
    /// Connects through the profile named by <see cref="LiveTestConfiguration.ProfileName"/> to
    /// <see cref="LiveTestConfiguration.DataverseUrl"/>, silently.
    /// </summary>
    public static async Task<LiveTestProfileConnectionSource> CreateAsync(LiveTestConfiguration config, string name, int maxPoolSize)
    {
        if (!config.HasProfileCredentials)
            throw new InvalidOperationException("Profile connection requires DATAVERSE_URL and PPDS_TEST_PROFILE.");

        var expected = Authority(config.DataverseUrl!, "DATAVERSE_URL");
        using var store = new ProfileStore();
        var profile = (await store.LoadAsync()).GetByName(config.ProfileName!)
            ?? throw new InvalidOperationException($"PPDS_TEST_PROFILE: no auth profile named '{config.ProfileName}' in {store.FilePath}.");
        if (profile.AuthMethod != AuthMethod.InteractiveBrowser)
            throw new InvalidOperationException(
                $"Auth profile '{config.ProfileName}' uses {profile.AuthMethod}; PPDS_TEST_PROFILE accepts only InteractiveBrowser profiles " +
                "(the only kind whose sign-in prompt can be refused). Use the PPDS_TEST_* service-principal settings instead.");
        var bound = profile.Environment is null ? null : Authority(profile.Environment.Url, "the profile's environment URL");
        if (!string.Equals(bound, expected, StringComparison.OrdinalIgnoreCase) && !EnvironmentOverride)
            throw new InvalidOperationException(
                $"Auth profile '{config.ProfileName}' is bound to '{profile.Environment?.Url ?? "(no environment)"}', not DATAVERSE_URL '{expected}'. Refusing to connect " +
                "(set PPDS_TEST_ENVIRONMENT_OVERRIDE=1 to use the profile's identity against DATAVERSE_URL without rebinding it).");

        // Never prompt: the pre-auth callback cancels before any browser or device-code flow starts, for the first
        // token and for every later refresh the client makes.
        var provider = InteractiveBrowserCredentialProvider.FromProfile(profile, deviceCodeCallback: null, beforeInteractiveAuth: _ => PreAuthDialogResult.Cancel);
        try
        {
            ServiceClient client;
            try
            {
                client = await provider.CreateServiceClientAsync(expected);
            }
            catch (OperationCanceledException ex) when (ex is not TaskCanceledException)
            {
                // The pre-auth Cancel above throws a plain OperationCanceledException: silent acquisition failed and a
                // prompt was needed. A TaskCanceledException (e.g. an HTTP timeout) and network or service failures
                // propagate as themselves.
                throw new InvalidOperationException(
                    $"Auth profile '{config.ProfileName}' has no usable cached sign-in; it would need an interactive sign-in. " +
                    "The live tests never prompt: sign in with the PPDS CLI yourself, then re-run.", ex);
            }

            try
            {
                var connected = client.ConnectedOrgUriActual is { } actual ? actual.GetLeftPart(UriPartial.Authority) : null;
                if (!string.Equals(connected, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Connected organisation '{connected ?? "(unknown)"}' does not equal DATAVERSE_URL '{expected}'. Refusing to run.");
                return new LiveTestProfileConnectionSource(new ServiceClientSource(client, name, maxPoolSize), provider);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// PPDS_TEST_ENVIRONMENT_OVERRIDE=1 (exactly "1"): connect with the profile's identity to DATAVERSE_URL even though
    /// the profile is bound to another environment, so a shared profile need not be rebound. Everything else is
    /// unchanged: silent acquisition only (no prompt), InteractiveBrowser profiles only, and the organisation actually
    /// connected to must equal DATAVERSE_URL. Unset or any other value: the profile must be bound to DATAVERSE_URL.
    /// </summary>
    public static bool EnvironmentOverride => Environment.GetEnvironmentVariable("PPDS_TEST_ENVIRONMENT_OVERRIDE")?.Trim() == "1";

    private static string Authority(string url, string what)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority)
            : throw new InvalidOperationException($"{what} '{url}' is not an absolute https URL.");

    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Dispose();
        _provider.Dispose();
    }
}
