using PPDS.Query.DifferentialTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// The negative control of #1798: each known wrong case of #1764 and #1851 runs on SQL Server and through the engine,
/// and must still differ exactly as recorded. This is the proof that the harness sees the defects it exists to catch.
/// </summary>
[Collection(ReferenceServerCollection.Name)]
[Trait("Category", "SqlDifferential")]
public sealed class NegativeControlTests(ReferenceDatabase database, ITestOutputHelper output)
{
    /// <summary>Every case of every known difference, by entry id and case index.</summary>
    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var entry in KnownDifferences.All)
        {
            for (var i = 0; i < entry.Cases.Count; i++)
                data.Add(entry.Id, i);
        }
        return data;
    }

    [ReferenceServerTheory]
    [MemberData(nameof(Cases))]
    public void Known_case_still_differs_from_SQL_Server_exactly_as_recorded(string id, int index)
    {
        var entry = KnownDifferences.All.Single(e => e.Id == id);
        var known = entry.Cases[index];
        var reference = new SqlServerEngine(database.Connection).Evaluate(known.Expression, known.RowId);
        var actual = new PpdsEngine(database.RowValues).Evaluate(known.Expression, known.RowId);
        output.WriteLine($"{entry}: {known.Expression} (row {known.RowId})");
        output.WriteLine($"  SQL Server: {reference}");
        output.WriteLine($"  PPDS:       {actual}");

        Assert.True(
            reference.ToString() == known.SqlServer,
            $"{entry}: SQL Server returned [{reference}] for {known.Expression}, recorded [{known.SqlServer}]. "
            + $"The recorded reference result is wrong, or the server ({database.ServerDescription}) or its settings differ from the reference.");

        Assert.False(
            Outcome.Compare(reference, actual) == Difference.None,
            $"{entry}: {known.Expression} no longer differs from SQL Server (both [{reference}]). "
            + "If the defect is fixed, remove this case from KnownDifferences, and the entry when it has no case left.");

        Assert.True(
            actual.ToString() == known.PpdsToday,
            $"{entry}: the engine's wrong result changed for {known.Expression}: recorded [{known.PpdsToday}], now [{actual}], "
            + $"SQL Server [{reference}]. It still differs; find out why before changing the pinned result.");
    }
}
