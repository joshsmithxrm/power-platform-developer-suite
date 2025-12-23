using System.Collections.Generic;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace PPDS.Dataverse.DependencyInjection
{
    /// <summary>
    /// Root configuration options for Dataverse connection pooling and operations.
    /// Supports both single-environment (Connections) and multi-environment (Environments) configurations.
    /// </summary>
    public class DataverseOptions
    {
        #region Multi-Environment Support

        /// <summary>
        /// Gets or sets named environments for multi-environment scenarios.
        /// Use this for source/target migration scenarios or dev/qa/prod configurations.
        /// Key: environment name (e.g., "source", "target", "dev", "prod")
        /// </summary>
        public Dictionary<string, DataverseEnvironmentOptions> Environments { get; set; } = new();

        /// <summary>
        /// Gets or sets the default environment name.
        /// Used when no environment is explicitly specified.
        /// </summary>
        public string? DefaultEnvironment { get; set; }

        #endregion

        #region Root-Level Configuration (Single Environment / Defaults)

        /// <summary>
        /// Gets or sets the default Dataverse environment URL.
        /// Can be overridden at the connection or environment level.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the default Azure AD tenant ID.
        /// Can be overridden at the connection or environment level.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the connection configurations.
        /// For single-environment scenarios, or as the default when no Environments are defined.
        /// </summary>
        public List<DataverseConnection> Connections { get; set; } = new();

        #endregion

        /// <summary>
        /// Gets or sets the connection pool settings.
        /// </summary>
        public ConnectionPoolOptions Pool { get; set; } = new();

        /// <summary>
        /// Gets or sets the resilience and retry settings.
        /// </summary>
        public ResilienceOptions Resilience { get; set; } = new();

        /// <summary>
        /// Gets or sets the bulk operation settings.
        /// </summary>
        public BulkOperationOptions BulkOperations { get; set; } = new();

        /// <summary>
        /// Gets or sets the adaptive rate control settings.
        /// Controls how parallelism adjusts based on throttle responses.
        /// </summary>
        public AdaptiveRateOptions AdaptiveRate { get; set; } = new();
    }
}
