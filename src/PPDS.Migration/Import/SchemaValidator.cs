using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Validates schema compatibility between exported data and target environment.
    /// </summary>
    public class SchemaValidator : ISchemaValidator
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly ILogger<SchemaValidator>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SchemaValidator"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="logger">Optional logger.</param>
        public SchemaValidator(
            IDataverseConnectionPool connectionPool,
            ILogger<SchemaValidator>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<FieldMetadataCollection> LoadTargetFieldMetadataAsync(
            IEnumerable<string> entityNames,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, Dictionary<string, FieldValidity>>(StringComparer.OrdinalIgnoreCase);

            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Loading target environment field metadata..."
            });

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var entityName in entityNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var request = new RetrieveEntityRequest
                    {
                        LogicalName = entityName,
                        EntityFilters = EntityFilters.Attributes
                    };

                    var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

                    var attrValidity = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase);
                    if (response.EntityMetadata.Attributes != null)
                    {
                        foreach (var attr in response.EntityMetadata.Attributes)
                        {
                            attrValidity[attr.LogicalName] = new FieldValidity(
                                attr.IsValidForCreate ?? false,
                                attr.IsValidForUpdate ?? false
                            );
                        }
                    }

                    result[entityName] = attrValidity;
                    _logger?.LogDebug("Loaded metadata for {Entity}: {Count} attributes", entityName, attrValidity.Count);
                }
                catch (FaultException ex)
                {
                    _logger?.LogWarning(ex, "Failed to load metadata for entity {Entity}, using schema defaults", entityName);
                    // Entity might not exist in target - use empty metadata (will use schema defaults)
                    result[entityName] = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase);
                }
            }

            _logger?.LogInformation("Loaded field metadata for {Count} entities", result.Count);
            return new FieldMetadataCollection(result);
        }

        /// <inheritdoc />
        public SchemaMismatchResult DetectMissingColumns(
            MigrationData data,
            FieldMetadataCollection targetFieldMetadata)
        {
            var missingColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (entityName, records) in data.EntityData)
            {
                if (records.Count == 0)
                    continue;

                // Get all unique field names from exported records
                var exportedFields = records
                    .SelectMany(r => r.Attributes.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Get target metadata for this entity
                var targetFields = targetFieldMetadata.GetFieldsForEntity(entityName);

                // Find fields that exist in export but not in target
                var missing = exportedFields
                    .Where(f => !targetFields.ContainsKey(f))
                    .Where(f => !f.EndsWith("id", StringComparison.OrdinalIgnoreCase) ||
                                !f.Equals($"{entityName}id", StringComparison.OrdinalIgnoreCase)) // Skip primary key
                    .OrderBy(f => f)
                    .ToList();

                if (missing.Count > 0)
                {
                    missingColumns[entityName] = missing;
                }
            }

            return new SchemaMismatchResult(missingColumns);
        }

        /// <inheritdoc />
        public bool ShouldIncludeField(
            string fieldName,
            ImportMode mode,
            IReadOnlyDictionary<string, FieldValidity>? fieldMetadata,
            out string? reason)
        {
            reason = null;

            // If no metadata available for this entity, skip unknown fields to prevent Dataverse errors
            if (fieldMetadata == null || !fieldMetadata.TryGetValue(fieldName, out var validity))
            {
                reason = "not found in target";
                return false;
            }

            // Never include fields that are not valid for any write operation
            if (!validity.IsValidForCreate && !validity.IsValidForUpdate)
            {
                reason = "not valid for create or update";
                return false;
            }

            // For Update mode, skip fields not valid for update
            if (mode == ImportMode.Update && !validity.IsValidForUpdate)
            {
                reason = "not valid for update";
                return false;
            }

            // For Create mode, skip fields not valid for create
            if (mode == ImportMode.Create && !validity.IsValidForCreate)
            {
                reason = "not valid for create";
                return false;
            }

            // For Upsert mode, include fields valid for either operation
            // (the actual operation will determine validity per-record)
            return true;
        }
    }
}
