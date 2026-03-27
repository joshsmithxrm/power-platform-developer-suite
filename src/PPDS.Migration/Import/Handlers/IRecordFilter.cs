using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Filters records during import, determining which should be skipped.
    /// </summary>
    public interface IRecordFilter
    {
        /// <summary>
        /// Determines whether this filter applies to the given entity.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <returns>True if this filter handles the entity.</returns>
        bool CanHandle(string entityLogicalName);

        /// <summary>
        /// Determines whether the given record should be skipped during import.
        /// </summary>
        /// <param name="record">The record to evaluate.</param>
        /// <param name="context">The import context.</param>
        /// <returns>True if the record should be skipped.</returns>
        bool ShouldSkip(Entity record, ImportContext context);
    }
}
