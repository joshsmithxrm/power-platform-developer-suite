using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Cli.Services.Schema.Snapshots;

/// <summary>
/// Loads a <see cref="SchemaSnapshot"/> from some backing source (live env, data package, ...).
/// </summary>
public interface ISnapshotLoader
{
    /// <summary>
    /// Load the snapshot. <paramref name="entityFilter"/>, when non-null, restricts
    /// the snapshot to the named entities (live-env loaders only — package loaders
    /// always return all entities defined in the file).
    /// </summary>
    Task<SchemaSnapshot> LoadAsync(System.Collections.Generic.IReadOnlyCollection<string>? entityFilter = null, CancellationToken cancellationToken = default);
}
