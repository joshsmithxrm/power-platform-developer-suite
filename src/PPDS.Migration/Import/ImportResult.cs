using System;
using System.Collections.Generic;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Result of an import operation.
    /// </summary>
    public class ImportResult
    {
        /// <summary>
        /// Gets or sets whether the import was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the number of tiers processed.
        /// </summary>
        public int TiersProcessed { get; set; }

        /// <summary>
        /// Gets or sets the number of records imported.
        /// </summary>
        public int RecordsImported { get; set; }

        /// <summary>
        /// Gets or sets the number of records updated (deferred fields).
        /// </summary>
        public int RecordsUpdated { get; set; }

        /// <summary>
        /// Gets or sets the number of relationships processed.
        /// </summary>
        public int RelationshipsProcessed { get; set; }

        /// <summary>
        /// Gets or sets the import duration.
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets the errors that occurred.
        /// </summary>
        public IReadOnlyList<MigrationError> Errors { get; set; } = Array.Empty<MigrationError>();

        /// <summary>
        /// Gets or sets the results per entity.
        /// </summary>
        public IReadOnlyList<EntityImportResult> EntityResults { get; set; } = Array.Empty<EntityImportResult>();

        /// <summary>
        /// Gets the average records per second.
        /// </summary>
        public double RecordsPerSecond => Duration.TotalSeconds > 0
            ? RecordsImported / Duration.TotalSeconds
            : 0;
    }

    /// <summary>
    /// Result for a single entity import.
    /// </summary>
    public class EntityImportResult
    {
        /// <summary>
        /// Gets or sets the entity logical name.
        /// </summary>
        public string EntityLogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the tier this entity was imported in.
        /// </summary>
        public int TierNumber { get; set; }

        /// <summary>
        /// Gets or sets the number of records imported.
        /// </summary>
        public int RecordCount { get; set; }

        /// <summary>
        /// Gets or sets the number of successful imports.
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Gets or sets the number of failed imports.
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records created (for upsert operations).
        /// </summary>
        public int? CreatedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records updated (for upsert operations).
        /// </summary>
        public int? UpdatedCount { get; set; }

        /// <summary>
        /// Gets or sets the import duration for this entity.
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets whether this entity import was successful.
        /// </summary>
        public bool Success { get; set; } = true;

        /// <summary>
        /// Gets or sets the errors that occurred during import.
        /// </summary>
        public IReadOnlyList<Progress.MigrationError> Errors { get; set; } = Array.Empty<Progress.MigrationError>();
    }
}
