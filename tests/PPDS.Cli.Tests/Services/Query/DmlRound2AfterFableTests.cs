using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The second fix round of #1698, after the Fable check of "Stop at that statement": the stopping paths that throw name
/// the statements not run (should-fix 3, 7); a currency column whose places do not come from the column takes whole
/// amounts only (P1); a write to an elastic, virtual or activity table is refused (P2). The serialisation (MF-A) is in
/// <see cref="DmlReviewRound6FixTests"/> and <see cref="ExecutorRequestTextTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlRound2AfterFableTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static readonly DmlSafetyOptions[] EveryRequest = [Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true }];

    private const string ThreeStatements =
        "UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM contact WHERE lastname = 'y'; INSERT INTO account (name) VALUES ('n')";

    // ---- should-fix 3 and 7: the stopping paths that throw --------------------------------------------------------------

    /// <summary>A short bulk result in the middle stops the script, and the outcome names the statement not run (should-fix 7).</summary>
    [Fact]
    public async Task AShortResultInTheMiddle_StopsTheScript_AndNamesTheStatementNotRun()
    {
        var h = DmlTestHarness.Create(rows: 3, contactRows: 2);
        h.ResultOf = (write, rows) => write == 2 ? new BulkOperationResult { SuccessCount = rows - 1 } : null;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed }));

        Assert.Equal(new[] { "Update", "Delete" }, h.Writes.Select(w => w.Operation));
        var outcome = DmlOutcome.From(ex)!;
        Assert.Equal(new[] { false, false, true }, outcome.Statements.Select(s => s.NotRun));
        Assert.True(outcome.Statements[1].Interrupted);
        Assert.EndsWith("Statement 3 was not run because statement 2 was interrupted.", outcome.DescribeCommitted());
    }

    [Fact]
    public async Task AThrownBulkCallInTheMiddle_NamesTheStatementNotRun()
    {
        var h = DmlTestHarness.Create(rows: 3, contactRows: 2);
        h.OnWrite = (entity, _, _) =>
        {
            if (entity == "contact")
                throw new InvalidOperationException("connection lost");
        };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed }));

        Assert.Equal(new[] { "Update" }, h.Writes.Select(w => w.Operation));
        Assert.EndsWith("Statement 3 was not run because statement 2 was interrupted.", DmlOutcome.From(ex)!.DescribeCommitted());
    }

    [Fact]
    public async Task ACancellationAfterTheFirstStatement_NamesTheStatementsNotRun()
    {
        var h = DmlTestHarness.Create(rows: 3, contactRows: 2);
        using var cts = new CancellationTokenSource();
        h.OnWrite = (_, _, _) => cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed }, cts.Token));

        Assert.Single(h.Writes);
        Assert.EndsWith("Statements 2 to 3 were not run because statement 1 was interrupted.", DmlOutcome.From(ex)!.DescribeCommitted());
    }

    /// <summary>A statement over the row cap writes nothing: it is among the statements not run, and the error says why.</summary>
    [Fact]
    public async Task ARowCapRefusalInTheMiddle_NamesItAndTheLaterStatementNotRun()
    {
        var h = DmlTestHarness.Create(rows: 5, contactRows: 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE contact SET lastname = 'b' WHERE lastname = 'y'; DELETE FROM account WHERE name = 'x'; UPDATE contact SET lastname = 'c' WHERE lastname = 'z'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        Assert.Equal(new[] { ("Update", "contact", 1) }, h.Writes);
        var outcome = DmlOutcome.From(ex)!;
        Assert.Equal(new[] { "2", "3" }, outcome.NotRun.Select(s => s.Position));
        Assert.EndsWith("Statements 2 to 3 were not run.", outcome.DescribeCommitted());
    }

    /// <summary>A value refused in the statement's pass (copied text with white space at an end) writes nothing of it.</summary>
    [Fact]
    public async Task AValueRefusedInThePassInTheMiddle_NamesItAndTheLaterStatementNotRun()
    {
        var h = DmlTestHarness.Create(rows: 2, contactRows: 1, shapeRow: (_, row) => row["name"] = QueryValue.Simple(" x"));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE contact SET lastname = 'b' WHERE lastname = 'y'; UPDATE account SET description = name WHERE name = 'x'; DELETE FROM contact WHERE lastname = 'z'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
        Assert.Equal(new[] { ("Update", "contact", 1) }, h.Writes);
        Assert.EndsWith("Statements 2 to 3 were not run.", DmlOutcome.From(ex)!.DescribeCommitted());
    }

    /// <summary>Before any statement ran, nothing is recorded: the error says that nothing was executed.</summary>
    [Fact]
    public async Task AThrowInTheFirstStatement_RecordsNothing()
    {
        var h = DmlTestHarness.Create(rows: 5, contactRows: 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x'; UPDATE contact SET lastname = 'c' WHERE lastname = 'z'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        h.AssertNothingChanged();
        Assert.Null(DmlOutcome.From(ex));
    }

    // ---- P1: a currency column whose places do not come from the column ------------------------------------------------

    [Theory]
    [InlineData("ppds_pricingamount", "organisation's pricing setting")]
    [InlineData("ppds_currencyamount", "currency of each row")]
    [InlineData("ppds_unreadamount", "was not read")]
    public async Task ACurrencyColumnWhosePlacesComeFromElsewhere_TakesWholeAmountsOnly(string column, string named)
    {
        foreach (var sql in new[]
                 {
                     $"UPDATE account SET {column} = 1.5 WHERE name = 'n1'",
                     $"INSERT INTO account (name, {column}) VALUES ('a', 0.01)",
                     $"UPDATE account SET description = 'x' WHERE {column} = 1.5",
                     $"UPDATE account SET description = 'x' WHERE {column} IN (2, 1.5)",
                     $"DELETE FROM account WHERE {column} BETWEEN 1 AND 1.5",
                 })
        {
            foreach (var safety in EveryRequest)
            {
                var h = DmlTestHarness.Create(rows: 1);
                var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = safety }));
                Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
                Assert.Contains(named, ex.Message);
                Assert.Contains("Write a whole amount", ex.Message);
                Assert.Empty(h.Reads);
                h.AssertNothingChanged();
            }
        }

        var whole = DmlTestHarness.Create(rows: 1);
        await whole.Service.ExecuteAsync(new SqlQueryRequest { Sql = $"UPDATE account SET {column} = 2 WHERE {column} IN (3, 4)", DmlSafety = Confirmed });
        Assert.Equal(2m, Assert.IsType<Microsoft.Xrm.Sdk.Money>(Assert.Single(whole.WrittenEntities)[column]).Value);
    }

    /// <summary>A copied amount with places is refused in the statement's pass for such a column; a whole one is written.</summary>
    [Theory]
    [InlineData(1.5, false)]
    [InlineData(2, true)]
    public async Task ACopiedAmount_IntoACurrencyColumnWhosePlacesComeFromTheCurrency_MustBeWhole(double amount, bool written)
    {
        var h = DmlTestHarness.Create(rows: 1, shapeRow: (_, row) => row["creditlimit"] = QueryValue.Simple(new Microsoft.Xrm.Sdk.Money((decimal)amount)));
        var request = new SqlQueryRequest { Sql = "UPDATE account SET ppds_currencyamount = creditlimit WHERE name = 'n1'", DmlSafety = Confirmed };

        if (written)
        {
            await h.Service.ExecuteAsync(request);
            Assert.Single(h.WrittenEntities);
            return;
        }

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(request));
        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Contains("currency of each row", ex.Message);
        h.AssertNothingChanged();
    }

    // ---- P2: tables that are not standard tables -----------------------------------------------------------------------

    [Theory]
    [InlineData("ppds_elastic", "an elastic table")]
    [InlineData("ppds_virtual", "a virtual table")]
    [InlineData("ppds_provided", "a virtual table")]
    [InlineData("task", "an activity table")]
    public async Task AWriteToATableThatIsNotStandard_IsRefusedBeforeAnythingIsRead(string table, string kind)
    {
        var name = table == "task" ? "subject" : "ppds_name";
        foreach (var sql in new[]
                 {
                     $"UPDATE {table} SET {name} = 'b' WHERE {name} = 'a'",
                     $"DELETE FROM {table} WHERE {name} = 'a'",
                     $"INSERT INTO {table} ({name}) VALUES ('a')",
                 })
        {
            foreach (var safety in EveryRequest)
            {
                var h = DmlTestHarness.Create(rows: 1);
                var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = safety }));
                Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
                Assert.Contains($"'{table}' is {kind}", ex.Message);
                Assert.Empty(h.Reads);
                h.AssertNothingChanged();
            }
        }
    }

    [Fact]
    public async Task AWriteToATableWhoseKindWasNotRead_IsRefusedAsMetadataUnavailable()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM ppds_untyped WHERE ppds_name = 'a'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlMetadataUnavailable, ex.ErrorCode);
        Assert.Empty(h.Reads);
    }
}
