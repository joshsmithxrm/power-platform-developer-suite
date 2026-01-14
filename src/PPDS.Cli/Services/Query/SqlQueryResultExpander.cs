using PPDS.Dataverse.Query;
using PPDS.Dataverse.Sql.Transpilation;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Expands SQL query results to include *name columns for lookups, optionsets, and booleans.
/// </summary>
/// <remarks>
/// This matches the behavior of the old VS Code extension, where querying "ownerid"
/// would automatically include both "ownerid" (GUID) and "owneridname" (display name) columns.
///
/// Column expansion rules:
/// - Lookup columns: ownerid → ownerid (GUID) + owneridname (display name)
/// - OptionSet columns: statuscode → statuscode (int) + statuscodename (label)
/// - Boolean columns: ismanaged → ismanaged (true/false) + ismanagedname (Yes/No)
///
/// Virtual column support:
/// - Users can query "owneridname" directly
/// - The transpiler converts this to query "ownerid"
/// - This expander populates the virtual column from the base column's FormattedValue
/// </remarks>
internal static class SqlQueryResultExpander
{
    private const string NameSuffix = "name";

    /// <summary>
    /// Expands a QueryResult to include *name columns for lookups, optionsets, and booleans.
    /// </summary>
    /// <param name="result">The original query result.</param>
    /// <param name="virtualColumns">Virtual columns detected by the transpiler.</param>
    /// <returns>A new QueryResult with expanded columns.</returns>
    public static QueryResult ExpandFormattedValueColumns(
        QueryResult result,
        IReadOnlyDictionary<string, VirtualColumnInfo>? virtualColumns = null)
    {
        virtualColumns ??= new Dictionary<string, VirtualColumnInfo>();

        if (result.Records.Count == 0 && virtualColumns.Count == 0)
        {
            return result;
        }

        // Detect which columns need auto-expansion by examining the first non-null values
        var columnsToAutoExpand = DetectExpandableColumns(result, virtualColumns);

        // If no virtual columns and no auto-expansion needed, return original
        if (virtualColumns.Count == 0 && columnsToAutoExpand.Count == 0)
        {
            return result;
        }

        // Build the expanded column list
        var expandedColumns = BuildExpandedColumns(result.Columns, columnsToAutoExpand, virtualColumns);

        // Expand each record
        var expandedRecords = new List<IReadOnlyDictionary<string, QueryValue>>();
        foreach (var record in result.Records)
        {
            var expandedRecord = ExpandRecord(record, columnsToAutoExpand, virtualColumns);
            expandedRecords.Add(expandedRecord);
        }

        return new QueryResult
        {
            EntityLogicalName = result.EntityLogicalName,
            Columns = expandedColumns,
            Records = expandedRecords,
            Count = result.Count,
            TotalCount = result.TotalCount,
            MoreRecords = result.MoreRecords,
            PagingCookie = result.PagingCookie,
            PageNumber = result.PageNumber,
            ExecutionTimeMs = result.ExecutionTimeMs,
            ExecutedFetchXml = result.ExecutedFetchXml,
            IsAggregate = result.IsAggregate
        };
    }

