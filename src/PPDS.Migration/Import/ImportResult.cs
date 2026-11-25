using System;
using System.Collections.Generic;
using PPDS.Dataverse.Pooling;
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
        /// Gets or sets the number of records in the source data file.
        /// </summary>
        public int SourceRecordCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records imported.
        /// </summary>
        public int RecordsImported { get; set; }

        /// <summary>
        /// Gets or sets the number of records updated (deferred fields).
        /// </summary>
        public int RecordsUpdated { get; set; }

        /// <summary>
        /// Gets or sets the number of records the entity phase created: every success in Create mode,
        /// none in Update mode, and what the responses reported in Upsert mode. Null when an Upsert
        /// response did not say.
        /// </summary>
        public int? CreatedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of existing records the entity phase updated: every success in
        /// Update mode, none in Create mode, and what the responses reported in Upsert mode. Null when
        /// an Upsert response did not say.
        /// </summary>
        public int? UpdatedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records in the data file that were not written: the records of
        /// tables with <c>importMode="skip"</c>, and records left out by a record filter.
        /// </summary>
        public int SkippedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records the entity phase failed to write.
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// Gets or sets the number of relationships processed.
        /// </summary>
        public int RelationshipsProcessed { get; set; }

        /// <summary>
        /// Gets or sets the number of relationship failures.
        /// </summary>
        public int RelationshipsFailed { get; set; }

        /// <summary>
        /// Gets or sets the import duration.
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets the duration of Phase 1 (entity import).
        /// </summary>
        public TimeSpan Phase1Duration { get; set; }

        /// <summary>
        /// Gets or sets the duration of Phase 2 (deferred fields).
        /// </summary>
        public TimeSpan Phase2Duration { get; set; }

        /// <summary>
        /// Gets or sets the duration of Phase 3 (M2M relationships).
        /// </summary>
        public TimeSpan Phase3Duration { get; set; }

        /// <summary>
        /// Gets or sets the errors that occurred.
        /// </summary>
        public IReadOnlyList<MigrationError> Errors { get; set; } = Array.Empty<MigrationError>();

        /// <summary>
        /// Gets or sets the results per entity.
        /// </summary>
        public IReadOnlyList<EntityImportResult> EntityResults { get; set; } = Array.Empty<EntityImportResult>();

        /// <summary>
        /// Gets or sets the connection pool statistics at the end of the import.
        /// </summary>
        public PoolStatistics? PoolStatistics { get; set; }

        /// <summary>
        /// Gets or sets the warnings that occurred during import.
        /// </summary>
        public IReadOnlyList<ImportWarning> Warnings { get; set; } = Array.Empty<ImportWarning>();

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
        /// Gets or sets the number of records created: every success in Create mode, none in Update
        /// mode or for a skipped table, and what the responses reported in Upsert mode (null when they
        /// did not say).
        /// </summary>
        public int? CreatedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of existing records updated: every success in Update mode, none in
        /// Create mode or for a skipped table, and what the responses reported in Upsert mode (null
        /// when they did not say).
        /// </summary>
        public int? UpdatedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records not written: all of them for a table with
        /// <c>importMode="skip"</c>, otherwise those left out by a record filter.
        /// </summary>
        public int SkippedCount { get; set; }

        /// <summary>
        /// Gets or sets whether the later phases (deferred fields, state transitions, many-to-many,
        /// file data) were not run for this table because the write result did not say which records
        /// were written. Only records proven written receive later-phase writes.
        /// </summary>
        public bool LaterPhasesSkipped { get; set; }

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
