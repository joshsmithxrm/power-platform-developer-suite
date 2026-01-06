using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
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
        /// Cache mapping intersect entity names to actual relationship SchemaNames.
        /// Populated lazily on first M2M processing. Key is lowercase intersect entity name.
        /// </summary>
        private ConcurrentDictionary<string, string>? _relationshipNameCache;

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

            // Flatten all M2M operations into a list for parallel processing
            var allOperations = context.Data.RelationshipData
                .SelectMany(kvp => kvp.Value.Select(m2m => (EntityName: kvp.Key, Data: m2m)))
                .ToList();

            // Calculate total target associations for progress reporting
            var totalTargetAssociations = allOperations.Sum(op => op.Data.TargetIds.Count);

            // Pre-load relationship metadata cache before parallel processing
            // This ensures thread-safe access during parallel operations
            await LoadRelationshipMetadataCacheAsync(cancellationToken).ConfigureAwait(false);

            // Thread-safe counters for progress tracking
            var processedAssociations = 0;
            var successCount = 0;
            var failureCount = 0;
            var skippedDuplicateCount = 0;

            // Get parallelism from connection pool
            var parallelism = _connectionPool.GetTotalRecommendedParallelism();
            _logger?.LogDebug("Processing {Count} M2M operations with parallelism {DOP}",
                allOperations.Count, parallelism);

            // Track first exception for non-ContinueOnError mode
            Exception? firstException = null;
            var shouldStop = false;

            await Parallel.ForEachAsync(
                allOperations,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken
                },
                async (operation, ct) =>
                {
                    // Check if we should stop (non-ContinueOnError mode hit an error)
                    if (shouldStop)
                    {
                        return;
                    }

                    var (entityName, m2mData) = operation;

                    // Get mapped source ID
                    if (!context.IdMappings.TryGetNewId(entityName, m2mData.SourceId, out var sourceNewId))
                    {
                        _logger?.LogDebug("Skipping M2M for unmapped source {Entity}:{Id}",
                            entityName, m2mData.SourceId);
                        return;
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
                        // For role entity, check if same ID exists in target
                        else if (isRoleTarget)
                        {
                            mappedId = await LookupRoleByIdAsync(targetId, ct).ConfigureAwait(false);
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
                        return;
                    }

                    // Resolve relationship name (cache is already loaded and thread-safe)
                    var resolvedRelationshipName = await ResolveRelationshipNameAsync(m2mData.RelationshipName, ct).ConfigureAwait(false);

                    // Get client from pool - each parallel operation gets its own client
                    await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: ct).ConfigureAwait(false);

                    var relatedEntities = new EntityReferenceCollection();
                    foreach (var targetId in mappedTargetIds)
                    {
                        relatedEntities.Add(new EntityReference(m2mData.TargetEntityName, targetId));
                    }

                    var request = new AssociateRequest
                    {
                        Target = new EntityReference(entityName, sourceNewId),
                        RelatedEntities = relatedEntities,
                        Relationship = new Relationship(resolvedRelationshipName)
                    };

                    try
                    {
                        await client.ExecuteAsync(request).ConfigureAwait(false);
                        var newSuccess = Interlocked.Add(ref successCount, mappedTargetIds.Count);
                        var current = Interlocked.Add(ref processedAssociations, mappedTargetIds.Count);

                        // Report progress
                        context.Progress?.Report(new ProgressEventArgs
                        {
                            Phase = MigrationPhase.ProcessingRelationships,
                            Entity = entityName,
                            Relationship = resolvedRelationshipName,
                            Current = current,
                            Total = totalTargetAssociations,
                            SuccessCount = newSuccess,
                            Message = $"[M2M] {resolvedRelationshipName}"
                        });
                    }
                    catch (Exception ex)
                    {
                        // Check if this is a "duplicate key" error - association already exists
                        // This is idempotent success: the desired state (association exists) is achieved
                        if (IsDuplicateAssociationError(ex))
                        {
                            _logger?.LogDebug("Association already exists for {Source} via {Relationship} - treating as success",
                                sourceNewId, m2mData.RelationshipName);

                            // Count as success since the association exists (idempotent)
                            var newSuccess = Interlocked.Add(ref successCount, mappedTargetIds.Count);
                            var current = Interlocked.Add(ref processedAssociations, mappedTargetIds.Count);
                            Interlocked.Increment(ref skippedDuplicateCount);

                            context.Progress?.Report(new ProgressEventArgs
                            {
                                Phase = MigrationPhase.ProcessingRelationships,
                                Entity = entityName,
                                Relationship = resolvedRelationshipName,
                                Current = current,
                                Total = totalTargetAssociations,
                                SuccessCount = newSuccess,
                                Message = $"[M2M] {resolvedRelationshipName}"
                            });
                        }
                        else
                        {
                            // Genuine failure - log and potentially stop
                            _logger?.LogDebug(ex, "Failed to associate {Source} with {TargetCount} targets via {Relationship}",
                                sourceNewId, mappedTargetIds.Count, m2mData.RelationshipName);
                            Interlocked.Increment(ref failureCount);

                            if (!context.Options.ContinueOnError)
                            {
                                shouldStop = true;
                                firstException ??= ex;
                            }
                        }
                    }
                }).ConfigureAwait(false);

            // If we stopped due to an error in non-ContinueOnError mode, rethrow
            if (firstException != null)
            {
                throw firstException;
            }

            stopwatch.Stop();

            if (skippedDuplicateCount > 0)
            {
                _logger?.LogInformation("Processed {Count} M2M associations in {Duration}ms (parallelism: {DOP}, {Skipped} already existed)",
                    successCount, stopwatch.ElapsedMilliseconds, parallelism, skippedDuplicateCount);
            }
            else
            {
                _logger?.LogInformation("Processed {Count} M2M associations in {Duration}ms (parallelism: {DOP})",
                    successCount, stopwatch.ElapsedMilliseconds, parallelism);
            }

            return new PhaseResult
            {
                Success = failureCount == 0,
                RecordsProcessed = successCount + failureCount,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Duration = stopwatch.Elapsed
            };
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

        /// <summary>
        /// Resolves the actual relationship SchemaName from what may be an intersect entity name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Some schema files (e.g., CMT-generated or older PPDS schemas) may store the intersect
        /// entity name (e.g., "teamroles") instead of the relationship SchemaName (e.g., "teamroles_association").
        /// This method resolves the correct name by querying Dataverse metadata.
        /// </para>
        /// <para>
        /// Resolution order:
        /// 1. If already cached, return cached value
        /// 2. If cache doesn't have it, try loading all M2M relationships from metadata
        /// 3. Look up by intersect entity name
        /// 4. If still not found, return original name (let Dataverse fail if invalid)
        /// </para>
        /// </remarks>
        private async Task<string> ResolveRelationshipNameAsync(
            string relationshipName,
            CancellationToken cancellationToken)
        {
            var key = relationshipName.ToLowerInvariant();

            // If cache not loaded, load it now
            if (_relationshipNameCache == null)
            {
                await LoadRelationshipMetadataCacheAsync(cancellationToken).ConfigureAwait(false);
            }

            // Check if this is an intersect entity name that maps to a different SchemaName
            if (_relationshipNameCache!.TryGetValue(key, out var resolvedName))
            {
                if (!resolvedName.Equals(relationshipName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogDebug("Resolved relationship name '{Original}' to '{Resolved}' (intersect entity to SchemaName)",
                        relationshipName, resolvedName);
                }
                return resolvedName;
            }

            // Not found in cache - return original and let Dataverse validate
            return relationshipName;
        }

        /// <summary>
        /// Loads all M2M relationship metadata from Dataverse and builds the intersect entity → SchemaName cache.
        /// </summary>
        private async Task LoadRelationshipMetadataCacheAsync(CancellationToken cancellationToken)
        {
            _relationshipNameCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                var request = new RetrieveAllEntitiesRequest
                {
                    EntityFilters = EntityFilters.Relationships,
                    RetrieveAsIfPublished = false
                };

                var response = (RetrieveAllEntitiesResponse)await client.ExecuteAsync(request).ConfigureAwait(false);

                foreach (var entityMetadata in response.EntityMetadata)
                {
                    if (entityMetadata.ManyToManyRelationships == null)
                        continue;

                    foreach (var rel in entityMetadata.ManyToManyRelationships)
                    {
                        if (string.IsNullOrEmpty(rel.IntersectEntityName) || string.IsNullOrEmpty(rel.SchemaName))
                            continue;

                        // Map both the intersect entity name and the SchemaName to the SchemaName
                        // This handles both cases: when input is the intersect entity or already the SchemaName
                        _relationshipNameCache.TryAdd(rel.IntersectEntityName.ToLowerInvariant(), rel.SchemaName);
                        _relationshipNameCache.TryAdd(rel.SchemaName.ToLowerInvariant(), rel.SchemaName);
                    }
                }

                _logger?.LogDebug("Loaded {Count} M2M relationship mappings from metadata", _relationshipNameCache.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load M2M relationship metadata - relationship name resolution may fail");
                // Don't throw - we'll try with the original names
            }
        }

        /// <summary>
        /// Determines if an exception indicates a duplicate association (association already exists).
        /// </summary>
        /// <remarks>
        /// Dataverse throws "Cannot insert duplicate key" (error code 0x80040237) when attempting
        /// to create an association that already exists. This is treated as idempotent success
        /// since the desired state (association exists) is achieved.
        /// </remarks>
        private static bool IsDuplicateAssociationError(Exception ex)
        {
            // Error code 0x80040237 = -2147220937 (Cannot insert duplicate key)
            const int DuplicateKeyErrorCode = unchecked((int)0x80040237);

            // Check for FaultException with OrganizationServiceFault
            if (ex is System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> faultEx
                && faultEx.Detail?.ErrorCode == DuplicateKeyErrorCode)
            {
                return true;
            }

            // Fallback: check message for common duplicate key patterns
            var message = ex.Message;
            if (message != null &&
                (message.Contains("Cannot insert duplicate key", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("0x80040237", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }
    }
}
