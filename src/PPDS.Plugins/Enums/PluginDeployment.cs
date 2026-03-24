namespace PPDS.Plugins
{
    /// <summary>
    /// Specifies the deployment context in which a plugin step executes.
    /// </summary>
    public enum PluginDeployment
    {
        /// <summary>
        /// Server only (0). Plugin executes only on the Dataverse server.
        /// Use for most server-side operations.
        /// </summary>
        ServerOnly = 0,

        /// <summary>
        /// Offline only (1). Plugin executes only in the offline client.
        /// Use for operations that must run in disconnected scenarios.
        /// </summary>
        Offline = 1,

        /// <summary>
        /// Both server and offline (2). Plugin executes in both contexts.
        /// Use when the same logic must run online and offline.
        /// </summary>
        Both = 2
    }
}
