using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Processes many-to-many relationships after entity import.
    /// Creates associations between records using the mapped IDs.
    /// </summary>
    public class RelationshipProcessor : IImportPhaseProcessor
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly ILogger<RelationshipProcessor>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RelationshipProcessor"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="logger">Optional logger.</param>
        public RelationshipProcessor(
            IDataverseConnectionPool connectionPool,
            ILogger<RelationshipProcessor>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _logger = logger;
        }

        /// <inheritdoc />
        public string PhaseName => "Relationships";

        /// <inheritdoc />
        public async Task<PhaseResult> ProcessAsync(
            ImportContext context,
            CancellationToken cancellationToken)
        {
            if (context.Data.RelationshipData.Count == 0)
            {
                _logger?.LogDebug("No relationships to process");
                return PhaseResult.Skipped();
            }

            var stopwatch = Stopwatch.StartNew();
            var totalProcessed = 0;
            var failureCount = 0;

            // Build role name-to-ID cache for role lookup (lazy initialized)
            Dictionary<string, Guid>? roleNameCache = null;

            foreach (var (entityName, m2mDataList) in context.Data.RelationshipData)
            {
                foreach (var m2mData in m2mDataList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    context.Progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.ProcessingRelationships,
                        Entity = entityName,
                        Relationship = m2mData.RelationshipName,
                        Message = $"Processing {m2mData.RelationshipName}..."
                    });

                    // Get mapped source ID
                    if (!context.IdMappings.TryGetNewId(entityName, m2mData.SourceId, out var sourceNewId))
                    {
                        _logger?.LogDebug("Skipping M2M for unmapped source {Entity}:{Id}",
                            entityName, m2mData.SourceId);
                        continue;
                    }

                    // Map target IDs - special handling for role entity
                    var mappedTargetIds = new List<Guid>();
                    var isRoleTarget = m2mData.TargetEntityName.Equals("role", StringComparison.OrdinalIgnoreCase);

                    foreach (var targetId in m2mData.TargetIds)
                    {
                        Guid? mappedId = null;

                        // First try direct ID mapping
                        if (context.IdMappings.TryGetNewId(m2mData.TargetEntityName, targetId, out var directMappedId))
                        {
                            mappedId = directMappedId;
                        }
                        // For role entity, try lookup by name
                        else if (isRoleTarget)
                        {
                            roleNameCache ??= await BuildRoleNameCacheAsync(cancellationToken).ConfigureAwait(false);
                            mappedId = await LookupRoleByIdAsync(targetId, roleNameCache, cancellationToken).ConfigureAwait(false);
                        }

                        if (mappedId.HasValue)
                        {
                            mappedTargetIds.Add(mappedId.Value);
                        }
                        else
                        {
                            _logger?.LogDebug("Could not map target {Entity}:{Id} for relationship {Relationship}",
                                m2mData.TargetEntityName, targetId, m2mData.RelationshipName);
                        }
                    }

                    if (mappedTargetIds.Count == 0)
                    {
                        continue;
                    }

                    // Create association request
                    await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                    var relatedEntities = new EntityReferenceCollection();
                    foreach (var targetId in mappedTargetIds)
                    {
                        relatedEntities.Add(new EntityReference(m2mData.TargetEntityName, targetId));
                    }

                    var request = new AssociateRequest
                    {
                        Target = new EntityReference(entityName, sourceNewId),
                        RelatedEntities = relatedEntities,
                        Relationship = new Relationship(m2mData.RelationshipName)
                    };

                    try
                    {
                        await client.ExecuteAsync(request).ConfigureAwait(false);
                        totalProcessed += mappedTargetIds.Count;
                    }
                    catch (Exception ex)
                    {
                        // M2M associations may fail if already exists - log but continue
                        _logger?.LogDebug(ex, "Failed to associate {Source} with {TargetCount} targets via {Relationship}",
                            sourceNewId, mappedTargetIds.Count, m2mData.RelationshipName);
                        failureCount++;

                        if (!context.Options.ContinueOnError)
                        {
                            throw;
                        }
                    }
                }
            }

            stopwatch.Stop();
            _logger?.LogInformation("Processed {Count} M2M associations in {Duration}ms",
                totalProcessed, stopwatch.ElapsedMilliseconds);

            return new PhaseResult
            {
                Success = failureCount == 0,
                RecordsProcessed = totalProcessed + failureCount,
                SuccessCount = totalProcessed,
                FailureCount = failureCount,
                Duration = stopwatch.Elapsed
            };
        }

        /// <summary>
        /// Builds a cache of role names to IDs from the target environment.
        /// </summary>
        private async Task<Dictionary<string, Guid>> BuildRoleNameCacheAsync(CancellationToken cancellationToken)
        {
            var cache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            try
            {
                await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                var fetchXml = @"<fetch>
                    <entity name='role'>
                        <attribute name='roleid' />
                        <attribute name='name' />
                    </entity>
                </fetch>";

                var response = await client.RetrieveMultipleAsync(
                    new Microsoft.Xrm.Sdk.Query.FetchExpression(fetchXml)).ConfigureAwait(false);

                foreach (var entity in response.Entities)
                {
                    var name = entity.GetAttributeValue<string>("name");
                    var id = entity.Id;
                    if (!string.IsNullOrEmpty(name) && !cache.ContainsKey(name))
                    {
                        cache[name] = id;
                    }
                }

                _logger?.LogDebug("Built role name cache with {Count} entries", cache.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to build role name cache");
            }

            return cache;
        }

        /// <summary>
        /// Attempts to find a matching role in the target environment by ID.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method only succeeds when the source and target environments share the same role IDs
        /// (e.g., same tenant or restored from backup). For cross-environment migrations where role IDs
        /// differ, this returns null because we don't have the source role name to look up by name.
        /// </para>
        /// <para>
        /// Known limitation: Proper role mapping requires exporting role names alongside IDs in the
        /// migration schema. This is tracked for future enhancement.
        /// </para>
        /// </remarks>
        private async Task<Guid?> LookupRoleByIdAsync(
            Guid sourceRoleId,
            Dictionary<string, Guid> roleNameCache,
            CancellationToken cancellationToken)
        {
            // First, we need to get the role name from source environment
            // Since we only have the source ID, we need to query for it
            try
            {
                await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                // Try to retrieve the role by ID - if it exists in target, we can use it directly
                var fetchXml = $@"<fetch top='1'>
                    <entity name='role'>
                        <attribute name='name' />
                        <filter>
                            <condition attribute='roleid' operator='eq' value='{sourceRoleId}' />
                        </filter>
                    </entity>
                </fetch>";

                var response = await client.RetrieveMultipleAsync(
                    new Microsoft.Xrm.Sdk.Query.FetchExpression(fetchXml)).ConfigureAwait(false);

                if (response.Entities.Count > 0)
                {
                    // Role exists with same ID in target
                    return sourceRoleId;
                }
            }
            catch (Exception ex)
            {
                // Role lookup failed - this is expected when source role ID doesn't exist in target
                _logger?.LogDebug(ex, "Role lookup by ID {RoleId} failed (expected for cross-environment migrations)", sourceRoleId);
            }

            // Role doesn't exist with source ID - this is the common case
            // We need to find it by name, but we don't have the source name here
            // For now, return null - proper solution requires exporting role names
            return null;
        }
    }
}
