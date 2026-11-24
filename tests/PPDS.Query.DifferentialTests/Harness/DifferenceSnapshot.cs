using System.Globalization;
using System.Security.Cryptography;
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
/// What the run evaluated: how many evaluations, and the SHA-256 of the list of them (seed, index, row and expression
/// text, one per line, equal ones included). The snapshot's header pins it, so the run cannot evaluate fewer or other
/// expressions than it recorded and still pass.
/// </summary>
public sealed record RunFingerprint(int Evaluations, string Sha256)
{
    /// <summary>The header line that records it.</summary>
    public string HeaderLine => string.Create(CultureInfo.InvariantCulture, $"# Evaluations: {Evaluations}. Evaluation list SHA-256: {Sha256}.");

    /// <summary>The fingerprint of a run's evaluations.</summary>
    public static RunFingerprint Of(IEnumerable<(int Seed, int Index, Evaluation Evaluation)> evaluations)
    {
        var list = new StringBuilder();
        var count = 0;
        foreach (var (seed, index, evaluation) in evaluations)
        {
            count++;
            list.Append(CultureInfo.InvariantCulture, $"{seed}\t{index}\t{evaluation.RowId}\t{evaluation.Expression.ScalarSql}\n");
        }
        return new RunFingerprint(count, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(list.ToString()))).ToLowerInvariant());
    }

    /// <summary>Reads the fingerprint from a snapshot's text.</summary>
    public static RunFingerprint Read(string snapshot)
    {
        const string prefix = "# Evaluations: ";
        var line = snapshot.Split('\n').SingleOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))
                   ?? throw new HarnessException("The snapshot does not record its evaluation count and list hash.");
        var parts = line[prefix.Length..].Split(". Evaluation list SHA-256: ");
        if (parts.Length != 2 || !parts[1].EndsWith('.') || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new HarnessException($"The snapshot's evaluation line is malformed: {line}");
        return new RunFingerprint(count, parts[1][..^1]);
    }
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
        var seen = new Dictionary<(string, int), (string SqlServer, string Ppds, string FirstSeen)>();
        var rows = new Dictionary<(string, int), SnapshotRow>();
        foreach (var (seed, index, evaluation) in evaluations)
        {
            var key = (evaluation.Expression.ScalarSql, evaluation.RowId);
            var outcomes = (evaluation.Reference.ToString(), evaluation.Actual.ToString(), $"seed {seed} expression {index}");

            // The same expression generated again gives the same outcomes, equal or not, or the engines are not
            // deterministic: checked before equal evaluations are left out.
            if (seen.TryGetValue(key, out var first))
            {
                if (first.SqlServer != outcomes.Item1 || first.Ppds != outcomes.Item2)
                {
                    throw new HarnessException(
                        $"The same evaluation gave two outcomes in one run: row {key.RowId}: {key.ScalarSql}: "
                        + $"SQL Server [{first.SqlServer}] / PPDS [{first.Ppds}] ({first.FirstSeen}), then SQL Server [{outcomes.Item1}] / PPDS [{outcomes.Item2}] ({outcomes.Item3}).");
                }
                continue;
            }
            seen[key] = outcomes;

            if (evaluation.IsEqual)
                continue;
            var row = new SnapshotRow(key.ScalarSql, key.RowId, outcomes.Item1, outcomes.Item2, outcomes.Item3, "");
            rows[row.Key] = row with { Note = notes.GetValueOrDefault((row.Key, row.SqlServer, row.Ppds), "") };
        }
        return rows.Values.OrderBy(r => r.Expression, StringComparer.Ordinal).ThenBy(r => r.RowId).ToList();
    }

    /// <summary>The file's text for <paramref name="rows"/> of the run with <paramref name="fingerprint"/>.</summary>
    public static string Format(IReadOnlyList<SnapshotRow> rows, RunFingerprint fingerprint)
    {
        var text = new StringBuilder();
        text.Append("# Every evaluation of the default differential run that differs from SQL Server, exactly. Regenerate with the command in tests/PPDS.Query.DifferentialTests/README.md.\n");
        text.Append(CultureInfo.InvariantCulture, $"# Run: seeds {string.Join(",", DefaultRun.Seeds)}, {DefaultRun.Count} expressions per seed, depth up to {DefaultRun.Depth}. Rows: {rows.Count}.\n");
        text.Append(fingerprint.HeaderLine).Append('\n');
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
        var duplicate = rows.GroupBy(r => r.Key).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new HarnessException($"The snapshot has {duplicate.Count()} rows for row {duplicate.Key.RowId}: {duplicate.Key.Expression}");
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
