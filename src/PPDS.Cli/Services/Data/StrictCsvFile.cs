using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;

namespace PPDS.Cli.Services.Data;

/// <summary>A CSV file read exactly: its header and every record, each with the header's number of fields.</summary>
/// <param name="Headers">The header's names, as written.</param>
/// <param name="Rows">The records in file order; <c>Rows[i]</c> is data row <c>i + 1</c>.</param>
public sealed record StrictCsvTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Reads the CSV files of <c>data load</c> and <c>data update</c> with one rule (#1916, #1917): comma-separated, RFC 4180
/// quoting (a field in double quotes may hold commas, line breaks and doubled quotes), UTF-8 with or without a byte
/// order mark, blank lines skipped. Nothing is trimmed. The file is refused, naming the rows, when it is empty, a header
/// name is empty or repeated (ignoring case), a quote is stray or unterminated, or a record has more or fewer fields than
/// the header.
/// </summary>
public static class StrictCsvFile
{
    /// <summary>
    /// Reads <paramref name="path"/> whole, or throws <see cref="DataValuesRefusedException"/> listing every problem.
    /// </summary>
    public static async Task<StrictCsvTable> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            DetectDelimiter = false,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.None,
        };

        var problems = new List<DataValueProblem>();
        var rows = new List<IReadOnlyList<string>>();
        string[] headers;
        var rowNumber = 0;
        var reading = 1; // the data row being read; the library reads a record's fields only when they are used
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            using var csv = new CsvReader(reader, configuration);
            if (!await csv.ReadAsync().ConfigureAwait(false))
            {
                throw new DataValuesRefusedException([new DataValueProblem(null, null, "The file is empty: it has no header.")]);
            }

            csv.ReadHeader();
            headers = csv.HeaderRecord ?? [];
            problems.AddRange(HeaderProblems(headers));

            while (await csv.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowNumber++;
                reading = rowNumber;
                var count = csv.Parser.Count;
                if (count != headers.Length)
                {
                    problems.Add(new DataValueProblem(rowNumber, null,
                        $"The row has {count} field(s) and the header has {headers.Length}; quote a value that holds a comma."));
                    continue;
                }

                var fields = new string[count];
                for (var i = 0; i < count; i++)
                {
                    fields[i] = csv.Parser[i] ?? string.Empty;
                }

                rows.Add(fields);
                reading = rowNumber + 1;
            }
        }
        catch (CsvHelperException ex)
        {
            // A stray or unterminated quote. The library's message repeats the text, so only the position is kept.
            problems.Add(new DataValueProblem(reading, null,
                "The row is not valid CSV: a double quote is stray or not closed. A field that holds a quote must be in " +
                "double quotes, with each quote in it doubled."));
            throw new DataValuesRefusedException(problems, ex);
        }

        if (problems.Count > 0)
        {
            throw new DataValuesRefusedException(problems);
        }

        return new StrictCsvTable(headers, rows);
    }

    private static IEnumerable<DataValueProblem> HeaderProblems(string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(headers[i]))
            {
                yield return new DataValueProblem(null, null, $"Header field {i + 1} is empty.");
            }
        }

        foreach (var repeated in headers
                     .Where(h => !string.IsNullOrWhiteSpace(h))
                     .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            yield return new DataValueProblem(null, repeated.Key,
                $"The header names this column {repeated.Count()} times (ignoring case), so which value is meant is not certain.");
        }
    }
}
