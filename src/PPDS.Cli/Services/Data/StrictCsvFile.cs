using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;

namespace PPDS.Cli.Services.Data;

/// <summary>A CSV file read exactly: its header and every record, each with the header's number of fields.</summary>
/// <param name="Headers">The header's names, as written.</param>
/// <param name="Rows">The records in file order; <c>Rows[i]</c> is data row <c>i + 1</c>.</param>
public sealed record StrictCsvFileTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Reads the CSV files of <c>data load</c> and <c>data update</c> with one rule (#1916, #1917): comma-separated, RFC 4180
/// quoting (a field in double quotes may hold commas, line breaks and doubled quotes), UTF-8 with or without a byte
/// order mark, blank lines skipped. Nothing is trimmed. The file is refused, naming the rows, when it is empty, it is not
/// valid UTF-8 (a file saved in another encoding, such as Windows-1252, or a sequence cut off), a header name is empty or
/// repeated (ignoring case), a quote is stray or unterminated, or a record has more or fewer fields than the header.
/// </summary>
public static class StrictCsvFile
{
    /// <summary>
    /// Reads <paramref name="path"/> whole, or throws <see cref="DataValuesRefusedException"/> listing every problem.
    /// </summary>
    public static async Task<StrictCsvFileTable> ReadAsync(string path, CancellationToken cancellationToken = default)
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
            // Invalid bytes throw instead of becoming U+FFFD, so a file in another encoding is refused, never rewritten.
            using var reader = new StreamReader(path, StrictUtf8, detectEncodingFromByteOrderMarks: true);
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
        catch (Exception ex) when (IsInvalidText(ex))
        {
            // The reader decodes ahead of the record being read, so no row can be named.
            throw new DataValuesRefusedException([new DataValueProblem(null, null,
                "The file is not valid UTF-8 text: it may be saved in another encoding (such as Windows-1252) or cut off. " +
                "Save it as UTF-8 (CSV UTF-8); nothing was written.")]);
        }
        catch (CsvHelperException)
        {
            // A stray or unterminated quote. The library's message and its exception repeat the text, so neither is kept:
            // only the position.
            problems.Add(new DataValueProblem(reading, null,
                "The row is not valid CSV: a double quote is stray or not closed. A field that holds a quote must be in " +
                "double quotes, with each quote in it doubled."));
            throw new DataValuesRefusedException(problems);
        }

        if (problems.Count > 0)
        {
            throw new DataValuesRefusedException(problems);
        }

        return new StrictCsvFileTable(headers, rows);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool IsInvalidText(Exception ex) =>
        ex is DecoderFallbackException || ex.InnerException is DecoderFallbackException;

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
