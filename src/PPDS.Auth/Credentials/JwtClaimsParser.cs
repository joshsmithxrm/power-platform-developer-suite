using System;
using System.IdentityModel.Tokens.Jwt;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Parses JWT access tokens to extract claims for display.
/// </summary>
public static class JwtClaimsParser
{
    /// <summary>
    /// Parses a JWT access token and extracts relevant claims.
    /// </summary>
    /// <param name="accessToken">The JWT access token string.</param>
    /// <returns>Parsed claims, or null if the token cannot be parsed.</returns>
    public static ParsedJwtClaims? Parse(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(accessToken))
                return null;

            var token = handler.ReadJwtToken(accessToken);

            return new ParsedJwtClaims
            {
                TenantCountry = GetClaim(token, "tenant_ctry"),
                UserCountry = GetClaim(token, "ctry"),
                Puid = GetClaim(token, "puid")
            };
        }
        catch
        {
            // Token parsing failed - return null
            return null;
        }
    }

    private static string? GetClaim(JwtSecurityToken token, string claimType)
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
