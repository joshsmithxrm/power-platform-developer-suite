using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Transforms records before they are imported.
    /// </summary>
    public interface IRecordTransformer
    {
        /// <summary>
        /// Determines whether this transformer applies to the given entity.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <returns>True if this transformer handles the entity.</returns>
        bool CanHandle(string entityLogicalName);

        /// <summary>
        /// Transforms a record before import.
        /// </summary>
        /// <param name="record">The record to transform.</param>
        /// <param name="context">The import context.</param>
        /// <returns>The transformed record.</returns>
        Entity Transform(Entity record, ImportContext context);
    }
}
