using System;
using System.Collections.Generic;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Describes a state/status transition to apply to a record after import.
    /// </summary>
    public class StateTransitionData
    {
        /// <summary>
        /// Gets the entity logical name.
        /// </summary>
        public string EntityName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the record ID.
        /// </summary>
        public Guid RecordId { get; init; }

        /// <summary>
        /// Gets the target state code.
        /// </summary>
        public int StateCode { get; init; }

        /// <summary>
        /// Gets the target status code.
        /// </summary>
        public int StatusCode { get; init; }

        /// <summary>
        /// Gets the SDK message to use for the transition, or null to use SetStateRequest.
        /// </summary>
        public string? SdkMessage { get; init; }

        /// <summary>
        /// Gets extra parameters for the SDK message.
        /// </summary>
        public Dictionary<string, object>? MessageData { get; init; }
    }
}
