namespace PPDS.Plugins
{
    /// <summary>
    /// Specifies the type of processing steps allowed for a Custom API.
    /// Controls whether synchronous and/or asynchronous processing steps can be registered.
    /// </summary>
    public enum ApiProcessingStepType
    {
        /// <summary>
        /// None (0). No custom processing steps are allowed for this API.
        /// Use when the Custom API handler plugin is sufficient and no additional steps are needed.
        /// </summary>
        None = 0,

        /// <summary>
        /// Async only (1). Only asynchronous processing steps are allowed.
        /// Use when additional processing must not block the caller.
        /// </summary>
        AsyncOnly = 1,

        /// <summary>
        /// Sync and async (2). Both synchronous and asynchronous processing steps are allowed.
        /// Use when additional processing may need to run before the response is returned.
        /// </summary>
        SyncAndAsync = 2
    }
}
