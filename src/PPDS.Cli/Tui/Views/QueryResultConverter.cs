using System.Data;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Converts QueryResult to DataTable format for display in Terminal.Gui TableView.
/// </summary>
internal static class QueryResultConverter
{
    /// <summary>
    /// Converts a QueryResult to a DataTable with all values formatted as strings.
    /// </summary>
    public static DataTable ToDataTable(QueryResult result)
    {
        return ToDataTableWithTypes(result).Table;
    }

    /// <summary>
    /// Converts a QueryResult to a DataTable with all values formatted as strings,
    /// also returning column type metadata for display optimization.
    /// </summary>
    public static (DataTable Table, Dictionary<string, QueryColumnType> ColumnTypes) ToDataTableWithTypes(QueryResult result)
    {
        var table = new DataTable();
        var columnTypes = new Dictionary<string, QueryColumnType>();

        // Add columns and track their types
        foreach (var column in result.Columns)
        {
            table.Columns.Add(column.LogicalName, typeof(string));
            columnTypes[column.LogicalName] = column.DataType;
        }

        // Add rows
        foreach (var record in result.Records)
        {
            var row = table.NewRow();
            foreach (var column in result.Columns)
            {
                if (record.TryGetValue(column.LogicalName, out var value))
                {
                    row[column.LogicalName] = FormatValue(value);
                }
            }
            table.Rows.Add(row);
        }

        return (table, columnTypes);
    }

    /// <summary>
    /// Formats a QueryValue for display, using FormattedValue when available.
    /// </summary>
    public static string FormatValue(QueryValue value)
    {
        if (value.Value == null)
            return string.Empty;

        // Use formatted value if available (for lookups, optionsets, etc.)
        if (!string.IsNullOrEmpty(value.FormattedValue))
            return value.FormattedValue;

        return value.Value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
            bool b => b ? "Yes" : "No",
            decimal d => d.ToString("N2"),
            double dbl => dbl.ToString("N2"),
            Guid g => g.ToString(),
            _ => value.Value.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// Builds the Dynamics 365 record URL for a given entity and ID.
    /// </summary>
    public static string? BuildRecordUrl(string? environmentUrl, string? entityLogicalName, string? recordId)
    {
        if (string.IsNullOrEmpty(environmentUrl) ||
            string.IsNullOrEmpty(entityLogicalName) ||
            string.IsNullOrEmpty(recordId))
        {
            return null;
        }

        var baseUrl = environmentUrl.TrimEnd('/');
        return $"{baseUrl}/main.aspx?etn={entityLogicalName}&id={recordId}&pagetype=entityrecord";
    }
}
