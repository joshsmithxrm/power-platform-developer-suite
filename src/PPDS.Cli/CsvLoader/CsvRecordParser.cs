using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Services.Data;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Converts CSV cells to Dataverse values by the target column's type, through <see cref="ColumnValueConverter"/>:
/// numbers in the invariant culture in one exact form, never guessed and never in the machine's culture (#1916).
/// </summary>
public sealed class CsvRecordParser
{
    /// <summary>
    /// Converts a CSV cell for the column.
    /// </summary>
    /// <param name="csvValue">The raw string value from the CSV.</param>
    /// <param name="attributeMetadata">The target attribute metadata.</param>
    /// <param name="mapping">Optional column mapping with additional configuration.</param>
    /// <returns>The converted value, or null if the cell is empty or is not exact for the column.</returns>
    public object? CoerceValue(
        string? csvValue,
        AttributeMetadata attributeMetadata,
        ColumnMappingEntry? mapping = null)
    {
        var (success, value, _) = TryCoerceValue(csvValue, attributeMetadata, mapping);
        return success ? value : null;
    }

    /// <summary>
    /// Converts a CSV cell for the column, returning success status and, when it is refused, why. An empty cell is a
    /// success with no value. The reason repeats nothing of the cell.
    /// </summary>
    public (bool Success, object? Value, string? ErrorMessage) TryCoerceValue(
        string? csvValue,
        AttributeMetadata attributeMetadata,
        ColumnMappingEntry? mapping = null)
    {
        if (string.IsNullOrEmpty(csvValue))
        {
            return (true, null, null);
        }

        var result = ColumnValueConverter.Convert(csvValue, attributeMetadata, new ColumnValueOptions
        {
            DateFormat = string.IsNullOrEmpty(mapping?.DateFormat) ? null : mapping.DateFormat,
            OptionLabels = mapping?.OptionsetMap
        });

        return (result.IsAdmitted, result.Value, result.Refusal);
    }

    /// <summary>
    /// Checks if a value is a GUID by the exact rule of <see cref="ColumnValueConverter"/>.
    /// </summary>
    public static bool IsGuid(string? value)
    {
        return ColumnValueConverter.TryReadExactGuid(value, out _);
    }
}
