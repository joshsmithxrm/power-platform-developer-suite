using System;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Shared utility methods for query value operations: type checks, column lookups, and data type formatting.
/// Centralizes logic previously duplicated across join nodes, sort nodes, and expression compilers.
/// </summary>
internal static class QueryValueHelper
{
    /// <summary>
    /// Returns true if the value is a numeric type (int, long, short, byte, decimal, double, float).
    /// </summary>
    public static bool IsNumeric(object value)
        => value is int or long or short or byte or decimal or double or float;

    /// <summary>
    /// Retrieves the raw value of a column from a <see cref="QueryRow"/>.
    /// Tries exact key match first, then falls back to case-insensitive comparison.
    /// </summary>
    public static object? GetColumnValue(QueryRow row, string columnName)
    {
        if (row.Values.TryGetValue(columnName, out var qv))
            return qv.Value;

        foreach (var kvp in row.Values)
        {
            if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
                return kvp.Value.Value;
        }

        return null;
    }

    /// <summary>
    /// Formats a ScriptDom <see cref="DataTypeReference"/> as a lowercase string.
    /// E.g., SqlDataTypeOption.Int → "int", SqlDataTypeOption.NVarChar with param 100 → "nvarchar(100)".
    /// </summary>
    public static string FormatDataType(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            var name = sqlType.SqlDataTypeOption.ToString().ToLowerInvariant();
            if (sqlType.Parameters.Count > 0)
            {
                var parms = string.Join(", ", sqlType.Parameters.Select(p => p.Value));
                return $"{name}({parms})";
            }
            return name;
        }

        if (dataType is XmlDataTypeReference)
            return "xml";

        if (dataType.Name?.Identifiers is { Count: > 0 } ids)
            return string.Join(".", ids.Select(i => i.Value));

        return "nvarchar";
    }
}
