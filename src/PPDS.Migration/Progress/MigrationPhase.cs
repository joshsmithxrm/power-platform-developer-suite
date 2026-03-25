namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Phase of the migration operation.
    /// </summary>
    public enum MigrationPhase
    {
        /// <summary>Analyzing schema and building dependency graph.</summary>
        Analyzing,

        /// <summary>Exporting data from source environment.</summary>
        Exporting,

        /// <summary>Importing data to target environment.</summary>
        Importing,

        /// <summary>Processing deferred lookup fields.</summary>
        ProcessingDeferredFields,

        /// <summary>Processing state/status transitions on imported records.</summary>
        ProcessingStateTransitions,

        /// <summary>Processing many-to-many relationships.</summary>
        ProcessingRelationships,

        /// <summary>Operation completed successfully.</summary>
        Complete,

        /// <summary>Operation encountered an error.</summary>
        Error
    }
}
