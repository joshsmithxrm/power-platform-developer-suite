using System;
using System.Collections.Generic;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Export
{
    /// <summary>
    /// Result of an export operation.
    /// </summary>
    public class ExportResult
    {
        /// <summary>
        /// Gets or sets whether the export was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the number of entities exported.
        /// </summary>
        public int EntitiesExported { get; set; }

        /// <summary>
        /// Gets or sets the total number of records exported.
        /// </summary>
        public int RecordsExported { get; set; }

        /// <summary>
        /// Gets or sets the export duration.
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets the results per entity.
        /// </summary>
        public IReadOnlyList<EntityExportResult> EntityResults { get; set; } = Array.Empty<EntityExportResult>();

        /// <summary>
        /// Gets or sets the output file path.
        /// </summary>
        public string? OutputPath { get; set; }

        /// <summary>
        /// Gets or sets any errors that occurred.
        /// </summary>
        public IReadOnlyList<MigrationError> Errors { get; set; } = Array.Empty<MigrationError>();

        /// <summary>
        /// Gets or sets non-fatal warnings produced during export (e.g., count-query failures
        /// that forced an entity to sequential export).
        /// </summary>
        public IReadOnlyList<ImportWarning> Warnings { get; set; } = Array.Empty<ImportWarning>();

        /// <summary>
        /// Gets the average records per second.
        /// </summary>
        public double RecordsPerSecond => Duration.TotalSeconds > 0
            ? RecordsExported / Duration.TotalSeconds
            : 0;
    }

    /// <summary>
    /// Result for a single entity export.
    /// </summary>
    public class EntityExportResult
    {
        /// <summary>
        /// Gets or sets the entity logical name.
        /// </summary>
        public string EntityLogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of records exported.
        /// </summary>
        public int RecordCount { get; set; }

        /// <summary>
        /// Gets or sets the export duration for this entity.
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets whether this entity export was successful.
        /// </summary>
        public bool Success { get; set; } = true;

        /// <summary>
        /// Gets or sets the error message if export failed.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
