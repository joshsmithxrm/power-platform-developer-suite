using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Handles state transitions for incident (case) records.
    /// Both resolved and canceled states use CloseIncident.
    /// </summary>
    public class IncidentHandler : IRecordTransformer, IStateTransitionHandler
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("incident", StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Entity Transform(Entity record, ImportContext context)
        {
            // statecode/statuscode already stripped by TieredImporter before Transform is called
            return record;
        }

        /// <inheritdoc />
        public StateTransitionData? GetTransition(Entity record, ImportContext context)
        {
            var stateCode = record.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
            var statusCode = record.GetAttributeValue<OptionSetValue>("statuscode")?.Value ?? -1;

            if (stateCode == 0) return null; // Active

            // Both Resolved (1) and Canceled (2) use CloseIncidentRequest
            return new StateTransitionData
            {
                EntityName = "incident",
                RecordId = record.Id,
                StateCode = stateCode,
                StatusCode = statusCode,
                SdkMessage = "CloseIncident",
                MessageData = new Dictionary<string, object>
                {
                    ["IncidentResolution"] = new Entity("incidentresolution")
                    {
                        ["incidentid"] = new EntityReference("incident", record.Id),
                        ["subject"] = stateCode == 1 ? "Resolved (migrated)" : "Canceled (migrated)"
                    },
                    ["Status"] = new OptionSetValue(statusCode)
                }
            };
        }
    }
}
