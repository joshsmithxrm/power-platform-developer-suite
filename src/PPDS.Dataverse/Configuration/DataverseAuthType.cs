namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// Authentication type for Dataverse connections.
    /// </summary>
    public enum DataverseAuthType
    {
        /// <summary>
        /// Client ID and client secret authentication (Service Principal).
        /// Recommended for production server-to-server scenarios.
        /// </summary>
        ClientSecret,

        /// <summary>
        /// Client ID and certificate authentication (Service Principal).
        /// More secure than ClientSecret for high-security environments.
        /// </summary>
        Certificate,

        /// <summary>
        /// OAuth interactive authentication.
        /// For development and user-context scenarios.
        /// </summary>
        OAuth
    }
}
