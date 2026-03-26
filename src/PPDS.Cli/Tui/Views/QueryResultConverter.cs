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

        // Add columns and track their types.
        // Handle duplicate column names (e.g., SELECT accountid, accountid FROM account)
        // by appending _1, _2, etc. to subsequent duplicates.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in result.Columns)
        {
            var name = column.LogicalName;
            var uniqueName = name;
            var counter = 1;
            while (!usedNames.Add(uniqueName))
            {
                uniqueName = $"{name}_{counter++}";
            }
            table.Columns.Add(uniqueName, typeof(string));
            columnTypes[uniqueName] = column.DataType;
        }

        // Add rows using column index to handle duplicate column names correctly.
        // Each column in result.Columns maps to the corresponding DataTable column by index.
        foreach (var record in result.Records)
        {
            var row = table.NewRow();
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var column = result.Columns[i];
                if (record.TryGetValue(column.LogicalName, out var value))
                {
                    // Use index to set value in the correct DataTable column
                    row[i] = FormatValue(value);
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

}