    /// <summary>
    /// Detects which columns should be auto-expanded based on their value types.
    /// Excludes columns that have explicitly queried virtual *name columns.
    /// </summary>
    private static Dictionary<string, ExpandableColumnInfo> DetectExpandableColumns(
        QueryResult result,
        IReadOnlyDictionary<string, VirtualColumnInfo> virtualColumns)
    {
        var expandableColumns = new Dictionary<string, ExpandableColumnInfo>(StringComparer.OrdinalIgnoreCase);
        var existingColumnNames = new HashSet<string>(
            result.Columns.Select(c => c.LogicalName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var column in result.Columns)
        {
            var nameColumn = column.LogicalName + NameSuffix;

            // Skip if *name column already exists in result columns
            if (existingColumnNames.Contains(nameColumn))
            {
                continue;
            }

            // Skip if user explicitly queried the *name column (it's a virtual column)
            // In this case, we only show what they asked for, not auto-expand
            if (virtualColumns.ContainsKey(nameColumn))
            {
                continue;
            }

            // Find the first non-null value for this column to determine its type
            QueryValue? sampleValue = null;
            foreach (var record in result.Records)
            {
                if (record.TryGetValue(column.LogicalName, out var value) && value.Value != null)
                {
                    sampleValue = value;
                    break;
                }
            }

            if (sampleValue == null)
            {
                continue;
            }

            // Determine if this column should be expanded
            ExpandableColumnType? columnType = null;
            if (sampleValue.IsLookup)
            {
                columnType = ExpandableColumnType.Lookup;
            }
            else if (sampleValue.IsOptionSet)
            {
                columnType = ExpandableColumnType.OptionSet;
            }
            else if (sampleValue.IsBoolean && sampleValue.HasFormattedValue)
            {
                columnType = ExpandableColumnType.Boolean;
            }

            if (columnType.HasValue)
            {
                expandableColumns[column.LogicalName] = new ExpandableColumnInfo
                {
                    OriginalColumn = column,
                    NameColumnName = nameColumn,
                    Type = columnType.Value
                };
            }
        }

        return expandableColumns;
    }

    /// <summary>
    /// Builds the expanded column list by inserting *name columns after their base columns.
    /// </summary>
    private static List<QueryColumn> BuildExpandedColumns(
        IReadOnlyList<QueryColumn> originalColumns,
        Dictionary<string, ExpandableColumnInfo> columnsToAutoExpand,
        IReadOnlyDictionary<string, VirtualColumnInfo> virtualColumns)
    {
        var expanded = new List<QueryColumn>();
        var addedVirtualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in originalColumns)
        {
            var columnName = column.LogicalName;

            // Check if this base column has a virtual *name column that was explicitly queried
            var virtualNameColumn = columnName + NameSuffix;
            if (virtualColumns.TryGetValue(virtualNameColumn, out var virtualInfo))
            {
                // User explicitly queried the *name column
                if (virtualInfo.BaseColumnExplicitlyQueried)
                {
                    // User queried both: ownerid AND owneridname
                    // Add base column
                    expanded.Add(column);
                    // Add virtual *name column after it
                    expanded.Add(new QueryColumn
                    {
                        LogicalName = virtualNameColumn,
                        DataType = QueryColumnType.String,
                        LinkedEntityAlias = column.LinkedEntityAlias,
                        LinkedEntityName = column.LinkedEntityName
                    });
                    addedVirtualColumns.Add(virtualNameColumn);
                }
                else
                {
                    // User only queried *name column - hide the base column
                    // Add only the virtual *name column
                    expanded.Add(new QueryColumn
                    {
                        LogicalName = virtualNameColumn,
                        DataType = QueryColumnType.String,
                        LinkedEntityAlias = column.LinkedEntityAlias,
                        LinkedEntityName = column.LinkedEntityName
                    });
                    addedVirtualColumns.Add(virtualNameColumn);
                }
            }
            else
            {
                // No virtual column for this - add the original column
                expanded.Add(column);

                // If this column should be auto-expanded, add the *name column immediately after
                if (columnsToAutoExpand.TryGetValue(columnName, out var expandInfo))
                {
                    expanded.Add(new QueryColumn
                    {
                        LogicalName = expandInfo.NameColumnName,
                        DataType = QueryColumnType.String,
                        LinkedEntityAlias = column.LinkedEntityAlias,
                        LinkedEntityName = column.LinkedEntityName
                    });
                }
            }
        }

        return expanded;
    }

