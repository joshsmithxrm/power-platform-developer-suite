using Microsoft.Identity.Client;

namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Provides OAuth tokens using device code flow for CLI interactive authentication.
/// Device code flow displays a URL and code in the console, allowing the user to
/// authenticate in any browser (including on a different device).
/// </summary>
public sealed class DeviceCodeTokenProvider
{
    /// <summary>
    /// Microsoft's well-known public client ID for development/prototyping with Dataverse.
    /// See: https://learn.microsoft.com/en-us/power-apps/developer/data-platform/xrm-tooling/use-connection-strings-xrm-tooling-connect
    /// </summary>
    private const string MicrosoftPublicClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    /// <summary>
    /// The Dataverse scope for user impersonation.
    /// The {url}/.default requests all configured permissions for the app.
    /// </summary>
    private const string DataverseScopeTemplate = "{0}/.default";

    private readonly IPublicClientApplication _msalClient;
    private readonly string _dataverseUrl;
    private AuthenticationResult? _cachedToken;

    /// <summary>
    /// Creates a new device code token provider for the specified Dataverse URL.
    /// </summary>
    /// <param name="dataverseUrl">The Dataverse environment URL.</param>
    public DeviceCodeTokenProvider(string dataverseUrl)
    {
        _dataverseUrl = dataverseUrl.TrimEnd('/');

        _msalClient = PublicClientApplicationBuilder
            .Create(MicrosoftPublicClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "common")
            .WithDefaultRedirectUri()
            .Build();
    }

    /// <summary>
    /// Gets an access token for the Dataverse instance.
    /// Uses cached token if available and not expired, otherwise initiates device code flow.
    /// </summary>
    /// <param name="instanceUri">The Dataverse instance URI (passed by ServiceClient).</param>
    /// <returns>The access token.</returns>
    public async Task<string> GetTokenAsync(string instanceUri)
    {
        var scopes = new[] { string.Format(DataverseScopeTemplate, _dataverseUrl) };

        // Try to get token silently from cache first
        if (_cachedToken != null && _cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _cachedToken.AccessToken;
        }

        // Try silent acquisition (from MSAL cache)
        var accounts = await _msalClient.GetAccountsAsync();
        var account = accounts.FirstOrDefault();

        if (account != null)
        {
            try
            {
                _cachedToken = await _msalClient
                    .AcquireTokenSilent(scopes, account)
                    .ExecuteAsync();
                return _cachedToken.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Silent acquisition failed, need interactive
            }
        }

        // Fall back to device code flow
        _cachedToken = await _msalClient
            .AcquireTokenWithDeviceCode(scopes, deviceCodeResult =>
            {
                // Display the device code message to the user
                Console.WriteLine();
                Console.WriteLine("To sign in, use a web browser to open the page:");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  {deviceCodeResult.VerificationUrl}");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("Enter the code:");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  {deviceCodeResult.UserCode}");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("Waiting for authentication...");
                return Task.CompletedTask;
            })
            .ExecuteAsync();

        Console.WriteLine($"Authenticated as: {_cachedToken.Account.Username}");
        Console.WriteLine();

        return _cachedToken.AccessToken;
    }
}
