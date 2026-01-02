using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Validates schema compatibility between exported data and target environment.
    /// </summary>
    public interface ISchemaValidator
    {
        /// <summary>
        /// Loads field validity metadata from the target environment for all entities.
        /// </summary>
        /// <param name="entityNames">The entity names to load metadata for.</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Collection of field metadata indexed by entity name.</returns>
        Task<FieldMetadataCollection> LoadTargetFieldMetadataAsync(
            IEnumerable<string> entityNames,
            IProgressReporter? progress,
            CancellationToken cancellationToken);

        /// <summary>
        /// Detects columns in exported data that don't exist in target environment.
        /// </summary>
        /// <param name="data">The migration data containing exported records.</param>
        /// <param name="targetFieldMetadata">The target environment field metadata.</param>
        /// <returns>Result containing missing columns by entity.</returns>
        SchemaMismatchResult DetectMissingColumns(
            MigrationData data,
            FieldMetadataCollection targetFieldMetadata);

        /// <summary>
        /// Determines if a field should be included in the import based on operation mode and metadata.
        /// </summary>
        /// <param name="fieldName">The field name to check.</param>
        /// <param name="mode">The import mode.</param>
        /// <param name="fieldMetadata">Target environment field metadata for the entity.</param>
        /// <param name="reason">Output: reason field was excluded, if any.</param>
        /// <returns>True if field should be included, false otherwise.</returns>
        bool ShouldIncludeField(
            string fieldName,
            ImportMode mode,
            IReadOnlyDictionary<string, FieldValidity>? fieldMetadata,
            out string? reason);
    }
}
