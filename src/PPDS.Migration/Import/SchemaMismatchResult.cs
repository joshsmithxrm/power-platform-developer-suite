using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Result of schema mismatch detection between exported data and target environment.
    /// </summary>
    public class SchemaMismatchResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SchemaMismatchResult"/> class.
        /// </summary>
        /// <param name="missingColumns">Dictionary of entity name to list of missing column names.</param>
        public SchemaMismatchResult(IReadOnlyDictionary<string, List<string>> missingColumns)
        {
            MissingColumns = missingColumns ?? throw new ArgumentNullException(nameof(missingColumns));
        }

        /// <summary>
        /// Gets the missing columns by entity name.
        /// </summary>
        public IReadOnlyDictionary<string, List<string>> MissingColumns { get; }

        /// <summary>
        /// Gets whether there are any missing columns.
        /// </summary>
        public bool HasMissingColumns => MissingColumns.Count > 0;

        /// <summary>
        /// Gets the total number of missing columns across all entities.
        /// </summary>
        public int TotalMissingCount => MissingColumns.Values.Sum(v => v.Count);

        /// <summary>
        /// Builds a detailed error message describing the missing columns.
        /// </summary>
        /// <returns>A formatted error message.</returns>
        public string BuildDetailedMessage()
        {
            var details = new StringBuilder();
            details.AppendLine($"Schema mismatch: {TotalMissingCount} column(s) in exported data do not exist in target environment.");
            details.AppendLine();

            foreach (var (entity, columns) in MissingColumns.OrderBy(x => x.Key))
            {
                details.AppendLine($"  {entity}:");
                foreach (var col in columns)
                {
                    details.AppendLine($"    - {col}");
                }
            }

            details.AppendLine();
            details.Append("Use --skip-missing-columns to import anyway (these columns will be skipped).");

            return details.ToString();
        }
    }
}
