namespace PPDS.Cli.Plugins.Registration;

/// <summary>
/// Read-only package identity observation shared by preview, capability checks and upload.
/// It is revalidated before a write; it is not an atomic reservation in Dataverse.
/// </summary>
public sealed record PluginPackageResolution
{
    /// <summary>The literal root nuspec ID, independent of the registered identity.</summary>
    public required string ArtifactId { get; init; }
    /// <summary>The version declared by the incoming artifact.</summary>
    public required string ArtifactVersion { get; init; }
    /// <summary>SHA-256 of the exact incoming artifact bytes.</summary>
    public required string ArtifactSha256 { get; init; }
    /// <summary>The existing target ID, or null for planned creation or an absent cleanup lookup.</summary>
    public Guid? ExistingPackageId { get; init; }
    /// <summary>The observed/planned display name, or literal artifact lookup ID for absent cleanup.</summary>
    public required string RegisteredName { get; init; }
    /// <summary>The observed or planned registered unique name.</summary>
    public string? RegisteredUniqueName { get; init; }
    /// <summary>SHA-256 of the existing artifact used to verify its nuspec identity.</summary>
    public string? ExistingArtifactSha256 { get; init; }
    /// <summary>The caller's explicit existing-target selector, if any.</summary>
    public string? ExplicitSelector { get; init; }
    /// <summary>The selected solution unique name, if supplied.</summary>
    public string? SolutionName { get; init; }
    /// <summary>The exact selected solution ID.</summary>
    public Guid? SolutionId { get; init; }
    /// <summary>The selected solution's publisher ID.</summary>
    public Guid? PublisherId { get; init; }
    /// <summary>The publisher's verified customization prefix.</summary>
    public string? PublisherPrefix { get; init; }
    /// <summary>How the target was selected; used for diagnostics and revalidation.</summary>
    public string MatchKind { get; init; } = "Create";
    /// <summary>Whether this observation is only for cleanup and cannot authorize upload or creation.</summary>
    public bool IsCleanupLookup { get; init; }
    /// <summary>Whether this observation plans a new package.</summary>
    public bool IsCreate => !IsCleanupLookup && !ExistingPackageId.HasValue;
}
