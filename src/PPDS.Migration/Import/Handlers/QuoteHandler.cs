using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Handles state transitions for quote records.
    /// Won quotes use WinQuote; closed quotes use CloseQuote.
    /// </summary>
    public class QuoteHandler : IRecordTransformer, IStateTransitionHandler
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("quote", StringComparison.OrdinalIgnoreCase);

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

            return stateCode switch
            {
                0 or 1 => null, // Draft (0) or Active (1) -- no transition needed
                2 => new StateTransitionData // Won
                {
                    EntityName = "quote",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = "WinQuote",
                    MessageData = new Dictionary<string, object>
                    {
                        ["QuoteClose"] = new Entity("quoteclose")
                        {
                            ["quoteid"] = new EntityReference("quote", record.Id),
                            ["subject"] = "Won (migrated)"
                        },
                        ["Status"] = new OptionSetValue(statusCode)
                    }
                },
                3 => new StateTransitionData // Closed
                {
                    EntityName = "quote",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = "CloseQuote",
                    MessageData = new Dictionary<string, object>
                    {
                        ["QuoteClose"] = new Entity("quoteclose")
                        {
                            ["quoteid"] = new EntityReference("quote", record.Id),
                            ["subject"] = "Closed (migrated)"
                        },
                        ["Status"] = new OptionSetValue(statusCode)
                    }
                },
                _ => new StateTransitionData
                {
                    EntityName = "quote",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode
                }
            };
        }
    }
}
