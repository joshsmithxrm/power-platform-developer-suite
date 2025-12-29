using System;

namespace PPDS.Migration.Export
{
    /// <summary>
    /// Options for export operations.
    /// </summary>
    public class ExportOptions
    {
        /// <summary>
        /// Gets or sets the degree of parallelism for entity export.
        /// Default: ProcessorCount * 2
        /// </summary>
        public int DegreeOfParallelism { get; set; } = Environment.ProcessorCount * 2;

        /// <summary>
        /// Gets or sets the page size for FetchXML queries.
        /// Default: 5000
        /// </summary>
        public int PageSize { get; set; } = 5000;

        /// <summary>
        /// Gets or sets the progress reporting interval in records.
        /// Default: 100
        /// </summary>
        public int ProgressInterval { get; set; } = 100;
    }
}
