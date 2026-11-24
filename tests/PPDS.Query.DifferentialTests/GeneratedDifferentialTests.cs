using System.Globalization;
using System.Text;
using PPDS.Query.DifferentialTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Query.DifferentialTests;

/// <summary>Skipped, with the reason, unless a server and the report settings are configured.</summary>
public sealed class ReportRunFactAttribute : FactAttribute
{
    /// <summary>Sets <see cref="FactAttribute.Skip"/> when there is no server or no report seed.</summary>
    public ReportRunFactAttribute()
        => Skip = ReferenceServer.SkipReason
                  ?? (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_SQLDIFF_SEEDS"))
                      ? "Report run: set PPDS_SQLDIFF_SEEDS (and optionally PPDS_SQLDIFF_COUNT and PPDS_SQLDIFF_DEPTH) to run one."
                      : null);
}

/// <summary>
/// Generated expressions from the rule table, run on SQL Server and through the engine over every operand row.
/// </summary>
/// <remarks>
/// <para>
/// The gated test runs the fixed default run (seeds 1, 2 and 3, 300 expressions each, depth 3) and passes only when its
/// differing evaluations equal the committed snapshot exactly: a new difference, a changed outcome on either side, or
/// a recorded difference that no longer occurs fails, naming the rows. No rule decides that a difference resembles a
/// known one.
/// </para>
/// <para>
/// The report test runs other seeds or depths, from <c>PPDS_SQLDIFF_SEEDS</c>, <c>PPDS_SQLDIFF_COUNT</c> and
/// <c>PPDS_SQLDIFF_DEPTH</c>, and only reports: it lists the differences grouped for reading and passes unless the
/// harness itself fails. <c>PPDS_SQLDIFF_REPORT</c> names a directory to write each report to.
/// </para>
/// </remarks>
[Collection(ReferenceServerCollection.Name)]
[Trait("Category", "SqlDifferential")]
public sealed class GeneratedDifferentialTests(ReferenceDatabase database, ITestOutputHelper output)
{
    /// <summary>Set to a file path to write the snapshot of the default run there instead of comparing with it.</summary>
    public const string WriteSnapshotVariable = "PPDS_SQLDIFF_WRITE_SNAPSHOT";

    private const int RowsListed = 40;

    [ReferenceServerFact]
    public void The_default_run_differs_from_SQL_Server_exactly_as_the_snapshot_records()
    {
        var runner = new DifferentialRunner(new SqlServerEngine(database.Connection), new PpdsEngine(database.RowValues));
        var (seeds, count, depth) = DifferenceSnapshot.DefaultRun;
        var evaluations = seeds
            .SelectMany(seed => runner.Run(seed, count, depth, Operands.RowIds).Select(e => (seed, e.Index, e.Evaluation)))
            .ToList();

        // Both engines ran every evaluation; a run in which the engine under test was not asked cannot pass.
        Assert.NotEmpty(evaluations);
        Assert.Equal(evaluations.Count, runner.ReferenceCalls);
        Assert.Equal(evaluations.Count, runner.ActualCalls);

        var snapshotPath = Path.Combine(AppContext.BaseDirectory, "Snapshots", DifferenceSnapshot.FileName);
        var snapshot = File.Exists(snapshotPath) ? DifferenceSnapshot.Parse(File.ReadAllText(snapshotPath)) : [];
        var found = DifferenceSnapshot.FromRun(evaluations, snapshot);
        output.WriteLine($"Reference: SQL Server {database.ServerDescription}. Default run: {evaluations.Count} evaluations, {found.Count} differ; the snapshot records {snapshot.Count}.");

        if (Environment.GetEnvironmentVariable(WriteSnapshotVariable) is { Length: > 0 } target)
        {
            File.WriteAllText(target, DifferenceSnapshot.Format(found), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Fail($"Snapshot of {found.Count} rows written to {target}. Review its diff, cite the issue of each change, commit it, and unset {WriteSnapshotVariable}: a run that writes the snapshot never passes.");
        }

        Assert.True(File.Exists(snapshotPath), $"The snapshot {DifferenceSnapshot.FileName} is missing beside the test assembly; see README.md, \"Updating the snapshot\".");
        var (added, changed, gone) = DifferenceSnapshot.Compare(snapshot, found);
        if (added.Count + changed.Count + gone.Count == 0)
            return;

        var message = new StringBuilder();
        message.AppendLine(CultureInfo.InvariantCulture, $"The default run does not match the snapshot: {added.Count} new, {changed.Count} changed, {gone.Count} no longer differing (of {snapshot.Count} recorded).");
        Section(message, "NEW (not in the snapshot)", added.Select(r => $"{r}   first seen: {r.FirstSeen}"));
        Section(message, "CHANGED (either side's outcome)", changed.Select(c => $"row {c.Now.RowId}: {c.Now.Expression}\n      was  SQL Server: {c.Was.SqlServer}   PPDS: {c.Was.Ppds}\n      now  SQL Server: {c.Now.SqlServer}   PPDS: {c.Now.Ppds}"));
        Section(message, "GONE (recorded, no longer differing or no longer generated)", gone.Select(r => r.ToString()));
        message.AppendLine("A change that is right (an engine fix) updates the snapshot: see README.md, \"Updating the snapshot\".");
        output.WriteLine(message.ToString());
        Assert.Fail(message.ToString());
    }

    private static void Section(StringBuilder message, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0)
            return;
        message.AppendLine(CultureInfo.InvariantCulture, $"{title}: {list.Count}");
        foreach (var line in list.Take(RowsListed))
            message.AppendLine("  " + line);
        if (list.Count > RowsListed)
            message.AppendLine(CultureInfo.InvariantCulture, $"  ... and {list.Count - RowsListed} more");
    }

    [ReportRunFact]
    public void Report_of_the_configured_seeds()
    {
        static int Setting(string name, int fallback)
            => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        var count = Setting("PPDS_SQLDIFF_COUNT", DifferenceSnapshot.DefaultRun.Count);
        var depth = Setting("PPDS_SQLDIFF_DEPTH", DifferenceSnapshot.DefaultRun.Depth);
        var seeds = Environment.GetEnvironmentVariable("PPDS_SQLDIFF_SEEDS")!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture));

        foreach (var seed in seeds)
        {
            var runner = new DifferentialRunner(new SqlServerEngine(database.Connection), new PpdsEngine(database.RowValues));
            var report = new RunReport(seed, count, depth);
            foreach (var (index, evaluation) in runner.Run(seed, count, depth, Operands.RowIds))
                report.Add(index, evaluation, runner);
            var text = $"Reference: SQL Server {database.ServerDescription}.{Environment.NewLine}{report}";
            output.WriteLine(text);
            if (Environment.GetEnvironmentVariable("PPDS_SQLDIFF_REPORT") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"sqldiff-seed-{seed}.txt"), text);
            }
        }
    }
}
