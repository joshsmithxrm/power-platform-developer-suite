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
/// Implements <see cref="IDataUpdateValueService"/>: reads the table's column metadata once and converts every value
/// through <see cref="ColumnValueConverter"/>.
/// </summary>
public sealed class DataUpdateValueService : IDataUpdateValueService
{
    private const int ProblemsShown = 20;

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

        var problems = new List<string>();
        var pairs = new List<(string Field, string Value)>();
        foreach (var pair in ParseSetPairs(setText))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex <= 0)
            {
                problems.Add($"--set pair {pairs.Count + problems.Count + 1} is not field=value " +
                    "(a comma separates pairs; put a value that holds a comma in double quotes).");
                continue;
            }

            pairs.Add((pair[..eqIndex].Trim(), pair[(eqIndex + 1)..].Trim()));
        }

        if (pairs.Count == 0 && problems.Count == 0)
        {
            problems.Add("--set gives no field=value pair.");
        }

        ThrowIfAny(problems, "--set");

        var columns = await GetColumnsAsync(entity, cancellationToken).ConfigureAwait(false);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, text) in pairs)
        {
            var column = FindColumn(columns, field, $"--set column '{field}'", problems);
            if (column == null)
            {
                continue;
            }

            if (values.ContainsKey(column.LogicalName))
            {
                problems.Add($"--set column '{field}' is given more than once.");
                continue;
            }

            if (text.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                values[column.LogicalName] = null;
                continue;
            }

            var converted = ColumnValueConverter.Convert(text, column);
            if (converted.IsAdmitted)
            {
                values[column.LogicalName] = converted.Value;
            }
            else
            {
                problems.Add($"--set column '{field}': {converted.Refusal}");
            }
        }

        ThrowIfAny(problems, "--set");
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

        var content = (await File.ReadAllTextAsync(csvPath, cancellationToken).ConfigureAwait(false)).Trim();

        Dictionary<string, string>? columnMappings = null;
        if (mappingPath != null)
        {
            var mappingContent = await File.ReadAllTextAsync(mappingPath, cancellationToken).ConfigureAwait(false);
            using var mappingDoc = JsonDocument.Parse(mappingContent);
            if (mappingDoc.RootElement.TryGetProperty("columns", out var mappedColumns))
            {
                columnMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in mappedColumns.EnumerateObject())
                {
                    if (col.Value.TryGetProperty("field", out var field) && field.GetString() is { } fieldName)
                    {
                        columnMappings[col.Name] = fieldName;
                    }
                }
            }
        }

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return [];
        }

        var headers = ParseCsvLine(lines[0]).Select(h => h.Trim()).ToList();
        var actualIdColumn = idColumn ?? $"{entity}id";
        var idColIndex = headers.FindIndex(h => h.Equals(actualIdColumn, StringComparison.OrdinalIgnoreCase));
        if (idColIndex < 0)
        {
            idColIndex = headers.FindIndex(h => h.Equals("id", StringComparison.OrdinalIgnoreCase));
        }

        if (idColIndex < 0)
        {
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                $"Could not find ID column '{actualIdColumn}'. Specify --id-column. Available: {string.Join(", ", headers)}");
        }

        // Every header is a column of the table that can be updated, or nothing is read further.
        var columns = await GetColumnsAsync(entity, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        var targets = new AttributeMetadata?[headers.Count];
        for (var j = 0; j < headers.Count; j++)
        {
            if (j == idColIndex)
            {
                continue;
            }

            var fieldName = columnMappings != null && columnMappings.TryGetValue(headers[j], out var mapped)
                ? mapped
                : headers[j];
            targets[j] = FindColumn(columns, fieldName, $"Header '{headers[j]}'", problems);
        }

        ThrowIfAny(problems, csvPath);

        var entities = new List<Entity>();
        for (var i = 1; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var row = i;
            var values = ParseCsvLine(line);
            if (values.Count != headers.Count)
            {
                problems.Add($"Row {row}: the row has {values.Count} cell(s) and the header has {headers.Count}.");
                continue;
            }

            if (!Guid.TryParse(values[idColIndex].Trim().Trim('"'), out var recordId))
            {
                problems.Add($"Row {row}, column '{headers[idColIndex]}': the record id is not a GUID.");
                continue;
            }

            var updateEntity = new Entity(entity, recordId);
            for (var j = 0; j < headers.Count; j++)
            {
                if (j == idColIndex || targets[j] is not { } column)
                {
                    continue;
                }

                var text = values[j].Trim().Trim('"');
                if (string.IsNullOrEmpty(text))
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
                    problems.Add($"Row {row}, column '{headers[j]}': {converted.Refusal}");
                }
            }

            entities.Add(updateEntity);
        }

        ThrowIfAny(problems, csvPath);
        return entities;
    }

    private async Task<Dictionary<string, AttributeMetadata>> GetColumnsAsync(string entity, CancellationToken cancellationToken)
    {
        EntityMetadata metadata;
        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var response = (RetrieveEntityResponse)await client.ExecuteAsync(new RetrieveEntityRequest
            {
                LogicalName = entity,
                EntityFilters = EntityFilters.Attributes
            }, cancellationToken).ConfigureAwait(false);
            metadata = response.EntityMetadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException)
        {
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"The columns of table '{entity}' could not be read, so the values cannot be checked against their types. Nothing was updated.",
                ex);
        }

        return (metadata.Attributes ?? [])
            .Where(a => !string.IsNullOrEmpty(a.LogicalName))
            .ToDictionary(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);
    }

    private static AttributeMetadata? FindColumn(
        Dictionary<string, AttributeMetadata> columns, string field, string where, List<string> problems)
    {
        if (!columns.TryGetValue(field, out var column))
        {
            problems.Add($"{where}: '{field}' is not a column of the table.");
            return null;
        }

        if (column.IsValidForUpdate == false)
        {
            problems.Add($"{where}: '{column.LogicalName}' cannot be updated.");
            return null;
        }

        return column;
    }

    private static void ThrowIfAny(List<string> problems, string source)
    {
        if (problems.Count == 0)
        {
            return;
        }

        var message = new StringBuilder();
        message.Append(problems.Count == 1
            ? "1 value is not valid for its column; nothing was updated:"
            : $"{problems.Count} values are not valid for their columns; nothing was updated:");
        foreach (var problem in problems.Take(ProblemsShown))
        {
            message.Append(System.Environment.NewLine).Append("  ").Append(problem);
        }

        if (problems.Count > ProblemsShown)
        {
            message.Append(System.Environment.NewLine).Append($"  ... and {problems.Count - ProblemsShown} more");
        }

        throw new PpdsException(ErrorCodes.Validation.InvalidValue, message.ToString(),
            new Dictionary<string, object> { ["source"] = source, ["problemCount"] = problems.Count });
    }

    /// <summary>
    /// Parses field=value pairs, respecting quoted values that may contain commas.
    /// </summary>
    private static List<string> ParseSetPairs(string setString)
    {
        var pairs = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in setString)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                var pair = current.ToString().Trim();
                if (!string.IsNullOrEmpty(pair))
                {
                    pairs.Add(pair);
                }

                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        var finalPair = current.ToString().Trim();
        if (!string.IsNullOrEmpty(finalPair))
        {
            pairs.Add(finalPair);
        }

        return pairs;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values;
    }
}
