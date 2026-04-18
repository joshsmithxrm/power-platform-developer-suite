using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace PPDS.DocsGen.AppToken;

/// <summary>
/// Mints a short-lived GitHub App installation token for the <c>ppds-docs</c>
/// repository. Invoked by <c>.github/workflows/docs-release.yml</c> to obtain a
/// token with write permission on the docs repo without storing a long-lived
/// PAT.
/// </summary>
/// <remarks>
/// <para>Expected environment:</para>
/// <list type="bullet">
///   <item><c>APP_ID</c> — numeric GitHub App id (public value).</item>
///   <item><c>APP_PRIVATE_KEY</c> — PEM-encoded RSA private key (secret).</item>
/// </list>
/// <para>Output channels follow Constitution I1:</para>
/// <list type="bullet">
///   <item>stdout — exactly the installation access token, no trailing
///     whitespace except a final newline. Captured by the workflow via
///     <c>$(...)</c> command substitution and written to
///     <c>$GITHUB_OUTPUT</c>.</item>
///   <item>stderr — every progress line, HTTP status, and error message.</item>
/// </list>
/// <para>Exit codes: <c>0</c> on success, <c>1</c> on any failure (missing
/// env, bad key, HTTP error, installation not found).</para>
/// </remarks>
public static class Program
{
    /// <summary>Target installation account — the token is minted for the
    /// app installation on this GitHub account/org's <c>ppds-docs</c> repo.
    /// </summary>
    private const string TargetInstallationLogin = "ppds-docs";

    /// <summary>
    /// Entry point.
    /// </summary>
    /// <param name="args">Unused; the helper reads configuration from env.</param>
    /// <returns>
    /// <c>0</c> on success with the access token printed to stdout,
    /// <c>1</c> on any error.
    /// </returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var appId = Environment.GetEnvironmentVariable("APP_ID");
            var privateKeyPem = Environment.GetEnvironmentVariable("APP_PRIVATE_KEY");

            if (string.IsNullOrWhiteSpace(appId))
            {
                Console.Error.WriteLine("app-token: APP_ID environment variable is required");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(privateKeyPem))
            {
                Console.Error.WriteLine("app-token: APP_PRIVATE_KEY environment variable is required");
                return 1;
            }

            Console.Error.WriteLine($"app-token: creating JWT for app id {appId}");
            var jwt = CreateJwt(appId, privateKeyPem);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ppds-docs-release/1.0");
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwt);

            Console.Error.WriteLine("app-token: GET /app/installations");
            var installationId = await FindInstallationIdAsync(http, TargetInstallationLogin);
            Console.Error.WriteLine($"app-token: installation id = {installationId}");

            Console.Error.WriteLine($"app-token: POST /app/installations/{installationId}/access_tokens");
            var token = await MintAccessTokenAsync(http, installationId);

            // Constitution I1: stdout is data. Emit only the token.
            Console.Out.Write(token);
            Console.Out.Write('\n');
            Console.Error.WriteLine("app-token: done");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"app-token: error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Builds a 3-minute JWT signed with RS256. GitHub's maximum allowed JWT
    /// lifetime is 10 minutes; 3 minutes is plenty for the two follow-up calls
    /// and forgiving of small clock skew.
    /// </summary>
    /// <param name="appId">The numeric GitHub App id (the <c>iss</c> claim).</param>
    /// <param name="privateKeyPem">PEM-encoded RSA private key.</param>
    /// <returns>Compact JWS string.</returns>
    private static string CreateJwt(string appId, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        // We must detach the key from the disposable RSA so
        // SigningCredentials can keep using it for the lifetime of the JWT
        // handler call. Create a persistent key instance by copying params.
        var parameters = rsa.ExportParameters(includePrivateParameters: true);
        var signingRsa = RSA.Create();
        signingRsa.ImportParameters(parameters);

        var securityKey = new RsaSecurityKey(signingRsa);
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);

        var now = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: appId,
            audience: null,
            subject: null,
            notBefore: now.AddSeconds(-30),
            expires: now.AddMinutes(3),
            issuedAt: now,
            signingCredentials: credentials);

        return handler.WriteToken(token);
    }

    /// <summary>
    /// Calls <c>GET /app/installations</c> and returns the installation id
    /// whose <c>account.login</c> matches <paramref name="login"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no matching installation is found.
    /// </exception>
    private static async Task<long> FindInstallationIdAsync(HttpClient http, string login)
    {
        using var response = await http.GetAsync("https://api.github.com/app/installations");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET /app/installations failed: HTTP {(int)response.StatusCode} — {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "GET /app/installations did not return a JSON array");
        }

        foreach (var inst in doc.RootElement.EnumerateArray())
        {
            if (!inst.TryGetProperty("account", out var account))
            {
                continue;
            }

            if (!account.TryGetProperty("login", out var loginProp)
                || loginProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!string.Equals(loginProp.GetString(), login, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (inst.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out var id))
            {
                return id;
            }
        }

        throw new InvalidOperationException(
            $"No installation found matching account login '{login}'. " +
            "Ensure the GitHub App is installed on the target repo's account.");
    }

    /// <summary>
    /// Calls <c>POST /app/installations/{id}/access_tokens</c> and returns
    /// the resulting 1-hour installation access token.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the API does not return a <c>token</c> string.
    /// </exception>
    private static async Task<string> MintAccessTokenAsync(HttpClient http, long installationId)
    {
        var url = $"https://api.github.com/app/installations/{installationId}/access_tokens";
        using var content = new StringContent(
            "{}",
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"POST {url} failed: HTTP {(int)response.StatusCode} — {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("token", out var tokenProp)
            || tokenProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "access_tokens response missing 'token' field");
        }

        var token = tokenProp.GetString();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("access_tokens response had empty 'token'");
        }

        return token;
    }
}
