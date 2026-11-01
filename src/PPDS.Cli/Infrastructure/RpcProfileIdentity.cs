using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure;

/// <summary>Opaque, non-secret identity binding for concurrent daemon requests.</summary>
internal static class RpcProfileIdentity
{
    internal static string Create(AuthProfile profile)
    {
        // Never hash credentials or tokens. Name/index are intentionally excluded:
        // reordering and renaming must not select a different authenticated identity.
        var fields = new[]
        {
            profile.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            profile.AuthMethod.ToString(), profile.Cloud.ToString(),
            profile.TenantId, profile.Username, profile.ObjectId, profile.ApplicationId,
            profile.Puid, profile.HomeAccountId, profile.Authority
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))))
            .ToLowerInvariant();
    }

    internal static AuthProfile ResolveSnapshot(ProfileCollection collection, string identity)
    {
        if (identity.Length != 64 || identity.Any(c => !Uri.IsHexDigit(c)))
            throw new RpcException(ErrorCodes.Validation.InvalidArguments, "Invalid profile identity. Resolve the profile again before running.");

        var matches = collection.All.Select(profile => profile.Clone())
            .Where(profile => string.Equals(Create(profile), identity, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length == 0)
            throw new RpcException(ErrorCodes.Auth.ProfileNotFound, "The captured profile was removed or its authentication identity changed. Start a new run.");
        if (matches.Length != 1)
            throw new RpcException(ErrorCodes.Validation.InvalidArguments, "The captured profile identity is ambiguous. Resolve the duplicate profiles before running.");
        return matches[0];
    }
}
