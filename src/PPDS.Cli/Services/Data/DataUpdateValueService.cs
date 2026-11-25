using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Implements <see cref="IDataUpdateValueService"/>: reads the table's metadata once and converts every value through
/// <see cref="ColumnValueConverter"/>. Every problem found is listed in one <see cref="DataValuesRefusedException"/>.
/// </summary>
public sealed class DataUpdateValueService : IDataUpdateValueService
{
    private const string QuotingHelp =
        "write the value unquoted when it holds no comma and no double quote, else wrap the whole value in double quotes " +
        "and double each quote in it (name=\"Smith, \"\"Jr\"\"\")";

    private readonly IDataverseConnectionPool _pool;

    /// <summary>
    /// Initializes a new instance of <see cref="DataUpdateValueService"/>.
    /// </summary>
    /// <param name="pool">The Dataverse connection pool, used to read the table's metadata.</param>
    public DataUpdateValueService(IDataverseConnectionPool pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>> ConvertSetValuesAsync(
        string entity,
        string setText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(setText);

        var problems = new List<DataValueProblem>();
        var pairs = ParseSet(setText, problems);
        ThrowIfAny(problems);

        var metadata = await GetMetadataAsync(entity, cancellationToken).ConfigureAwait(false);
        var columns = Columns(metadata);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var column = FindColumn(columns, pair.Field, pair.Field, problems);
            if (column == null)
            {
                continue;
            }

            if (values.ContainsKey(column.LogicalName))
            {
                problems.Add(new DataValueProblem(null, pair.Field, $"--set pair {pair.Number}: the column is given more than once."));
                continue;
            }

            if (!pair.Quoted && pair.Value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                values[column.LogicalName] = null;
                continue;
            }

            var converted = ColumnValueConverter.Convert(pair.Value, column);
            if (converted.IsAdmitted)
            {
                values[column.LogicalName] = converted.Value;
            }
            else
            {
                problems.Add(new DataValueProblem(null, pair.Field, $"--set pair {pair.Number}: {converted.Refusal}"));
            }
        }

        ThrowIfAny(problems);
        return values;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entity>> ReadUpdateFileAsync(
        string entity,
        string csvPath,
        string? idColumn,
        string? mappingPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);

        Dictionary<string, string>? columnMappings = null;
        if (mappingPath != null)
        {
            var mappingContent = await File.ReadAllTextAsync(mappingPath, cancellationToken).ConfigureAwait(false);
            columnMappings = ReadColumnMappings(mappingContent);
        }

        // The file is read exactly first: quoting, line breaks in quoted fields, field counts, header names (#1936).
        var table = await StrictCsvFile.ReadAsync(csvPath, cancellationToken).ConfigureAwait(false);
        var headers = table.Headers;

        var metadata = await GetMetadataAsync(entity, cancellationToken).ConfigureAwait(false);
        var columns = Columns(metadata);
        string FieldOf(string header) =>
            columnMappings != null && columnMappings.TryGetValue(header, out var mapped) ? mapped : header;

        // The id column: the one named with --id-column, else the table's primary id column from metadata. Never guessed.
        var problems = new List<DataValueProblem>();
        int idIndex;
        if (idColumn != null)
        {
            idIndex = IndexOf(headers, h => string.Equals(h, idColumn, StringComparison.OrdinalIgnoreCase));
            if (idIndex < 0)
            {
                throw new DataValuesRefusedException([new DataValueProblem(null, idColumn,
                    "The file has no column with this name (given with --id-column).")]);
            }
        }
        else
        {
            var primaryId = metadata.PrimaryIdAttribute;
            if (string.IsNullOrEmpty(primaryId))
            {
                throw new DataValuesRefusedException([new DataValueProblem(null, null,
                    "The table's primary id column is not in its metadata; name the id column with --id-column.")]);
            }

            idIndex = IndexOf(headers, h => string.Equals(FieldOf(h), primaryId, StringComparison.OrdinalIgnoreCase));
            if (idIndex < 0)
            {
                throw new DataValuesRefusedException([new DataValueProblem(null, null,
                    $"The file has no column '{primaryId}' (the table's primary id column); name the id column with --id-column.")]);
            }
        }

        // Every other header is a column of the table that can be updated, and no two name the same column.
        var targets = new AttributeMetadata?[headers.Count];
        var seenFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < headers.Count; j++)
        {
            if (j == idIndex)
            {
                continue;
            }

            var column = FindColumn(columns, FieldOf(headers[j]), headers[j], problems);
            if (column == null)
            {
                continue;
            }

            if (seenFields.TryGetValue(column.LogicalName, out var other))
            {
                problems.Add(new DataValueProblem(null, headers[j], $"The column names '{column.LogicalName}', as column '{other}' does."));
                continue;
            }

            seenFields[column.LogicalName] = headers[j];
            targets[j] = column;
        }

