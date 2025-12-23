using System.Collections.Generic;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// Configuration for a named Dataverse environment.
    /// Used for multi-environment scenarios like source/target migrations.
    /// </summary>
    public class DataverseEnvironmentOptions
    {
        /// <summary>
        /// Gets or sets the environment name.
        /// Example: "dev", "qa", "prod", "source", "target"
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Dataverse environment URL.
        /// Example: https://contoso-dev.crm.dynamics.com
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the Azure AD tenant ID for this environment.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the connections for this environment.
        /// Multiple connections enable load distribution across Application Users.
        /// </summary>
        public List<DataverseConnection> Connections { get; set; } = new();

        /// <summary>
        /// Gets whether this environment has any connections configured.
        /// </summary>
        public bool HasConnections => Connections.Count > 0;
    }
}
