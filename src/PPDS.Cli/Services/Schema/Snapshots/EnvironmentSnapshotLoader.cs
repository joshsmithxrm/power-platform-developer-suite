using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Cli.Services.Schema.Snapshots;

/// <summary>
/// Builds a <see cref="SchemaSnapshot"/> from a live Dataverse environment via
/// <see cref="IMetadataQueryService"/>.
/// </summary>
public sealed class EnvironmentSnapshotLoader : ISnapshotLoader
{
    private readonly IMetadataQueryService _metadata;
    private readonly string _sourceDescriptor;
    private readonly Action<string>? _progress;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="metadata">Metadata query service for the target environment.</param>
    /// <param name="sourceDescriptor">Descriptor used in the report (e.g. <c>env:https://qa.crm.dynamics.com</c>).</param>
    /// <param name="progress">Optional callback for per-entity progress messages.</param>
    public EnvironmentSnapshotLoader(
        IMetadataQueryService metadata,
        string sourceDescriptor,
        Action<string>? progress = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _sourceDescriptor = sourceDescriptor ?? throw new ArgumentNullException(nameof(sourceDescriptor));
        _progress = progress;
    }

    /// <summary>
    /// Logical names of entities that exist in the environment but are not loaded in
    /// detail (i.e. filtered out by <c>entityFilter</c>). Populated after
    /// <see cref="LoadAsync"/> returns. Empty when no filter was applied.
    /// </summary>
    public IReadOnlyList<string> UnloadedEntities { get; private set; } = Array.Empty<string>();

    /// <inheritdoc />
    public async Task<SchemaSnapshot> LoadAsync(
        IReadOnlyCollection<string>? entityFilter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var allEntities = await _metadata.GetEntitiesAsync(
                customOnly: false,
                filter: null,
                includeIntersect: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            List<EntitySummary> selected;
            if (entityFilter is { Count: > 0 })
            {
                var filterSet = new HashSet<string>(entityFilter, StringComparer.OrdinalIgnoreCase);
                selected = allEntities.Where(e => filterSet.Contains(e.LogicalName)).ToList();
                UnloadedEntities = allEntities.Where(e => !filterSet.Contains(e.LogicalName))
                    .Select(e => e.LogicalName).ToList();
            }
            else
            {
                selected = allEntities.ToList();
                UnloadedEntities = Array.Empty<string>();
            }

            var snapshots = new List<EntitySnapshot>(selected.Count);
            var index = 0;
            foreach (var summary in selected)
            {
                index++;
                _progress?.Invoke($"  Loading entity {index}/{selected.Count}: {summary.LogicalName}");
                cancellationToken.ThrowIfCancellationRequested();
                var entity = await _metadata.GetEntityAsync(
                    summary.LogicalName,
                    includeAttributes: true,
                    includeRelationships: true,
                    includeKeys: false,
                    includePrivileges: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                snapshots.Add(new EntitySnapshot
                {
                    LogicalName = entity.LogicalName,
                    DisplayName = entity.DisplayName,
                    Attributes = entity.Attributes.Select(a => new AttributeSnapshot
                    {
                        LogicalName = a.LogicalName,
                        AttributeType = NormalizeType(a.AttributeType),
                        RequiredLevel = a.RequiredLevel,
                        MaxLength = a.MaxLength,
                        Precision = a.Precision,
                        LookupTargets = a.Targets?.Select(t => t.ToLowerInvariant()).ToList(),
                        OptionValues = a.Options?.Select(o => o.Value).ToList()
                    }).ToList(),
                    Relationships = entity.OneToManyRelationships
                        .Select(r => new RelationshipSnapshot
                        {
                            SchemaName = r.SchemaName,
                            RelationshipType = "OneToMany",
                            ReferencingEntity = r.ReferencingEntity,
                            ReferencedEntity = r.ReferencedEntity
                        })
                        .Concat(entity.ManyToOneRelationships.Select(r => new RelationshipSnapshot
                        {
                            SchemaName = r.SchemaName,
                            RelationshipType = "ManyToOne",
                            ReferencingEntity = r.ReferencingEntity,
                            ReferencedEntity = r.ReferencedEntity
                        }))
                        .Concat(entity.ManyToManyRelationships.Select(r => new RelationshipSnapshot
                        {
                            SchemaName = r.SchemaName,
                            RelationshipType = "ManyToMany"
                        }))
                        .ToList()
                });
            }

            return new SchemaSnapshot
            {
                Source = _sourceDescriptor,
                Entities = snapshots,
                IncludesOptionSetValues = true
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException)
        {
            throw new PpdsException(
                ErrorCodes.Operation.Internal,
                $"Failed to load schema snapshot from environment: {ex.Message}",
                ex);
        }
    }

    private static string NormalizeType(string? type) =>
        string.IsNullOrEmpty(type) ? "unknown" : type.ToLowerInvariant();
}
