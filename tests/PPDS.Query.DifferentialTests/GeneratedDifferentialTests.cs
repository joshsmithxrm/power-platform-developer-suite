using System.Globalization;
using PPDS.Query.DifferentialTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// Generated expressions from the rule table, run on SQL Server and through the engine over every operand row. A run
/// passes when every difference is a known one; each new difference is reported with the seed that reproduces it.
/// </summary>
/// <remarks>
/// Settings, from the environment: <c>PPDS_SQLDIFF_SEEDS</c> (comma-separated, default <c>1,2,3</c>),
/// <c>PPDS_SQLDIFF_COUNT</c> (expressions per seed, default 300), <c>PPDS_SQLDIFF_DEPTH</c> (maximum depth, default 3),
/// and <c>PPDS_SQLDIFF_REPORT</c> (a directory: each seed's report is also written there).
/// </remarks>
[Collection(ReferenceServerCollection.Name)]
[Trait("Category", "SqlDifferential")]
public sealed class GeneratedDifferentialTests(ReferenceDatabase database, ITestOutputHelper output)
{
    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>The seeds to run.</summary>
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        var text = Environment.GetEnvironmentVariable("PPDS_SQLDIFF_SEEDS");
        foreach (var seed in string.IsNullOrWhiteSpace(text) ? ["1", "2", "3"] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            data.Add(int.Parse(seed, CultureInfo.InvariantCulture));
        return data;
    }

    [ReferenceServerTheory]
    [MemberData(nameof(Seeds))]
    public void Generated_expressions_agree_with_SQL_Server_or_differ_only_by_known_differences(int seed)
    {
        var count = Setting("PPDS_SQLDIFF_COUNT", 300);
        var depth = Setting("PPDS_SQLDIFF_DEPTH", 3);
        var sql = new SqlServerEngine(database.Connection);
        var runner = new DifferentialRunner(sql, new PpdsEngine(database.RowValues), KnownDifferences.All);

        var report = runner.Run(seed, count, depth, Operands.RowIds);

        var text = $"Reference: SQL Server {database.ServerDescription}; {sql.RoundTrips} statements.{Environment.NewLine}{report}";
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("PPDS_SQLDIFF_REPORT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"sqldiff-seed-{seed}.txt"), text);
        }

        Assert.True(
            report.New == 0,
            $"Seed {seed}: {report.New} of {report.Evaluations} evaluations differ from SQL Server in a way no known difference explains. "
            + $"Each is a possible wrong value. Reproduce with PPDS_SQLDIFF_SEEDS={seed} PPDS_SQLDIFF_COUNT={count} PPDS_SQLDIFF_DEPTH={depth}.{Environment.NewLine}{report}");
    }
}
