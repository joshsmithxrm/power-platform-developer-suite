namespace PPDS.Plugins
{
    /// <summary>
    /// Specifies the invocation source for a plugin step.
    /// </summary>
    public enum PluginInvocationSource
    {
        /// <summary>
        /// Parent pipeline (0). Plugin is invoked from the parent execution context.
        /// Use to restrict execution to the top-level pipeline only.
        /// </summary>
        Parent = 0,

        /// <summary>
        /// Child pipeline (1). Plugin is invoked from a child execution context.
        /// Use to restrict execution to nested/child operations only.
        /// </summary>
        Child = 1
    }
}
