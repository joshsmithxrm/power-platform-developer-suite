using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using CsvHelper.Configuration;

namespace PPDS.Cli.Services.Data;

/// <summary>One record of a CSV file: its fields, and the file line it starts on (1-based).</summary>
/// <param name="LineNumber">The line of the file the record starts on; a quoted field may carry it over later lines.</param>
/// <param name="Fields">The record's fields, unquoted, in order.</param>
internal sealed record CsvRow(int LineNumber, IReadOnlyList<string> Fields);

/// <summary>A record whose number of fields differs from the header's.</summary>
/// <param name="LineNumber">The line the record starts on.</param>
/// <param name="FieldCount">The number of fields found on it.</param>
internal sealed record CsvFieldCountMismatch(int LineNumber, int FieldCount);

/// <summary>
/// The result of <see cref="StrictCsvReader.Read"/>: the header and the data rows, and every reason the file cannot be
/// read exactly. A caller acts on the rows only when <see cref="IsExact"/> is true; otherwise it refuses the whole file.
/// </summary>
internal sealed class StrictCsvTable
{
    /// <summary>The first record of the file, or <see langword="null"/> when the file holds no record.</summary>
    public CsvRow? Header { get; init; }

    /// <summary>The records after the header, in file order. Blank lines are not records.</summary>
    public IReadOnlyList<CsvRow> Rows { get; init; } = [];

    /// <summary>Data rows whose field count is not the header's.</summary>
    public IReadOnlyList<CsvFieldCountMismatch> FieldCountMismatches { get; init; } = [];

    /// <summary>Lines of records holding a quote the CSV rules do not allow (inside an unquoted field, or after a closing quote).</summary>
    public IReadOnlyList<int> MalformedQuoteLines { get; init; } = [];

    /// <summary>The line on which a quoted field opens and is never closed (the file ends inside it), if any.</summary>
    public int? UnclosedQuoteLine { get; init; }

    /// <summary>True when every record has the header's field count and every quote is well formed.</summary>
    public bool IsExact => FieldCountMismatches.Count == 0 && MalformedQuoteLines.Count == 0 && UnclosedQuoteLine is null;

    /// <summary>
    /// Describes why the file is not exact, without repeating any of its content: line numbers (at most
    /// <paramref name="maxLines"/> of each kind) and field counts. Empty when <see cref="IsExact"/>.
    /// </summary>
    /// <param name="maxLines">How many line numbers of each kind to name.</param>
    public IReadOnlyList<string> DescribeProblems(int maxLines = 10)
    {
        var parts = new List<string>();
        if (UnclosedQuoteLine is { } open)
        {
            parts.Add($"a quoted field opened on line {open} is never closed, so the file ends inside it");
        }

        if (MalformedQuoteLines.Count > 0)
        {
            parts.Add($"{Count(MalformedQuoteLines.Count, "record has", "records have")} a quote that is not allowed " +
                      $"there ({Lines(MalformedQuoteLines, maxLines)})");
        }

        if (FieldCountMismatches.Count > 0)
        {
            var expected = Header?.Fields.Count ?? 0;
            var shown = string.Join(", ", FieldCountMismatches.Take(maxLines)
                .Select(m => $"line {m.LineNumber}: {m.FieldCount}"));
            var more = FieldCountMismatches.Count > maxLines ? $", and {FieldCountMismatches.Count - maxLines} more" : "";
            parts.Add($"{Count(FieldCountMismatches.Count, "record has", "records have")} a number of fields other than " +
                      $"the header's {expected} ({shown}{more})");
        }

        return parts;
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string Lines(IReadOnlyList<int> lines, int max)
    {
        var shown = string.Join(", ", lines.Take(max));
        var more = lines.Count > max ? $", and {lines.Count - max} more" : "";
        return $"{(lines.Count == 1 ? "line" : "lines")} {shown}{more}";
    }
}

/// <summary>
/// Reads comma-separated text exactly, by the CSV rules (RFC 4180) as CsvHelper implements them: quoted fields, doubled
/// quotes, commas and line breaks inside quoted fields, CRLF and LF line ends, a leading byte-order mark. The first
/// record is the header. Nothing is dropped or guessed: blank lines are skipped, and every other irregularity is
/// reported in the returned <see cref="StrictCsvTable"/> for the caller to refuse the file with.
/// </summary>
internal static class StrictCsvReader
{
    /// <summary>Reads <paramref name="content"/> into a header and rows, with every irregularity found.</summary>
    /// <param name="content">The file's text.</param>
    /// <returns>The table; check <see cref="StrictCsvTable.IsExact"/> before using its rows.</returns>
    public static StrictCsvTable Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 0 && content[0] == '﻿')
        {
            content = content[1..];
        }

        var badData = false;
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            Delimiter = ",",
            IgnoreBlankLines = true,
            BadDataFound = _ => badData = true,
        };

        CsvRow? header = null;
        var rows = new List<CsvRow>();
        var malformed = new List<int>();
        int? unclosed = null;

        using var parser = new CsvParser(new StringReader(content), config);
        while (parser.Read())
        {
            var raw = parser.RawRecord;
            var line = parser.RawRow - CountInnerLineBreaks(raw);
            var fields = parser.Record ?? [];

            // A line of only spaces is blank, not a record of one empty field.
            if (fields.Length == 1 && string.IsNullOrWhiteSpace(fields[0]) && !raw.Contains('"'))
            {
                badData = false;
                continue;
            }

            if (badData)
            {
                badData = false;
                if (raw.Count(c => c == '"') % 2 == 1)
                {
                    // The open quote swallowed the file's last line break too, so every break in it is inside the record.
                    unclosed ??= parser.RawRow - CountLineBreaks(raw);
                }
                else
                {
                    malformed.Add(line);
                }
            }

            var row = new CsvRow(line, fields);
            if (header is null)
            {
                header = row;
            }
            else
            {
                rows.Add(row);
            }
        }

        var expected = header?.Fields.Count ?? 0;
        return new StrictCsvTable
        {
            Header = header,
            Rows = rows,
            FieldCountMismatches = rows.Where(r => r.Fields.Count != expected)
                .Select(r => new CsvFieldCountMismatch(r.LineNumber, r.Fields.Count)).ToList(),
            MalformedQuoteLines = malformed,
            UnclosedQuoteLine = unclosed,
        };
    }

    /// <summary>The line breaks inside a record's raw text, not counting the one that ends it.</summary>
    private static int CountInnerLineBreaks(string raw)
        => CountLineBreaks(raw.EndsWith("\r\n", StringComparison.Ordinal) ? raw[..^2]
            : raw.EndsWith('\n') || raw.EndsWith('\r') ? raw[..^1]
            : raw);

    /// <summary>The line breaks (CRLF, LF or CR) in <paramref name="text"/>.</summary>
    private static int CountLineBreaks(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                count++;
            }
        }

        return count;
    }
}
