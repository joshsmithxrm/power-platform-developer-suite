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
    /// Converts the <c>--set</c> text (<c>field=value,field2="value, with comma"</c>) into the values to write.
    /// <c>null</c> (any case) clears a column.
    /// </summary>
    /// <param name="entity">Logical name of the table.</param>
    /// <param name="setText">The <c>--set</c> text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each column's logical name (as the metadata spells it) and the value to send.</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// <c>Validation.InvalidValue</c> when a pair is malformed, a column is not in the table or cannot be updated, or a
    /// value is not exact for its column; the message names each column. Nothing has been read but the metadata.
    /// </exception>
    Task<IReadOnlyDictionary<string, object?>> ConvertSetValuesAsync(
        string entity,
        string setText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a <c>data update --file</c> CSV: an id column and the columns to write, each cell converted for its column.
    /// The whole file is checked before anything is returned. An empty cell clears the column.
    /// </summary>
    /// <param name="entity">Logical name of the table.</param>
    /// <param name="csvPath">Path of the CSV file.</param>
    /// <param name="idColumn">The id column's header, or null for <c>{entity}id</c> (then <c>id</c>).</param>
    /// <param name="mappingPath">Optional mapping JSON (<c>columns.{header}.field</c>), or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One record per data row, in file order.</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// <c>Validation.InvalidValue</c> when a row's id is not a GUID, a row has more or fewer cells than the header, a
    /// header is not a column of the table or cannot be updated, or a cell is not exact for its column; the message names
    /// each row and column.
    /// </exception>
    Task<IReadOnlyList<Entity>> ReadUpdateFileAsync(
        string entity,
        string csvPath,
        string? idColumn,
        string? mappingPath,
        CancellationToken cancellationToken = default);
}
