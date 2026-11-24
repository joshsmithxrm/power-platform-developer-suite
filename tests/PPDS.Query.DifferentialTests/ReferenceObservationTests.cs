using System.Text;
using PPDS.Query.DifferentialTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// Behaviours the SQL Server documentation leaves silent or ambiguous, settled on the reference server. Each case fails
/// when the server's outcome is not the pinned one. The engine's outcome for a scalar expression is reported beside
/// it, not asserted: a difference there is a finding, and only an issue can make it a known difference.
/// </summary>
[Collection(ReferenceServerCollection.Name)]
[Trait("Category", "SqlDifferential")]
public sealed class ReferenceObservationTests(ReferenceDatabase database, ITestOutputHelper output)
{
    /// <summary>Every observation by id.</summary>
    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var observation in ReferenceObservations.All)
            data.Add(observation.Id);
        return data;
    }

    private (string SqlServer, string? Ppds, string Verdict) Observe(ReferenceObservation observation)
    {
        if (observation.IsBatch)
            return (ReferenceBatch.Run(database.Connection, observation.Sql), null, "SQL Server only (a batch)");
        var reference = new SqlServerEngine(database.Connection).Evaluate(observation.Sql, 1);
        var actual = new PpdsEngine(database.RowValues).Evaluate(observation.Sql, 1);
        var difference = Outcome.Compare(reference, actual);
        return (reference.ToString(), actual.ToString(), difference == Difference.None ? "same" : "different: " + NodeDifference.Describe(reference, actual, difference));
    }

    [ReferenceServerTheory]
    [MemberData(nameof(Ids))]
    public void SQL_Server_behaves_as_recorded(string id)
    {
        var observation = ReferenceObservations.All.Single(o => o.Id == id);
        var (sqlServer, ppds, verdict) = Observe(observation);
        output.WriteLine($"{id}: {observation.Sql}");
        output.WriteLine($"  SQL Server: {sqlServer}");
        output.WriteLine($"  PPDS:       {ppds ?? "(not run)"}   [{verdict}]");

        Assert.True(
            sqlServer == observation.SqlServer,
            $"{id} ({observation.Question}): SQL Server returned [{sqlServer}], recorded [{observation.SqlServer}]. "
            + $"Server: {database.ServerDescription}.");
    }

    [ReferenceServerFact]
    public void Report_of_every_observation_with_the_engine_beside_it()
    {
        var text = new StringBuilder();
        foreach (var observation in ReferenceObservations.All)
        {
            var (sqlServer, ppds, verdict) = Observe(observation);
            text.AppendLine($"{observation.Id}\t{observation.Sql}\tSQL Server: {sqlServer}\tPPDS: {ppds ?? "(not run)"}\t{verdict}");
        }
        output.WriteLine(text.ToString());
        if (Environment.GetEnvironmentVariable("PPDS_SQLDIFF_REPORT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "reference-observations.tsv"), text.ToString());
        }
    }
}
