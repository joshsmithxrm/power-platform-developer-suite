using System;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Constants;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Filters system, integration, and support users during import.
    /// </summary>
    public class SystemUserHandler : IRecordFilter
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals(EntityNames.SystemUser, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public bool ShouldSkip(Entity record, ImportContext context)
        {
            // Skip SYSTEM user (fullname == "SYSTEM") and INTEGRATION user (fullname == "INTEGRATION")
            var fullname = record.GetAttributeValue<string>("fullname");
            if (string.Equals(fullname, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullname, "INTEGRATION", StringComparison.OrdinalIgnoreCase))
                return true;

            // Skip support users (accessmode = 3) and integration users (accessmode = 5)
            var accessMode = record.GetAttributeValue<OptionSetValue>("accessmode")?.Value;
            return accessMode == 3 || accessMode == 5;
        }
    }
}
