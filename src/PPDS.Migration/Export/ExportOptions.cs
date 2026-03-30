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

        /// <summary>
        /// Gets or sets whether to include file column binary data in the export.
        /// When true, file column data is downloaded and stored in the ZIP archive.
        /// Default: false
        /// </summary>
        public bool IncludeFileData { get; set; } = false;

        /// <summary>
        /// Gets or sets the number of GUID range partitions per entity for page-level parallelism.
        /// 0 = auto (determined from record count), 1 = disabled (sequential only).
        /// Default: 0
        /// </summary>
        public int PageLevelParallelism { get; set; }

        /// <summary>
        /// Gets or sets the minimum record count before page-level parallelism activates.
        /// Entities with fewer records than this threshold use sequential paging.
        /// Default: 5000
        /// </summary>
        public int PageLevelParallelismThreshold { get; set; } = 5000;
    }
}
