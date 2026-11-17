using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Dataverse.Pooling;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Local-only connection for in-process live tests through an existing PPDS auth profile (PPDS_TEST_PROFILE) instead of
/// a service-principal secret. Uses the product's own profile store and credential provider with silent token
/// acquisition only: if the profile would need an interactive sign-in, browser or device code, connecting fails with a
/// clear message and nothing is prompted. Before the source is handed out, the organisation the client actually
/// connected to is checked against DATAVERSE_URL, so a test can never write to another environment.
/// No token, token cache or secret is read, printed or logged here.
/// </summary>
public sealed class ProfileConnectionSource : IConnectionSource
{
    private readonly ServiceClientSource _inner;
    private readonly ICredentialProvider _provider;

    private ProfileConnectionSource(ServiceClientSource inner, ICredentialProvider provider)
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
    public static async Task<ProfileConnectionSource> CreateAsync(LiveTestConfiguration config, string name, int maxPoolSize)
    {
        if (!config.HasProfileCredentials)
            throw new InvalidOperationException("Profile connection requires DATAVERSE_URL and PPDS_TEST_PROFILE.");

        var expected = Authority(config.DataverseUrl!, "DATAVERSE_URL");
        using var store = new ProfileStore();
        var profile = (await store.LoadAsync()).GetByName(config.ProfileName!)
            ?? throw new InvalidOperationException($"PPDS_TEST_PROFILE: no auth profile named '{config.ProfileName}' in {store.FilePath}.");
        if (profile.Environment is null || !string.Equals(Authority(profile.Environment.Url, "the profile's environment URL"), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Auth profile '{profile.Name}' is bound to '{profile.Environment?.Url ?? "(no environment)"}', not DATAVERSE_URL '{expected}'. Refusing to connect.");

        // Never prompt: the pre-auth callback cancels before any browser or device code flow starts, for the first
        // token and for every later refresh the client makes.
        var provider = CredentialProviderFactory.Create(profile, deviceCodeCallback: null, beforeInteractiveAuth: _ => PreAuthDialogResult.Cancel);
        try
        {
            if (profile.AuthMethod is not (AuthMethod.InteractiveBrowser or AuthMethod.DeviceCode))
                throw new InvalidOperationException(
                    $"Auth profile '{profile.Name}' uses {profile.AuthMethod}; PPDS_TEST_PROFILE supports only user profiles signed in through the PPDS CLI (InteractiveBrowser, DeviceCode). Use the PPDS_TEST_* service-principal settings instead.");
            if (await provider.GetCachedTokenInfoAsync(expected) is null)
                throw SignInRequired(config.ProfileName!, null);

            ServiceClient client;
            try
            {
                client = await provider.CreateServiceClientAsync(expected);
            }
            catch (OperationCanceledException ex)
            {
                throw SignInRequired(config.ProfileName!, ex);
            }

            try
            {
                var connected = client.ConnectedOrgUriActual is { } actual ? actual.GetLeftPart(UriPartial.Authority) : null;
                if (!string.Equals(connected, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Connected organisation '{connected ?? "(unknown)"}' does not equal DATAVERSE_URL '{expected}'. Refusing to run.");
                return new ProfileConnectionSource(new ServiceClientSource(client, name, maxPoolSize), provider);
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

    private static InvalidOperationException SignInRequired(string profileName, Exception? inner) => new(
        $"Auth profile '{profileName}' has no usable cached sign-in; it would need an interactive sign-in. " +
        "The live tests never prompt: sign in with the PPDS CLI yourself, then re-run.", inner);

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
