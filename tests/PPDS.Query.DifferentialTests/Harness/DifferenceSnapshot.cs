using System.Globalization;
using System.Text;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// One differing evaluation, recorded exactly: the expression text and operand row (the key), SQL Server's outcome and
/// the engine's outcome in <see cref="Outcome.ToString"/> form, where the expression was first generated, and a
/// free-text note (for example an issue number) that nothing reads.
/// </summary>
public sealed record SnapshotRow(string Expression, int RowId, string SqlServer, string Ppds, string FirstSeen, string Note)
{
    /// <summary>The key a snapshot row is compared by.</summary>
    public (string Expression, int RowId) Key => (Expression, RowId);

    /// <inheritdoc />
    public override string ToString() => $"row {RowId}: {Expression}   SQL Server: {SqlServer}   PPDS: {Ppds}";
}

/// <summary>
/// The snapshot of every differing evaluation of the fixed default run, and the exact comparison with a new run.
/// The file is tab-separated, sorted by expression (ordinal) and row, UTF-8 without a byte order mark, with <c>\n</c>
/// line endings, so the same run writes the same bytes. Comment lines start with <c>#</c>.
/// </summary>
public static class DifferenceSnapshot
{
    /// <summary>The fixed default run the snapshot records: seeds, expressions per seed, maximum depth.</summary>
    public static readonly (int[] Seeds, int Count, int Depth) DefaultRun = ([1, 2, 3], 300, 3);

    /// <summary>The snapshot's file name, beside the test assembly and in the project's <c>Snapshots</c> folder.</summary>
    public const string FileName = "default-run-differences.tsv";

    private const string Header = "expression\trow\tsql_server\tppds\tfirst_seen\tnote";

    /// <summary>Builds the rows of a run's differing evaluations, carrying over the notes of an earlier snapshot for rows whose outcomes are unchanged.</summary>
    public static IReadOnlyList<SnapshotRow> FromRun(IEnumerable<(int Seed, int Index, Evaluation Evaluation)> evaluations, IReadOnlyList<SnapshotRow>? earlier = null)
    {
        var notes = (earlier ?? []).ToDictionary(r => (r.Key, r.SqlServer, r.Ppds), r => r.Note);
        var rows = new Dictionary<(string, int), SnapshotRow>();
        foreach (var (seed, index, evaluation) in evaluations)
        {
            if (evaluation.IsEqual)
                continue;
            var row = new SnapshotRow(
                evaluation.Expression.ScalarSql, evaluation.RowId, evaluation.Reference.ToString(), evaluation.Actual.ToString(),
                $"seed {seed} expression {index}", "");
            if (rows.TryGetValue(row.Key, out var existing))
            {
                // The same expression generated again gives the same outcomes, or the engines are not deterministic.
                if (existing.SqlServer != row.SqlServer || existing.Ppds != row.Ppds)
                    throw new HarnessException($"The same evaluation gave two outcomes in one run: {existing} and {row}.");
                continue;
            }
            rows[row.Key] = row with { Note = notes.GetValueOrDefault((row.Key, row.SqlServer, row.Ppds), "") };
        }
        return rows.Values.OrderBy(r => r.Expression, StringComparer.Ordinal).ThenBy(r => r.RowId).ToList();
    }

    /// <summary>The file's text for <paramref name="rows"/>.</summary>
    public static string Format(IReadOnlyList<SnapshotRow> rows)
    {
        var text = new StringBuilder();
        text.Append("# Every evaluation of the default differential run that differs from SQL Server, exactly. Regenerate with the command in tests/PPDS.Query.DifferentialTests/README.md.\n");
        text.Append(CultureInfo.InvariantCulture, $"# Run: seeds {string.Join(",", DefaultRun.Seeds)}, {DefaultRun.Count} expressions per seed, depth up to {DefaultRun.Depth}. Rows: {rows.Count}.\n");
        text.Append(Header).Append('\n');
        foreach (var row in rows)
        {
            foreach (var field in new[] { row.Expression, row.SqlServer, row.Ppds, row.FirstSeen, row.Note })
            {
                if (field.Contains('\t') || field.Contains('\n') || field.Contains('\r'))
                    throw new HarnessException($"A snapshot field contains a tab or a line break: {field}");
            }
            text.Append(row.Expression).Append('\t')
                .Append(row.RowId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.SqlServer).Append('\t')
                .Append(row.Ppds).Append('\t')
                .Append(row.FirstSeen).Append('\t')
                .Append(row.Note).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Parses the file's text.</summary>
    public static IReadOnlyList<SnapshotRow> Parse(string text)
    {
        var rows = new List<SnapshotRow>();
        var sawHeader = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (!sawHeader)
            {
                if (line != Header)
                    throw new HarnessException($"The snapshot's header is not [{Header}].");
                sawHeader = true;
                continue;
            }
            var fields = line.Split('\t');
            if (fields.Length != 6)
                throw new HarnessException($"A snapshot line has {fields.Length} fields, not 6: {line}");
            rows.Add(new SnapshotRow(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2], fields[3], fields[4], fields[5]));
        }
        if (!sawHeader)
            throw new HarnessException("The snapshot has no header line.");
        if (rows.Select(r => r.Key).Distinct().Count() != rows.Count)
            throw new HarnessException("The snapshot has two rows with the same expression and row.");
        return rows;
    }

    /// <summary>
    /// The exact comparison: differences found that the snapshot does not have, rows whose outcome on either side
    /// changed, and snapshot rows that no longer differ (or were not generated). The note is not compared.
    /// </summary>
    public static (IReadOnlyList<SnapshotRow> New, IReadOnlyList<(SnapshotRow Was, SnapshotRow Now)> Changed, IReadOnlyList<SnapshotRow> Gone)
        Compare(IReadOnlyList<SnapshotRow> snapshot, IReadOnlyList<SnapshotRow> found)
    {
        var recorded = snapshot.ToDictionary(r => r.Key);
        var now = found.ToDictionary(r => r.Key);
        var added = found.Where(r => !recorded.ContainsKey(r.Key)).ToList();
        var changed = new List<(SnapshotRow Was, SnapshotRow Now)>();
        foreach (var row in found)
        {
            if (recorded.TryGetValue(row.Key, out var was) && (was.SqlServer != row.SqlServer || was.Ppds != row.Ppds))
                changed.Add((was, row));
        }
        var gone = snapshot.Where(r => !now.ContainsKey(r.Key)).ToList();
        return (added, changed, gone);
    }
}