    /// <summary>
    /// Expands a single record to include *name values.
    /// </summary>
    private static Dictionary<string, QueryValue> ExpandRecord(
        IReadOnlyDictionary<string, QueryValue> originalRecord,
        Dictionary<string, ExpandableColumnInfo> columnsToAutoExpand,
        IReadOnlyDictionary<string, VirtualColumnInfo> virtualColumns)
    {
        var expanded = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in originalRecord)
        {
            var columnName = kvp.Key;
            var value = kvp.Value;

            // Check if this base column has a virtual *name column that was explicitly queried
            var virtualNameColumn = columnName + NameSuffix;
            if (virtualColumns.TryGetValue(virtualNameColumn, out var virtualInfo))
            {
                // Determine the column type for proper value handling
                var columnType = DetermineColumnType(value);

                if (virtualInfo.BaseColumnExplicitlyQueried)
                {
                    // User queried both: add base column with raw value
                    expanded[columnName] = CreateBaseColumnValue(value, columnType);
                    // Add virtual *name column with formatted value
                    expanded[virtualNameColumn] = CreateNameColumnValue(value, columnType);
                }
                else
                {
                    // User only queried *name column - only add the *name column
                    expanded[virtualNameColumn] = CreateNameColumnValue(value, columnType);
                }
            }
            else if (columnsToAutoExpand.TryGetValue(columnName, out var expandInfo))
            {
                // Auto-expand: add both base and *name columns
                expanded[columnName] = CreateBaseColumnValue(value, expandInfo.Type);
                expanded[expandInfo.NameColumnName] = CreateNameColumnValue(value, expandInfo.Type);
            }
            else
            {
                // Non-expanded column - keep as-is
                expanded[columnName] = value;
            }
        }

        return expanded;
    }

    /// <summary>
    /// Determines the column type from a QueryValue.
    /// </summary>
    private static ExpandableColumnType DetermineColumnType(QueryValue value)
    {
        if (value.IsLookup)
            return ExpandableColumnType.Lookup;
        if (value.IsOptionSet)
            return ExpandableColumnType.OptionSet;
        if (value.IsBoolean)
            return ExpandableColumnType.Boolean;
        // Default to OptionSet for values with FormattedValue (safest assumption)
        return ExpandableColumnType.OptionSet;
    }

    /// <summary>
    /// Creates the QueryValue for the base column (raw value only).
    /// </summary>
    private static QueryValue CreateBaseColumnValue(QueryValue originalValue, ExpandableColumnType columnType)
    {
        if (originalValue.Value == null)
        {
            return QueryValue.Null;
        }

        // For lookups, keep the lookup metadata but remove FormattedValue
        // so display layer shows the GUID
        if (columnType == ExpandableColumnType.Lookup && originalValue.LookupEntityId.HasValue)
        {
            return new QueryValue
            {
                Value = originalValue.Value, // The GUID
                FormattedValue = null,       // Force display of raw GUID
                LookupEntityType = originalValue.LookupEntityType,
                LookupEntityId = originalValue.LookupEntityId
            };
        }

        // For optionsets and booleans, just return raw value without FormattedValue
        return QueryValue.Simple(originalValue.Value);
    }

    /// <summary>
    /// Creates the QueryValue for a *name column.
    /// </summary>
    private static QueryValue CreateNameColumnValue(QueryValue originalValue, ExpandableColumnType columnType)
    {
        if (originalValue.Value == null)
        {
            return QueryValue.Null;
        }

        var formattedValue = originalValue.FormattedValue ?? string.Empty;

        // For lookups, preserve the lookup metadata so both columns are clickable
        if (columnType == ExpandableColumnType.Lookup && originalValue.LookupEntityId.HasValue)
        {
            return new QueryValue
            {
                Value = formattedValue,
                FormattedValue = null, // No further formatting needed
                LookupEntityType = originalValue.LookupEntityType,
                LookupEntityId = originalValue.LookupEntityId
            };
        }

        // For optionsets and booleans, just use the formatted value as a simple string
        return QueryValue.Simple(formattedValue);
    }

    private enum ExpandableColumnType
    {
        Lookup,
        OptionSet,
        Boolean
    }

    private sealed class ExpandableColumnInfo
    {
        public required QueryColumn OriginalColumn { get; init; }
        public required string NameColumnName { get; init; }
        public required ExpandableColumnType Type { get; init; }
    }
}
