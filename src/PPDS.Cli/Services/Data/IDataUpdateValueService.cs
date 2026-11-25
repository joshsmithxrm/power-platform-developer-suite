using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Builds the values <c>data update</c> writes, by each target column's type from the table's metadata
/// (<see cref="ColumnValueConverter"/>), never guessed from the text and never in the machine's culture (#1917).
/// Every value is checked before anything is read or written: one that is not exact for its column refuses the update.
/// </summary>
public interface IDataUpdateValueService
{
    /// <summary>
    /// Converts the <c>--set</c> text into the values to write: <c>field=value</c> pairs separated by commas; a value is
    /// unquoted (no comma, no double quote, taken as written) or wrapped whole in double quotes with each quote in it
    /// doubled (taken as written between the quotes). Unquoted <c>null</c> (any case) clears a column; quoted, it is text.
    /// </summary>
    /// <param name="entity">Logical name of the table.</param>
    /// <param name="setText">The <c>--set</c> text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each column's logical name (as the metadata spells it) and the value to send.</returns>
    /// <exception cref="DataValuesRefusedException">
    /// <c>Validation.InvalidValue</c> when a pair is malformed, a column is not in the table, cannot be updated or is given
    /// twice, or a value is not exact for its column; each problem names the pair and column. Nothing has been read but the
    /// metadata.
    /// </exception>
    Task<IReadOnlyDictionary<string, object?>> ConvertSetValuesAsync(
        string entity,
        string setText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a <c>data update --file</c> CSV with <see cref="StrictCsvFile"/> (RFC 4180 quoting, every row with the header's
    /// number of fields, nothing trimmed): an id column and the columns to write, each cell converted for its column. The
    /// whole file is checked before anything is returned. An empty cell clears the column.
    /// </summary>
    /// <param name="entity">Logical name of the table.</param>
    /// <param name="csvPath">Path of the CSV file.</param>
    /// <param name="idColumn">The id column's header, or null for the table's primary id column from metadata.</param>
    /// <param name="mappingPath">Optional mapping JSON (<c>columns.{header}.field</c>), or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One record per data row, in file order.</returns>
    /// <exception cref="DataValuesRefusedException">
    /// <c>Validation.InvalidValue</c> when the file is not well-formed CSV (a stray quote, a row with more or fewer fields
    /// than the header, an empty or repeated header), the id column is missing, a row's id is not an exact GUID or repeats
    /// another row's, a header is not a column of the table or cannot be updated or names the same column as another, or
    /// a cell is not exact for its column; each problem names the row and column.
    /// </exception>
    Task<IReadOnlyList<Entity>> ReadUpdateFileAsync(
        string entity,
        string csvPath,
        string? idColumn,
        string? mappingPath,
        CancellationToken cancellationToken = default);
}
