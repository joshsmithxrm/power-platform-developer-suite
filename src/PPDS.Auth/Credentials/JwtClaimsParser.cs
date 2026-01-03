using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Parses JWT tokens and ClaimsPrincipal to extract claims for profile storage.
/// </summary>
public static class JwtClaimsParser
{
    /// <summary>
    /// Parses claims from a ClaimsPrincipal (from MSAL's ID token) and/or access token.
    /// </summary>
    /// <param name="claimsPrincipal">The ClaimsPrincipal from MSAL AuthenticationResult.</param>
    /// <param name="accessToken">The JWT access token string (fallback).</param>
    /// <returns>Parsed claims, or null if no claims could be extracted.</returns>
    public static ParsedJwtClaims? Parse(ClaimsPrincipal? claimsPrincipal, string? accessToken)
    {
        string? puid = null;

        // Try ClaimsPrincipal first (from ID token)
        if (claimsPrincipal?.Claims != null)
        {
            puid = GetClaimValue(claimsPrincipal, "puid");
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

                    puid ??= GetClaimValue(token, "puid");
                }
            }
            catch
            {
                // Token parsing failed, use what we have
            }
        }

        // Return null if we couldn't extract any claims
        if (puid == null)
        {
            return null;
        }

        return new ParsedJwtClaims
        {
            Puid = puid
        };
    }

    /// <summary>
    /// Parses a JWT access token and extracts relevant claims.
    /// </summary>
    /// <param name="accessToken">The JWT access token string.</param>
    /// <returns>Parsed claims, or null if the token cannot be parsed.</returns>
    public static ParsedJwtClaims? Parse(string? accessToken)
    {
        return Parse(null, accessToken);
    }

    private static string? GetClaimValue(ClaimsPrincipal principal, string claimType)
    {
        return principal.Claims
            .FirstOrDefault(c => string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string? GetClaimValue(JwtSecurityToken token, string claimType)
    {
        return token.Claims
            .FirstOrDefault(c => string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }
}

/// <summary>
/// Claims extracted from authentication tokens.
/// </summary>
public sealed class ParsedJwtClaims
{
    /// <summary>
    /// Gets or sets the PUID (from 'puid' claim).
    /// </summary>
    public string? Puid { get; set; }
}
