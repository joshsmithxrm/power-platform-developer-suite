using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Handles state transitions for opportunity records.
    /// Won opportunities use WinOpportunity; lost use LoseOpportunity.
    /// </summary>
    public class OpportunityHandler : IRecordTransformer, IStateTransitionHandler
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("opportunity", StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Entity Transform(Entity record, ImportContext context)
        {
            record.Attributes.Remove("statecode");
            record.Attributes.Remove("statuscode");
            return record;
        }

        /// <inheritdoc />
        public StateTransitionData? GetTransition(Entity record, ImportContext context)
        {
            var stateCode = record.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
            var statusCode = record.GetAttributeValue<OptionSetValue>("statuscode")?.Value ?? -1;

            if (stateCode == 0) return null; // Active -- no transition needed

            return stateCode switch
            {
                1 => new StateTransitionData // Won
                {
                    EntityName = "opportunity",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = "WinOpportunity",
                    MessageData = new Dictionary<string, object>
                    {
                        ["OpportunityClose"] = new Entity("opportunityclose")
                        {
                            ["opportunityid"] = new EntityReference("opportunity", record.Id),
                            ["subject"] = "Won (migrated)"
                        },
                        ["Status"] = new OptionSetValue(statusCode)
                    }
                },
                2 => new StateTransitionData // Lost
                {
                    EntityName = "opportunity",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = "LoseOpportunity",
                    MessageData = new Dictionary<string, object>
                    {
                        ["OpportunityClose"] = new Entity("opportunityclose")
                        {
                            ["opportunityid"] = new EntityReference("opportunity", record.Id),
                            ["subject"] = "Lost (migrated)"
                        },
                        ["Status"] = new OptionSetValue(statusCode)
                    }
                },
                _ => new StateTransitionData
                {
                    EntityName = "opportunity",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode
                }
            };
        }
    }
}