        ThrowIfAny(problems);

        var entities = new List<Entity>();
        var rowOfId = new Dictionary<Guid, int>();
        for (var index = 0; index < table.Rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = index + 1;
            var values = table.Rows[index];

            if (!RecordIdText.TryParse(values[idIndex], out var recordId))
            {
                problems.Add(new DataValueProblem(row, headers[idIndex],
                    $"The cell is not a record id: {RecordIdText.Rule}; and not the empty (all-zero) GUID."));
                continue;
            }

            if (rowOfId.TryGetValue(recordId, out var firstRow))
            {
                problems.Add(new DataValueProblem(row, headers[idIndex], $"The record id is also on row {firstRow}."));
                continue;
            }

            rowOfId[recordId] = row;
            var updateEntity = new Entity(entity, recordId);
            for (var j = 0; j < headers.Count; j++)
            {
                if (targets[j] is not { } column)
                {
                    continue;
                }

                var text = values[j];
                if (text.Length == 0)
                {
                    // An empty cell clears the column, as before.
                    updateEntity[column.LogicalName] = null;
                    continue;
                }

                var converted = ColumnValueConverter.Convert(text, column);
                if (converted.IsAdmitted)
                {
                    updateEntity[column.LogicalName] = converted.Value;
                }
                else
                {
                    problems.Add(new DataValueProblem(row, headers[j], converted.Refusal!));
                }
            }

            entities.Add(updateEntity);
        }

