using PPDS.Query.DifferentialTests.Harness;
using Xunit;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// A fault of the reference server or of the harness stops the run; it is never an outcome to compare. Each case here
/// would otherwise be counted as an error of SQL Server and could compare equal to an engine error.
/// </summary>
[Collection(ReferenceServerCollection.Name)]
[Trait("Category", "SqlDifferential")]
public sealed class HarnessFaultTests(ReferenceDatabase database)
{
    [ReferenceServerFact]
    public void A_timeout_stops_the_run()
    {
        var engine = new SqlServerEngine(database.Connection) { CommandTimeoutSeconds = 1 };
        // Billions of string comparisons: far longer than the one-second timeout.
        var slow = "(SELECT COUNT_BIG(*) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c CROSS JOIN sys.all_objects d "
                   + "WHERE a.name + b.name + c.name + d.name LIKE N'%zzzzzz%')";
        Assert.Throws<HarnessException>(() => engine.Evaluate(slow, 1));
        ReferenceSettings.Verify(database.Connection); // the connection is still usable afterwards
    }

    [ReferenceServerFact]
    public void A_syntax_error_in_the_harness_sql_stops_the_run()
    {
        Assert.Throws<HarnessException>(() => new SqlServerEngine(database.Connection).Evaluate("1 +", 1));
        Assert.Throws<HarnessException>(() => ReferenceBatch.Run(database.Connection, "SELECT 1 +;"));
    }

    [ReferenceServerFact]
    public void A_resource_error_of_severity_17_to_19_stops_the_run()
    {
        // Severity 17 to 19 is the server running out of a resource (errors 701, 1105, 9002), not the statement.
        Assert.Throws<HarnessException>(() => ReferenceBatch.Run(database.Connection, "RAISERROR(N'insufficient resources (test)', 17, 1);"));
        ReferenceSettings.Verify(database.Connection);
    }

    [ReferenceServerFact]
    public void A_missing_operand_row_stops_the_run()
        => Assert.Throws<HarnessException>(() => new SqlServerEngine(database.Connection).Evaluate("1", 99));
}
