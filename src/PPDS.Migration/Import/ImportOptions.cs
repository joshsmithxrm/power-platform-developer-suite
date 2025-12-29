using PPDS.Dataverse.BulkOperations;
using PPDS.Migration.Models;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Options for import operations.
    /// </summary>
    public class ImportOptions
    {
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
