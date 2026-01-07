using System;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Shared context passed to all import phases.
    /// Contains the data, plan, options, and shared state needed for import operations.
    /// </summary>
    public class ImportContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImportContext"/> class.
        /// </summary>
        /// <param name="data">The migration data containing records to import.</param>
        /// <param name="plan">The execution plan defining tier order and deferred fields.</param>
        /// <param name="options">The import options.</param>
        /// <param name="idMappings">The shared ID mapping collection.</param>
        /// <param name="targetFieldMetadata">The target environment field metadata.</param>
        /// <param name="progress">Optional progress reporter.</param>
        public ImportContext(
            MigrationData data,
            ExecutionPlan plan,
            ImportOptions options,
            IdMappingCollection idMappings,
            FieldMetadataCollection targetFieldMetadata,
            IProgressReporter? progress = null)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            IdMappings = idMappings ?? throw new ArgumentNullException(nameof(idMappings));
            TargetFieldMetadata = targetFieldMetadata ?? throw new ArgumentNullException(nameof(targetFieldMetadata));
            Progress = progress;
        }

        /// <summary>
        /// Gets the migration data containing records to import.
        /// </summary>
        public MigrationData Data { get; }

        /// <summary>
        /// Gets the execution plan defining tier order and deferred fields.
        /// </summary>
        public ExecutionPlan Plan { get; }

        /// <summary>
        /// Gets the import options.
        /// </summary>
        public ImportOptions Options { get; }

        /// <summary>
        /// Gets the shared ID mapping collection.
        /// This is populated during entity import and read during deferred field and relationship processing.
        /// Thread-safe for concurrent access.
        /// </summary>
        public IdMappingCollection IdMappings { get; }

        /// <summary>
        /// Gets the target environment field metadata.
        /// Used to validate which fields are valid for create/update operations.
        /// </summary>
        public FieldMetadataCollection TargetFieldMetadata { get; }

        /// <summary>
        /// Gets the optional progress reporter.
        /// </summary>
        public IProgressReporter? Progress { get; }

        /// <summary>
        /// Gets or sets the optional output manager for checkpoint logging.
        /// When set, tier starts, entity completions, and phase transitions are logged to the progress file.
        /// </summary>
        public ImportOutputManager? OutputManager { get; set; }
    }
}
