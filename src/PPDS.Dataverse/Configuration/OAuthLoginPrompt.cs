namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// OAuth login prompt behavior.
    /// </summary>
    public enum OAuthLoginPrompt
    {
        /// <summary>
        /// Attempt silent authentication, prompt only if needed.
        /// </summary>
        Auto,

        /// <summary>
        /// Always prompt for credentials.
        /// </summary>
        Always,

        /// <summary>
        /// Never prompt - fail if silent auth is not possible.
        /// </summary>
        Never,

        /// <summary>
        /// Force re-authentication by discarding cached credentials.
        /// </summary>
        SelectAccount
    }
}