        ThrowIfAny(problems);
        return entities;
    }

    private sealed record SetPair(int Number, string Field, string Value, bool Quoted);

    /// <summary>
    /// Reads <c>--set</c>: pairs <c>field=value</c> separated by commas. A value is either unquoted, holding no comma and no
    /// double quote, taken exactly as written; or wrapped whole in double quotes, holding anything, with a doubled quote
    /// for one quote, taken exactly as written between the quotes. White space around a field name is ignored. Anything
    /// else is a problem naming the pair; nothing is guessed.
    /// </summary>
    private static List<SetPair> ParseSet(string text, List<DataValueProblem> problems)
    {
        var pairs = new List<SetPair>();
        var i = 0;
        var number = 0;
        while (true)
        {
            number++;
            var nameStart = i;
            while (i < text.Length && text[i] != '=' && text[i] != ',')
            {
                i++;
            }

            var field = text[nameStart..i].Trim();
            if (i >= text.Length || text[i] == ',')
            {
                problems.Add(new DataValueProblem(null, field.Length > 0 ? field : null,
                    $"--set pair {number} is not field=value; {QuotingHelp}."));
                if (i >= text.Length)
                {
                    break;
                }

                i++;
                continue;
            }

            i++; // past '='
            if (field.Length == 0)
            {
                problems.Add(new DataValueProblem(null, null, $"--set pair {number} has no field name before '='."));
            }

            string value;
            var quoted = i < text.Length && text[i] == '"';
            if (quoted)
            {
                var builder = new StringBuilder();
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            builder.Append('"');
                            i += 2;
                            continue;
                        }

                        closed = true;
                        i++;
                        break;
                    }

                    builder.Append(text[i]);
                    i++;
                }

                if (!closed)
                {
                    problems.Add(new DataValueProblem(null, field,
                        $"--set pair {number}: the double quote that opens the value is not closed; {QuotingHelp}."));
                    break;
                }

                if (i < text.Length && text[i] != ',')
                {
                    problems.Add(new DataValueProblem(null, field,
                        $"--set pair {number}: text follows the closing double quote; {QuotingHelp}."));
                    break;
                }

                value = builder.ToString();
            }
            else
            {
                var valueStart = i;
                while (i < text.Length && text[i] != ',')
                {
                    i++;
                }

                value = text[valueStart..i];
                if (value.Contains('"'))
                {
                    problems.Add(new DataValueProblem(null, field,
                        $"--set pair {number}: the value holds a double quote; {QuotingHelp}."));
                    value = "";
                }
                else if (value.Length == 0)
                {
                    problems.Add(new DataValueProblem(null, field,
                        $"--set pair {number} has no value; write null to clear the column."));
                }
            }

            if (field.Length > 0 && (quoted || value.Length > 0))
            {
                pairs.Add(new SetPair(number, field, value, quoted));
            }

            if (i >= text.Length)
            {
                break;
            }

            i++; // past ','
            if (i >= text.Length)
            {
                problems.Add(new DataValueProblem(null, null, $"--set ends with a comma: pair {number + 1} is empty."));
                break;
            }
        }

        return pairs;
    }

    private async Task<EntityMetadata> GetMetadataAsync(string entity, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var response = (RetrieveEntityResponse)await client.ExecuteAsync(new RetrieveEntityRequest
            {
                LogicalName = entity,
                EntityFilters = EntityFilters.Attributes
            }, cancellationToken).ConfigureAwait(false);
            return response.EntityMetadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException)
        {
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"The columns of table '{entity}' could not be read, so the values cannot be checked against their types. Nothing was updated.",
                ex);
        }
    }

    /// <summary>
    /// The <c>columns</c> of a mapping file: each header and the <c>field</c> it names. JSON that is not valid, or a
    /// <c>columns</c> or entry that is not an object, or a <c>field</c> that is not text, refuses the mapping file.
    /// </summary>
    private static Dictionary<string, string>? ReadColumnMappings(string content)
    {
        static DataValuesRefusedException Refused(string? column, string message) =>
            new([new DataValueProblem(null, column, message)]);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            // The parser's message can quote the file, so only the fact is kept.
            throw Refused(null, "The mapping file is not valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Refused(null, "The mapping file is not a JSON object.");
            }

            if (!document.RootElement.TryGetProperty("columns", out var mappedColumns))
            {
                return null;
            }

            if (mappedColumns.ValueKind != JsonValueKind.Object)
            {
                throw Refused(null, "The mapping file's 'columns' is not a JSON object.");
            }

            var columnMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var problems = new List<DataValueProblem>();
            foreach (var col in mappedColumns.EnumerateObject())
            {
                if (col.Value.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new DataValueProblem(null, col.Name, "The mapping file's entry for this column is not a JSON object."));
                }
                else if (col.Value.TryGetProperty("field", out var field) && field.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    problems.Add(new DataValueProblem(null, col.Name, "The mapping file's 'field' for this column is not text."));
                }
                else if (field.ValueKind == JsonValueKind.String && field.GetString() is { } fieldName
                         && !columnMappings.TryAdd(col.Name, fieldName))
                {
                    problems.Add(new DataValueProblem(null, col.Name,
                        "The mapping file has more than one entry for this column (ignoring case), so which is meant is not certain."));
                }
            }

            ThrowIfAny(problems);
            return columnMappings;
        }
    }

    private static Dictionary<string, AttributeMetadata> Columns(EntityMetadata metadata) =>
        (metadata.Attributes ?? [])
            .Where(a => !string.IsNullOrEmpty(a.LogicalName))
            .ToDictionary(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<string> headers, Func<string, bool> match)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            if (match(headers[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static AttributeMetadata? FindColumn(
        Dictionary<string, AttributeMetadata> columns, string field, string where, List<DataValueProblem> problems)
    {
        if (!columns.TryGetValue(field, out var column))
        {
            problems.Add(new DataValueProblem(null, where, $"'{field}' is not a column of the table."));
            return null;
        }

        if (column.IsValidForUpdate == false)
        {
            problems.Add(new DataValueProblem(null, where, $"'{column.LogicalName}' cannot be updated."));
            return null;
        }

        return column;
    }

    private static void ThrowIfAny(List<DataValueProblem> problems)
    {
        if (problems.Count > 0)
        {
            throw new DataValuesRefusedException(problems.ToList());
        }
    }
}
