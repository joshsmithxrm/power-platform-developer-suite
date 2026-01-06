using System;
using PPDS.Dataverse.BulkOperations;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Options for import operations.
    /// </summary>
    public class ImportOptions
    {
        private int _maxParallelEntities = 4;
        /// <summary>
        /// Gets or sets whether to use modern bulk APIs (CreateMultiple, etc.).
        /// Default: true
        /// </summary>
        public bool UseBulkApis { get; set; } = true;

        /// <summary>
        /// Gets or sets which custom business logic to bypass during import.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Requires the <c>prvBypassCustomBusinessLogic</c> privilege.
        /// By default, only users with the System Administrator security role have this privilege.
        /// </para>
        /// <para>
        /// This bypasses custom plugins and workflows only. Microsoft's core system plugins
        /// and workflows included in Microsoft-published solutions are NOT bypassed.
        /// </para>
        /// <para>
        /// Does not affect Power Automate flows. Use <see cref="BypassPowerAutomateFlows"/> for that.
        /// </para>
        /// </remarks>
        public CustomLogicBypass BypassCustomPlugins { get; set; } = CustomLogicBypass.None;

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
        /// Must be at least 1.
        /// Default: 4
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when value is less than 1.</exception>
        public int MaxParallelEntities
        {
            get => _maxParallelEntities;
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "MaxParallelEntities must be at least 1.");
                _maxParallelEntities = value;
            }
        }

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
        /// Gets or sets the user mappings for remapping user references.
        /// If null, user references are not remapped.
        /// </summary>
        public UserMappingCollection? UserMappings { get; set; }

        /// <summary>
        /// Gets or sets whether to disable plugins on entities marked with disableplugins=true in schema.
        /// Default: true (respects schema setting)
        /// </summary>
        public bool RespectDisablePluginsSetting { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to strip owner-related fields during import.
        /// When true, removes ownerid, createdby, modifiedby, and related fields,
        /// allowing Dataverse to assign the current user as owner.
        /// Use this when importing data to a different environment where source
        /// users don't exist.
        /// Default: false
        /// </summary>
        public bool StripOwnerFields { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to skip columns that exist in exported data but not in the target environment.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When false (default), import fails with a detailed report of missing columns.
        /// This prevents silent data loss and helps identify schema drift between environments.
        /// </para>
        /// <para>
        /// When true, missing columns are logged as warnings and skipped during import.
        /// Use this when intentionally importing to an environment with different schema.
        /// </para>
        /// </remarks>
        public bool SkipMissingColumns { get; set; } = false;

        /// <summary>
        /// Gets or sets the current user's ID for fallback when user mappings can't resolve a reference.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When <see cref="UserMappings"/> has <see cref="UserMappingCollection.UseCurrentUserAsDefault"/> set to true,
        /// and a user reference cannot be resolved through explicit mappings, this ID is used as the fallback.
        /// </para>
        /// <para>
        /// This should be set to the WhoAmI user ID of the importing service principal or user.
        /// If not set and UseCurrentUserAsDefault is true, unmapped user references will be left unchanged.
        /// </para>
        /// </remarks>
        public Guid? CurrentUserId { get; set; }

        /// <summary>
        /// Gets or sets the callback for streaming errors as they occur.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When set, this callback is invoked immediately when an error occurs during import.
        /// This allows real-time error streaming (e.g., to a .errors.jsonl file) so errors
        /// are not lost if the import is cancelled.
        /// </para>
        /// <para>
        /// The callback is invoked on multiple threads concurrently, so implementations
        /// must be thread-safe.
        /// </para>
        /// </remarks>
        public Action<MigrationError>? ErrorCallback { get; set; }
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
