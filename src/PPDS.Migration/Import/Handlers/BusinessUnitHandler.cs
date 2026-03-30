using System;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Constants;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Transforms business unit records during import.
    /// Remaps the root business unit to the target environment's root BU.
    /// </summary>
    public class BusinessUnitHandler : IRecordTransformer
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals(EntityNames.BusinessUnit, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Entity Transform(Entity record, ImportContext context)
        {
            // Check if this is the root BU (parentbusinessunitid is null)
            if (!record.Contains("parentbusinessunitid") || record["parentbusinessunitid"] == null)
            {
                // This is the root BU — remap its ID to the target's root BU
                if (context.TargetRootBusinessUnitId.HasValue)
                {
                    record.Id = context.TargetRootBusinessUnitId.Value;
                    var primaryKey = $"{record.LogicalName}id";
                    if (record.Contains(primaryKey))
                        record[primaryKey] = context.TargetRootBusinessUnitId.Value;
                }
            }
            return record;
        }
    }
}
