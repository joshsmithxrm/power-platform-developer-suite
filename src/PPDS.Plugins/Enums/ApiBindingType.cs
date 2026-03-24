namespace PPDS.Plugins
{
    /// <summary>
    /// Specifies the binding type for a Custom API.
    /// Determines whether the API is global or bound to a specific entity or entity collection.
    /// </summary>
    public enum ApiBindingType
    {
        /// <summary>
        /// Global (0). The API is not bound to any specific entity.
        /// Use for operations that are not entity-specific.
        /// </summary>
        Global = 0,

        /// <summary>
        /// Entity (1). The API is bound to a single entity record.
        /// The API operates on a specific entity instance identified by its ID.
        /// </summary>
        Entity = 1,

        /// <summary>
        /// Entity collection (2). The API is bound to a collection of entity records.
        /// The API operates on multiple entity instances.
        /// </summary>
        EntityCollection = 2
    }
}
