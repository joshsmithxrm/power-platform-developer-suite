using PPDS.Migration.Models;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Options for import operations.
    /// </summary>
    public class ImportOptions
    {
        /// <summary>
        /// Gets or sets the batch size for bulk operations.
        /// Default: 1000
        /// </summary>
        public int BatchSize { get; set; } = 1000;

        /// <summary>
        /// Gets or sets whether to use modern bulk APIs (CreateMultiple, etc.).
        /// Default: true
        /// </summary>
        public bool UseBulkApis { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to bypass custom plugin execution.
        /// Default: false
        /// </summary>
        public bool BypassCustomPluginExecution { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to bypass Power Automate flows.
        /// Default: false
        /// </summary>
        public bool BypassPowerAutomateFlows { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to continue on individual record failures.
        /// Default: true
        /// </summary>
        public bool ContinueOnError { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum parallel entities within a tier.
        /// Default: 4
        /// </summary>
        public int MaxParallelEntities { get; set; } = 4;

        /// <summary>
        /// Gets or sets the import mode.
        /// Default: Upsert
        /// </summary>
        public ImportMode Mode { get; set; } = ImportMode.Upsert;

        /// <summary>
        /// Gets or sets whether to suppress duplicate detection.
        /// Default: false
        /// </summary>
        public bool SuppressDuplicateDetection { get; set; } = false;

        /// <summary>
        /// Gets or sets the progress reporting interval in records.
        /// Default: 100
        /// </summary>
        public int ProgressInterval { get; set; } = 100;

        /// <summary>
        /// Gets or sets the user mappings for remapping user references.
        /// If null, user references are not remapped.
        /// </summary>
        public UserMappingCollection? UserMappings { get; set; }

        /// <summary>
        /// Gets or sets whether to disable plugins on entities marked with disableplugins=true in schema.
        /// Default: true (respects schema setting)
        /// </summary>
        public bool RespectDisablePluginsSetting { get; set; } = true;
    }

    /// <summary>
    /// Import mode for handling records.
    /// </summary>
    public enum ImportMode
    {
        /// <summary>Create new records only. Fails if record exists.</summary>
        Create,

        /// <summary>Update existing records only. Fails if record doesn't exist.</summary>
        Update,

        /// <summary>Create or update records as needed.</summary>
        Upsert
    }
}
