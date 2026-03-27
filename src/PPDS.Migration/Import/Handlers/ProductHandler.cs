using System;
using System.Collections.Concurrent;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Handles product records during import.
    /// Cascades skip to child products when a parent product fails,
    /// and handles state transitions for non-draft products.
    /// </summary>
    public class ProductHandler : IRecordFilter, IStateTransitionHandler
    {
        private readonly ConcurrentDictionary<Guid, byte> _failedProductIds = new();

        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("product", StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public bool ShouldSkip(Entity record, ImportContext context)
        {
            // Check if parent product failed
            var parentRef = record.GetAttributeValue<EntityReference>("parentproductid");
            if (parentRef != null && _failedProductIds.ContainsKey(parentRef.Id))
            {
                // Parent failed — cascade skip to this child
                _failedProductIds.TryAdd(record.Id, 0);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Tracks a product record that failed during import.
        /// This enables cascade-skip for child products that reference this parent.
        /// </summary>
        /// <param name="productId">The ID of the failed product.</param>
        public void TrackFailure(Guid productId)
        {
            _failedProductIds.TryAdd(productId, 0);
        }

        /// <inheritdoc />
        public StateTransitionData? GetTransition(Entity record, ImportContext context)
        {
            var stateCode = record.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
            var statusCode = record.GetAttributeValue<OptionSetValue>("statuscode")?.Value ?? -1;

            if (stateCode == 0) return null; // Draft — no transition

            return new StateTransitionData
            {
                EntityName = "product",
                RecordId = record.Id,
                StateCode = stateCode,
                StatusCode = statusCode
                // null SdkMessage = SetStateRequest
            };
        }
    }
}
