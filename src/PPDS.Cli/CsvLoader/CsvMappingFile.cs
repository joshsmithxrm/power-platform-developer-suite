using System.Text.Json;
using PPDS.Cli.Services.Data;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Reads a mapping file for <c>data load --mapping</c> and <c>data update --mapping</c> (#1916, #1917). The JSON is read
/// as a document first, so that what the serializer would drop silently is refused: two <c>columns</c> entries with the
/// same name or names equal ignoring case (the serializer keeps the last), a property of an entry or of its
/// <c>lookup</c> whose name is not one of the mapping's (such as <c>"Skip"</c> for <c>"skip"</c>, or a typo; names that
/// start with <c>_</c> are annotations and are allowed), and a <c>field</c> or <c>skip</c> of the wrong type. JSON
/// that is not valid is refused too. Every refusal is a <see cref="DataValuesRefusedException"/> that names the entry and
/// repeats nothing of the file.
/// </summary>
internal static class CsvMappingFile
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string[] EntryProperties = ["field", "skip", "lookup", "dateFormat", "optionsetMap"];

    private static readonly string[] LookupProperties = ["entity", "matchBy", "keyField"];

    /// <summary>Reads <paramref name="json"/> into a mapping, or throws <see cref="DataValuesRefusedException"/>.</summary>
    public static CsvMappingConfig Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
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

            if (document.RootElement.TryGetProperty("columns", out var columns))
            {
                if (columns.ValueKind != JsonValueKind.Object)
                {
                    throw Refused(null, "The mapping file's 'columns' is not a JSON object.");
                }

                var problems = new List<DataValueProblem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var column in columns.EnumerateObject())
                {
                    if (!seen.Add(column.Name))
                    {
                        problems.Add(new DataValueProblem(null, column.Name,
                            "The mapping file has more than one entry for this column (the same name, or names equal ignoring case), so which is meant is not certain."));
                        continue;
                    }

                    EntryProblems(column, problems);
                }

                if (problems.Count > 0)
                {
                    throw new DataValuesRefusedException(problems);
                }
            }
        }

        try
        {
            return JsonSerializer.Deserialize<CsvMappingConfig>(json, Options)
                ?? throw Refused(null, "The mapping file is empty.");
        }
        catch (JsonException)
        {
            throw Refused(null, "The mapping file does not have a mapping's shape: a property has a value of the wrong type.");
        }
    }

    private static void EntryProblems(JsonProperty column, List<DataValueProblem> problems)
    {
        if (column.Value.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new DataValueProblem(null, column.Name, "The mapping file's entry for this column is not a JSON object."));
            return;
        }

        foreach (var property in column.Value.EnumerateObject())
        {
            if (UnknownName(property.Name, EntryProperties) is { } unknown)
            {
                problems.Add(new DataValueProblem(null, column.Name, unknown));
            }
        }

        if (column.Value.TryGetProperty("field", out var field) && field.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            problems.Add(new DataValueProblem(null, column.Name, "The mapping file's 'field' for this column is not text."));
        }

        if (column.Value.TryGetProperty("skip", out var skip) && skip.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add(new DataValueProblem(null, column.Name, "The mapping file's 'skip' for this column is not true or false."));
        }

        if (column.Value.TryGetProperty("lookup", out var lookup) && lookup.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in lookup.EnumerateObject())
            {
                if (UnknownName(property.Name, LookupProperties) is { } unknown)
                {
                    problems.Add(new DataValueProblem(null, column.Name, "lookup: " + unknown));
                }
            }
        }
    }

    private static string? UnknownName(string name, string[] known)
    {
        if (name.StartsWith('_') || known.Contains(name, StringComparer.Ordinal))
        {
            return null;
        }

        var meant = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return meant != null
            ? $"The mapping file's property '{name}' is not one of the mapping's; names are case-sensitive: '{meant}'."
            : $"The mapping file's property '{name}' is not one of the mapping's ({string.Join(", ", known)}).";
    }

    private static DataValuesRefusedException Refused(string? column, string message) =>
        new([new DataValueProblem(null, column, message)]);
}
