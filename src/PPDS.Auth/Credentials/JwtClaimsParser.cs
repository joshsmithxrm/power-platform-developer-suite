using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Parses JWT tokens and ClaimsPrincipal to extract claims for display.
/// </summary>
public static class JwtClaimsParser
{
    /// <summary>
    /// Parses claims from a ClaimsPrincipal (from MSAL's ID token) and/or access token.
    /// The ClaimsPrincipal (ID token) typically has user claims like country that aren't in access tokens.
    /// </summary>
    /// <param name="claimsPrincipal">The ClaimsPrincipal from MSAL AuthenticationResult (contains ID token claims).</param>
    /// <param name="accessToken">The JWT access token string (fallback for claims not in ID token).</param>
    /// <param name="debug">If true, writes available claims to console for debugging.</param>
    /// <returns>Parsed claims, or null if no claims could be extracted.</returns>
    public static ParsedJwtClaims? Parse(ClaimsPrincipal? claimsPrincipal, string? accessToken, bool debug = false)
    {
        var result = new ParsedJwtClaims();
        var hasClaims = false;

        // First, try to get claims from ClaimsPrincipal (ID token - has more user claims)
        if (claimsPrincipal?.Claims != null)
        {
            if (debug)
            {
                Console.WriteLine("[DEBUG] ID Token Claims (ClaimsPrincipal):");
                foreach (var claim in claimsPrincipal.Claims)
                {
                    Console.WriteLine($"  {claim.Type}: {claim.Value}");
                }
            }

            result.TenantCountry = GetClaimFromPrincipal(claimsPrincipal, "tenant_ctry");
            result.UserCountry = GetClaimFromPrincipal(claimsPrincipal, "ctry");
            result.Puid = GetClaimFromPrincipal(claimsPrincipal, "puid");

            hasClaims = result.TenantCountry != null || result.UserCountry != null || result.Puid != null;
        }

        // Fall back to access token for any missing claims
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                if (handler.CanReadToken(accessToken))
                {
                    var token = handler.ReadJwtToken(accessToken);

                    if (debug)
                    {
                        Console.WriteLine("[DEBUG] Access Token Claims:");
                        foreach (var claim in token.Claims)
                        {
                            Console.WriteLine($"  {claim.Type}: {claim.Value}");
                        }
                    }

                    // Only fill in missing values
                    result.TenantCountry ??= GetClaimFromToken(token, "tenant_ctry");
                    result.UserCountry ??= GetClaimFromToken(token, "ctry");
                    result.Puid ??= GetClaimFromToken(token, "puid");

                    hasClaims = hasClaims || result.TenantCountry != null || result.UserCountry != null || result.Puid != null;
                }
            }
            catch
            {
                // Token parsing failed - continue with what we have
            }
        }

        return hasClaims ? result : null;
    }

    /// <summary>
    /// Parses a JWT access token and extracts relevant claims.
    /// </summary>
    /// <param name="accessToken">The JWT access token string.</param>
    /// <param name="debug">If true, writes available claims to console for debugging.</param>
    /// <returns>Parsed claims, or null if the token cannot be parsed.</returns>
    public static ParsedJwtClaims? Parse(string? accessToken, bool debug = false)
    {
        return Parse(null, accessToken, debug);
    }

    private static string? GetClaimFromPrincipal(ClaimsPrincipal principal, string claimType)
    {
        return principal.Claims.FirstOrDefault(c =>
            string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string? GetClaimFromToken(JwtSecurityToken token, string claimType)
    {
        foreach (var claim in token.Claims)
        {
            if (string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
            {
                return claim.Value;
            }
        }
        return null;
    }
}

/// <summary>
/// Claims extracted from a JWT access token.
/// </summary>
public sealed class ParsedJwtClaims
{
    /// <summary>
    /// Gets or sets the tenant country code (from 'tenant_ctry' claim).
    /// </summary>
    public string? TenantCountry { get; set; }

    /// <summary>
    /// Gets or sets the user country/region code (from 'ctry' claim).
    /// </summary>
    public string? UserCountry { get; set; }

    /// <summary>
    /// Gets or sets the PUID (from 'puid' claim).
    /// </summary>
    public string? Puid { get; set; }
}
