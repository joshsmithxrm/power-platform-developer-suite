using System.Security.Cryptography;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Tests.Services.Shared;

/// <summary>Explicit resolved identities for orchestration mocks; no Dataverse calls.</summary>
internal static class PluginPackageResolutionTestFactory
{
    internal static PluginPackageResolution Create(string artifactId, Guid? existingId = null,
        string? registeredName = null, string? uniqueName = null, byte[]? bytes = null) => new()
    {
        ArtifactId = artifactId,
        ArtifactVersion = "1.0.0",
        ArtifactSha256 = Convert.ToHexString(SHA256.HashData(bytes ?? [])),
        ExistingPackageId = existingId,
        RegisteredName = registeredName ?? artifactId,
        RegisteredUniqueName = uniqueName,
        MatchKind = existingId.HasValue ? "Legacy" : "Create"
    };
}
