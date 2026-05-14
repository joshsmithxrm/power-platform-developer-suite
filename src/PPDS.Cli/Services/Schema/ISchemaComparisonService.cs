using PPDS.Cli.Services.Schema.Models;
using PPDS.Cli.Services.Schema.Snapshots;

namespace PPDS.Cli.Services.Schema;

/// <summary>
/// Application service that diffs two schema snapshots and produces a
/// severity-categorized report.
/// </summary>
public interface ISchemaComparisonService
{
    /// <summary>
    /// Compare a source snapshot against a target. The source is treated as the
    /// "data being imported"; the target is the environment receiving it.
    /// </summary>
    /// <param name="source">Schema as it exists in the data package or source environment.</param>
    /// <param name="target">Schema as it exists in the target environment.</param>
    /// <returns>Report of differences, ordered deterministically.</returns>
    SchemaCompareReport Compare(SchemaSnapshot source, SchemaSnapshot target);
}
