using System;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Filters activitypointer base type records — always skipped because
    /// concrete activity types (email, task, etc.) are imported directly.
    /// </summary>
    public class ActivityPointerHandler : IRecordFilter
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("activitypointer", StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public bool ShouldSkip(Entity record, ImportContext context)
            => true; // Always skip activitypointer base type
    }
}
