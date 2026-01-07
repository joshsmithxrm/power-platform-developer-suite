using System.Collections.Generic;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Collects warnings during import operations.
    /// </summary>
    /// <remarks>
    /// Implementations must be thread-safe as warnings can be added from parallel operations.
    /// </remarks>
    public interface IWarningCollector
    {
        /// <summary>
        /// Adds a warning to the collection.
        /// </summary>
        /// <param name="warning">The warning to add.</param>
        void AddWarning(ImportWarning warning);

        /// <summary>
        /// Gets all collected warnings.
        /// </summary>
        /// <returns>A read-only list of warnings.</returns>
        IReadOnlyList<ImportWarning> GetWarnings();

        /// <summary>
        /// Gets the count of collected warnings.
        /// </summary>
        int Count { get; }
    }
}
