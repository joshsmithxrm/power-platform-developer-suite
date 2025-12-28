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
            puid = claimsPrincipal.Claims
                .FirstOrDefault(c => string.Equals(c.Type, "puid", StringComparison.OrdinalIgnoreCase))?.Value;
        }

        // Fall back to access token
        if (puid == null && !string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                if (handler.CanReadToken(accessToken))
                {
                    var token = handler.ReadJwtToken(accessToken);
                    puid = token.Claims
                        .FirstOrDefault(c => string.Equals(c.Type, "puid", StringComparison.OrdinalIgnoreCase))?.Value;
                }
            }
            catch
            {
                // Token parsing failed
            }
        }

        return puid != null ? new ParsedJwtClaims { Puid = puid } : null;
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
