using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Handles state transitions for sales order records.
    /// Fulfilled orders use FulfillSalesOrder; canceled orders use CancelSalesOrder.
    /// </summary>
    public class SalesOrderHandler : IRecordTransformer, IStateTransitionHandler
    {
        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("salesorder", StringComparison.OrdinalIgnoreCase);

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
                3 => new StateTransitionData // Fulfilled
                {
                    EntityName = "salesorder",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = "FulfillSalesOrder",
                    MessageData = new Dictionary<string, object>
                    {
                        ["OrderClose"] = new Entity("orderclose")
                        {
                            ["salesorderid"] = new EntityReference("salesorder", record.Id),
                            ["subject"] = "Fulfilled (migrated)"
                        },
                        ["Status"] = new OptionSetValue(statusCode)
                    }
                },
                2 => new StateTransitionData // Canceled
                {
                    EntityName = "salesorder",
                    RecordId = record.Id,
                    StateCode = stateCode,
                    StatusCode = statusCode,
                    SdkMessage = "CancelSalesOrder",
                    MessageData = new Dictionary<string, object>
                    {
                        ["OrderClose"] = new Entity("orderclose")
                        {
                            ["salesorderid"] = new EntityReference("salesorder", record.Id),
                            ["subject"] = "Canceled (migrated)"
                        },
                        ["Status"] = new OptionSetValue(statusCode)
                    }
                },
                _ => null // Active (0) or Submitted (1) — no transition needed
            };
        }
    }
}
