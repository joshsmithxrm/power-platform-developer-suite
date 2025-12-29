using System;
using System.Collections.Generic;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Exception thrown when exported data contains columns that don't exist in the target environment.
    /// </summary>
    public class SchemaMismatchException : Exception
    {
        /// <summary>
        /// Gets the missing columns by entity name.
        /// </summary>
        public IReadOnlyDictionary<string, List<string>> MissingColumns { get; }

        /// <summary>
        /// Gets the total count of missing columns across all entities.
        /// </summary>
        public int TotalMissingCount { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SchemaMismatchException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="missingColumns">Dictionary of entity name to list of missing column names.</param>
        public SchemaMismatchException(string message, Dictionary<string, List<string>> missingColumns)
            : base(message)
        {
            MissingColumns = missingColumns;
            TotalMissingCount = 0;
            foreach (var columns in missingColumns.Values)
            {
                TotalMissingCount += columns.Count;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SchemaMismatchException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="missingColumns">Dictionary of entity name to list of missing column names.</param>
        /// <param name="innerException">The inner exception.</param>
        public SchemaMismatchException(string message, Dictionary<string, List<string>> missingColumns, Exception innerException)
            : base(message, innerException)
        {
            MissingColumns = missingColumns;
            TotalMissingCount = 0;
            foreach (var columns in missingColumns.Values)
            {
                TotalMissingCount += columns.Count;
            }
        }
    }
}
