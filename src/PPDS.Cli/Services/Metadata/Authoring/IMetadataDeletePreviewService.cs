using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>Optional dependency-inspection capability; existing authoring implementers remain compatible.</summary>
public interface IMetadataDeletePreviewService
{
    /// <summary>Observes deletion dependencies without performing any mutation.</summary>
    Task<MetadataDeletePreviewResult> PreviewDeleteAsync(MetadataDeleteTarget target, CancellationToken ct = default);
}

/// <summary>The schema component family to inspect.</summary>
public enum MetadataDeleteKind { Table, Column, Relationship, Key, GlobalChoice }

/// <summary>Named target for dependency inspection; a parent is required for columns and keys.</summary>
public sealed record MetadataDeleteTarget(MetadataDeleteKind Kind, string SolutionUniqueName, string? EntityLogicalName = null, string? Name = null);

/// <summary>A complete returned dependency edge, including unresolved component identities.</summary>
public sealed class MetadataDeleteDependency
{
    /// <summary>Dependency record identifier, when supplied.</summary>
    public Guid? DependencyId { get; internal set; }
    /// <summary>Numeric dependent component type; null is explicitly incomplete.</summary>
    public int? ComponentType { get; internal set; }
    /// <summary>Dependent object identifier; null is explicitly incomplete.</summary>
    public Guid? ObjectId { get; internal set; }
    /// <summary>Returned dependency kind.</summary>
    public int? DependencyType { get; internal set; }
    /// <summary>Required object identifier, when supplied; may refer to a target subcomponent.</summary>
    public Guid? RequiredObjectId { get; internal set; }
    /// <summary>Required component type, when supplied.</summary>
    public int? RequiredComponentType { get; internal set; }
    /// <summary>Resolved name when available.</summary>
    public string? Name { get; internal set; }
    /// <summary>resolved, unavailable or unsupported.</summary>
    public string NameResolutionStatus { get; internal set; } = "unsupported";
}

/// <summary>Safe classification of optional name enrichment failure; the blocker remains visible.</summary>
public sealed record MetadataDependencyNameIssue(int ComponentType, Guid ObjectId, string ErrorCode, bool RequiresReauthentication);

/// <summary>Observed dependency scope, never a guarantee that a future deletion will succeed.</summary>
public sealed class MetadataDeletePreviewResult
{
    /// <summary>The checks performed by this preview.</summary>
    public string ValidationScope => "delete-dependencies";
    /// <summary>Whether the named target was resolved to metadata.</summary>
    public bool TargetExistenceChecked { get; internal set; }
    /// <summary>Whether all dependency rows were obtained and validated.</summary>
    public bool DependenciesChecked { get; internal set; }
    /// <summary>Other platform constraints and concurrent changes are not simulated.</summary>
    public bool DeletionFeasibilityChecked => false;
    /// <summary>no-blocking-dependencies, blocked or incomplete.</summary>
    public string Status { get; internal set; } = "incomplete";
    /// <summary>Canonical target identifier.</summary>
    public Guid? TargetObjectId { get; internal set; }
    /// <summary>Target solution component type.</summary>
    public int? TargetComponentType { get; internal set; }
    /// <summary>Every returned dependency row, with no orphan/type filtering.</summary>
    public MetadataDeleteDependency[] BlockingComponents { get; internal set; } = [];
    /// <summary>Observed edge count, including duplicates.</summary>
    public int DependencyRowCount => BlockingComponents.Length;
    /// <summary>Distinct known component identities; edge rows remain retained above.</summary>
    public int DistinctComponentCount => BlockingComponents.Where(r => r.ComponentType.HasValue && r.ObjectId.HasValue)
        .Select(r => (r.ComponentType, r.ObjectId)).Distinct().Count();
    /// <summary>Visible limitations of optional name enrichment.</summary>
    public string[] Warnings { get; internal set; } = [];
    /// <summary>Optional lookup error codes/reauthentication flags, without provider diagnostic text.</summary>
    public MetadataDependencyNameIssue[] NameResolutionIssues { get; internal set; } = [];
    /// <summary>Constraints not established by a dependency observation.</summary>
    public string[] UncheckedConstraints => ["Server deletion feasibility, permissions, customizability and concurrent changes"];
    [JsonIgnore] internal Exception? FailureCause { get; set; }
    [JsonIgnore] internal bool IsClear => Status == "no-blocking-dependencies";

    internal PpdsException Refusal() => new(
        Status == "blocked" ? PPDS.Dataverse.Metadata.Authoring.MetadataErrorCodes.DependencyConflict : ErrorCodes.Operation.Internal,
        Status == "blocked" ? $"Deletion is blocked by {DistinctComponentCount} component(s) across {DependencyRowCount} dependency row(s)." : "Deletion dependency inspection is incomplete; no deletion is permitted.",
        new Dictionary<string, object> { ["preview"] = this }, FailureCause);
}
