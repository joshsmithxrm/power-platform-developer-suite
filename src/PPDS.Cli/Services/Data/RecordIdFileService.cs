using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Reads the record ids that <c>data delete --file</c> acts on (#1915). The ids are taken only from the column the
/// user names (<c>--id-column</c>) or, without it, from the table's primary id column read from its metadata, by name,
/// for JSON and CSV alike. Nothing is guessed: a record without that column, a value that is not an id and an id given
/// twice are refused, with their count and positions, before any record is read or changed.
/// </summary>
public interface IRecordIdFileService
{
    /// <summary>
    /// Reads the ids from <paramref name="filePath"/>: a JSON array of id strings or of objects, a JSON object with a
    /// <c>records</c> array, or a CSV file with a header row.
    /// </summary>
    /// <param name="filePath">The file to read.</param>
    /// <param name="entity">Logical name of the table the ids belong to.</param>
    /// <param name="idColumn">The column holding the ids; when omitted, the table's primary id column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ids, in file order, each once. Empty when the file holds no entries.</returns>
    /// <exception cref="PpdsException">
    /// <c>Validation.FileNotFound</c> when the file does not exist; <c>Validation.RecordIdFileInvalid</c> when the file
    /// is not exactly a list of ids; <c>Query.ExecutionFailed</c> when the table's primary id column cannot be read.
    /// </exception>
    Task<IReadOnlyList<Guid>> ReadIdsAsync(
        string filePath,
        string entity,
        string? idColumn,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RecordIdFileService : IRecordIdFileService
{
    private const int MaxPositionsShown = 10;

    private readonly IMetadataQueryService _metadata;
    private readonly ILogger<RecordIdFileService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RecordIdFileService"/>.
    /// </summary>
    /// <param name="metadata">Metadata service the table's primary id column is read from.</param>
    /// <param name="logger">Logger instance.</param>
    public RecordIdFileService(IMetadataQueryService metadata, ILogger<RecordIdFileService> logger)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ReadIdsAsync(
        string filePath,
        string entity,
        string? idColumn,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));
        if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentNullException(nameof(entity));

        if (!File.Exists(filePath))
        {
            throw new PpdsException(ErrorCodes.Validation.FileNotFound,
                $"The file '{Path.GetFileName(filePath)}' was not found. Nothing was deleted.");
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var column = string.IsNullOrWhiteSpace(idColumn)
            ? await ReadPrimaryIdColumnAsync(entity, cancellationToken)
            : idColumn.Trim();

        return Parse(content, column);
    }

    /// <summary>
    /// Parses the ids of a file's <paramref name="content"/>, taking them only from <paramref name="idColumn"/>.
    /// </summary>
    /// <param name="content">The file's text.</param>
    /// <param name="idColumn">The column (JSON property or CSV header) that holds the ids.</param>
    /// <returns>The ids, in file order.</returns>
    /// <exception cref="PpdsException">With <c>Validation.RecordIdFileInvalid</c> when anything is not exactly an id.</exception>
    internal static IReadOnlyList<Guid> Parse(string content, string idColumn)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(idColumn);

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            return ParseJson(content, idColumn);
        }

        return ParseCsv(content, idColumn);
    }

    private async Task<string> ReadPrimaryIdColumnAsync(string entity, CancellationToken cancellationToken)
    {
        string? primaryId;
        try
        {
            var metadata = await _metadata.GetEntityAsync(
                entity,
                includeAttributes: false,
                includeRelationships: false,
                includeKeys: false,
                includePrivileges: false,
                cancellationToken: cancellationToken);
            primaryId = metadata.PrimaryIdAttribute;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the primary id column of entity {Entity}", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"The primary id column of '{entity}' could not be read (does the table exist?), so the ids in the file " +
                "cannot be found. Nothing was deleted. Name the column that holds the ids with --id-column.", ex);
        }

        if (string.IsNullOrWhiteSpace(primaryId))
        {
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"The metadata of '{entity}' names no primary id column, so the ids in the file cannot be found. " +
                "Nothing was deleted. Name the column that holds the ids with --id-column.");
        }

        return primaryId;
    }

    private static IReadOnlyList<Guid> ParseJson(string content, string idColumn)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw Invalid($"The file is not valid JSON (line {(ex.LineNumber ?? 0) + 1}). Nothing was deleted.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement entries;
            if (root.ValueKind == JsonValueKind.Array)
            {
                entries = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.EnumerateObject().Count(p => string.Equals(p.Name, "records", StringComparison.OrdinalIgnoreCase)) > 1)
            {
                // JsonDocument keeps every duplicate and TryGetProperty would pick one of them: refuse instead.
                throw Invalid("The JSON file has the 'records' key more than once, so the ids are not certain. " +
                              "Nothing was deleted.");
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("records", out var records) &&
                     records.ValueKind == JsonValueKind.Array)
            {
                entries = records;
            }
            else
            {
                throw Invalid("The JSON file is neither an array of ids or records nor an object with a 'records' " +
                              "array. Nothing was deleted.");
            }

            var problems = new Problems("entry", "entries");
            var ids = new List<Guid>();
            var seen = new HashSet<Guid>();
            var position = 0;

            foreach (var element in entries.EnumerateArray())
            {
                position++;
                JsonElement value;
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var matching = element.EnumerateObject()
                        .Where(p => string.Equals(p.Name, idColumn, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matching.Count == 0)
                    {
                        problems.Missing.Add(position);
                        continue;
                    }

                    if (matching.Count > 1)
                    {
                        problems.Repeated.Add(position);
                        continue;
                    }

                    // A record that holds any property twice is not well-formed JSON for this purpose: refuse it
                    // rather than decide which copy the user meant.
                    if (element.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
                    {
                        problems.RepeatedProperty.Add(position);
                        continue;
                    }

                    value = matching[0].Value;
                }
                else
                {
                    value = element;
                }

                if (value.ValueKind != JsonValueKind.String || !TryReadId(value.GetString(), out var id))
                {
                    problems.NotAnId.Add(position);
                    continue;
                }

                if (!seen.Add(id))
                {
                    problems.Duplicate.Add(position);
                    continue;
                }

                ids.Add(id);
            }

            problems.ThrowIfAny(idColumn);
            return ids;
        }
    }

    private static IReadOnlyList<Guid> ParseCsv(string content, string idColumn)
    {
        // The file is read by the CSV rules (quoted fields, commas and line breaks inside quotes), and it is refused as a
        // whole when any record's field count differs from the header's or a quote is malformed: a shifted row could
        // otherwise put another column's id (a parent's, say) under the id column (#1915).
        var table = StrictCsvReader.Read(content);
        if (table.Header is null)
        {
            return [];
        }

        var headers = table.Header.Fields.Select(h => h.Trim()).ToList();
        var matches = Enumerable.Range(0, headers.Count)
            .Where(i => string.Equals(headers[i], idColumn, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            throw Invalid(DescribeMissingHeaderColumn(headers, idColumn));
        }

        if (matches.Count > 1)
        {
            throw Invalid($"The CSV file has the column '{idColumn}' {matches.Count} times, so the ids are not " +
                          "certain. Nothing was deleted.");
        }

        if (!table.IsExact)
        {
            throw Invalid($"The CSV file cannot be read exactly: {string.Join("; ", table.DescribeProblems())}. " +
                          "Nothing was deleted. Quote every field that holds a comma, a quote or a line break, and " +
                          "give every record as many fields as the header.");
        }

        var idIndex = matches[0];
        var problems = new Problems("line", "lines");
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var row in table.Rows)
        {
            if (!TryReadId(row.Fields[idIndex].Trim(), out var id))
            {
                problems.NotAnId.Add(row.LineNumber);
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Duplicate.Add(row.LineNumber);
                continue;
            }

            ids.Add(id);
        }

        problems.ThrowIfAny(idColumn);
        return ids;
    }

    /// <summary>
    /// The refusal for a header without the id column. Header cells are named only when every one looks like a column
    /// name, so a file without a header (its first line a record) never has its values repeated.
    /// </summary>
    private static string DescribeMissingHeaderColumn(IReadOnlyList<string> headers, string idColumn)
    {
        var head = $"The first line of the CSV file has no column '{idColumn}' ({headers.Count} " +
                   $"{(headers.Count == 1 ? "column" : "columns")} found). Nothing was deleted.";
        var hint = $"The file needs a header row naming the column that holds the ids; name another column with --id-column.";
        if (headers.All(IsColumnName))
        {
            return $"{head} Columns: {string.Join(", ", headers)}. {hint}";
        }

        return $"{head} The first line does not look like a header. {hint}";
    }

    private static bool IsColumnName(string text)
        => text.Length is > 0 and <= 128 &&
           (char.IsAsciiLetter(text[0]) || text[0] == '_') &&
           text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool TryReadId(string? text, out Guid id)
        => Guid.TryParse(text, out id) && id != Guid.Empty;

    private static PpdsException Invalid(string message, Exception? inner = null)
        => inner is null
            ? new PpdsException(ErrorCodes.Validation.RecordIdFileInvalid, message)
            : new PpdsException(ErrorCodes.Validation.RecordIdFileInvalid, message, inner);

    /// <summary>The positions of each kind of entry that is not exactly one id; any of them refuses the whole file.</summary>
    private sealed class Problems
    {
        private readonly string _one;
        private readonly string _many;

        public Problems(string one, string many)
        {
            _one = one;
            _many = many;
        }

        public List<int> Missing { get; } = [];
        public List<int> Repeated { get; } = [];
        public List<int> RepeatedProperty { get; } = [];
        public List<int> NotAnId { get; } = [];
        public List<int> Duplicate { get; } = [];

        public void ThrowIfAny(string idColumn)
        {
            var parts = new List<string>();
            Add(parts, Missing, $"has no '{idColumn}'", $"have no '{idColumn}'");
            Add(parts, Repeated, $"names '{idColumn}' more than once", $"name '{idColumn}' more than once");
            Add(parts, RepeatedProperty, "holds another property more than once", "hold another property more than once");
            Add(parts, NotAnId, "holds a value that is not a record id", "hold a value that is not a record id");
            Add(parts, Duplicate, "repeats an id given earlier in the file", "repeat an id given earlier in the file");
            if (parts.Count == 0)
            {
                return;
            }

            throw Invalid($"The file is not a list of '{idColumn}' values: {string.Join("; ", parts)}. Nothing was " +
                          $"deleted. Correct the file, or name the column that holds the ids with --id-column.");
        }

        private void Add(List<string> parts, List<int> positions, string whatOne, string whatMany)
        {
            if (positions.Count == 0)
            {
                return;
            }

            var shown = string.Join(", ", positions.Take(MaxPositionsShown));
            var more = positions.Count > MaxPositionsShown ? $" and {positions.Count - MaxPositionsShown} more" : "";
            var noun = positions.Count == 1 ? _one : _many;
            var what = positions.Count == 1 ? whatOne : whatMany;
            parts.Add($"{positions.Count} {(positions.Count == 1 ? "entry" : "entries")} {what} ({noun} {shown}{more})");
        }
    }
}
