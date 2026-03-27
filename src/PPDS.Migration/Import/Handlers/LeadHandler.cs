using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Handles state transitions for lead records.
    /// Qualified leads use QualifyLead with suppressed side-effects;
    /// disqualified leads use SetStateRequest (null SdkMessage).
    /// </summary>
    public class LeadHandler : IRecordTransformer, IStateTransitionHandler
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("lead", StringComparison.OrdinalIgnoreCase);

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

            return stateCode switch
            {
                1 => new StateTransitionData // Qualified
                {
                    EntityName = "lead",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = "QualifyLead",
                    MessageData = new Dictionary<string, object>
                    {
                        ["LeadId"] = new EntityReference("lead", record.Id),
                        ["CreateAccount"] = false,
                        ["CreateContact"] = false,
                        ["CreateOpportunity"] = false,
                        ["Status"] = new OptionSetValue(statusCode)
                    }
                },
                2 => new StateTransitionData // Disqualified — use SetStateRequest, NOT QualifyLead
                {
                    EntityName = "lead",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = null // null means SetStateRequest
                },
                _ => null // Open (0) — no transition needed
            };
        }
    }
}
