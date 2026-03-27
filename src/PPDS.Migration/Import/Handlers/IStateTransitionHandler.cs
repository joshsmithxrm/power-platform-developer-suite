using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Determines state transitions to apply to records after import.
    /// </summary>
    public interface IStateTransitionHandler
    {
        /// <summary>
        /// Determines whether this handler applies to the given entity.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <returns>True if this handler handles the entity.</returns>
        bool CanHandle(string entityLogicalName);

        /// <summary>
        /// Gets the state transition data for a record, or null if no transition is needed.
        /// </summary>
        /// <param name="record">The record to evaluate.</param>
        /// <param name="context">The import context.</param>
        /// <returns>The state transition data, or null.</returns>
        StateTransitionData? GetTransition(Entity record, ImportContext context);
    }
}
