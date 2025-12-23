using System.Collections.Generic;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace PPDS.Dataverse.DependencyInjection
{
    /// <summary>
    /// Root configuration options for Dataverse connection pooling and operations.
    /// </summary>
    public class DataverseOptions
    {
        /// <summary>
        /// Gets or sets the connection configurations.
        /// At least one connection is required.
        /// </summary>
        public List<DataverseConnection> Connections { get; set; } = new();

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
