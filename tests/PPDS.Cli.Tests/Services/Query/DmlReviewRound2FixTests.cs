using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The second full review of the interim DML rule, end to end through <see cref="SqlQueryService"/>: values read from a
/// column pass the same per-value rule in the statement's pass, before its first write (MF-A); the UPDATE and INSERT ...
/// SELECT row caps at exactly the cap and one over it (MF-D); no refusal message repeats a literal or a GUID from the
/// script (should-fix 1); the guard's own refusals come first (should-fix 2); and compared operands go through the same
/// admission functions as written values (dates, numbers and GUIDs in a WHERE).
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlReviewRound2FixTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static readonly DmlSafetyOptions[] EveryRequest = [Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true }];

    // ---- MF-A: values read from a column ------------------------------------------------------------------------

    public static TheoryData<string> WhiteSpaceEdgedOrEmpty => new()
    {
        "x ", " y", "\tz", "w\n", "\u00A0v", "u\u3000", "   ", "",
    };

    [Theory]
    [MemberData(nameof(WhiteSpaceEdgedOrEmpty))]
    public async Task Update_CopyingAColumnWhoseValueHasWhiteSpaceAtAnEnd_WritesNothing(string value)
    {
        var h = DmlTestHarness.Create(rows: 3, shapeRow: (i, row) => row["name"] = PPDS.Dataverse.Query.QueryValue.Simple(i == 1 ? value : "n" + i));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET description = name WHERE name LIKE '%'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
        Assert.Contains("account.description", ex.Message);
        Assert.Contains("This statement wrote nothing.", ex.Message);
        Assert.Empty(h.Writes);
        Assert.Empty(h.WrittenEntities);
    }

    [Theory]
    [MemberData(nameof(WhiteSpaceEdgedOrEmpty))]
    public async Task InsertSelect_CopyingAColumnWhoseValueHasWhiteSpaceAtAnEnd_WritesNothing(string value)
    {
        var h = DmlTestHarness.Create(rows: 3, shapeRow: (i, row) => row["name"] = PPDS.Dataverse.Query.QueryValue.Simple(i == 2 ? value : "n" + i));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "INSERT INTO account (description) SELECT name FROM account WHERE name LIKE '%'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
        Assert.Empty(h.Writes);
    }

    [Fact]
    public async Task CopyingTextWithWhiteSpaceInside_IsWritten()
    {
        var h = DmlTestHarness.Create(rows: 2, shapeRow: (i, row) => row["name"] = PPDS.Dataverse.Query.QueryValue.Simple("a b" + i));

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE account SET description = name WHERE name LIKE '%'", DmlSafety = Confirmed });

        Assert.Equal(new[] { 2 }, h.Updated);
        Assert.Equal(new[] { "a b0", "a b1" }, h.WrittenEntities.Select(e => (string)e["description"]).OrderBy(v => v));
    }

    [Fact]
    public async Task CopyingADecimalWithMorePlacesThanACurrencyKeeps_WritesNothing()
    {
        var h = DmlTestHarness.Create(rows: 2, shapeRow: (i, row) => row["revenue"] = PPDS.Dataverse.Query.QueryValue.Simple(i == 1 ? 1.23456m : 2m));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET creditlimit = revenue WHERE name LIKE '%'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
        Assert.Empty(h.Writes);
    }

    /// <summary>A decimal column written to a whole number would lose its fraction: refused before anything is read.</summary>
    [Fact]
    public async Task CopyingADecimalColumnToAWholeNumber_IsRefusedBeforeAnythingIsRead()
    {
        var h = DmlTestHarness.Create(rows: 2);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET numberofemployees = revenue WHERE name LIKE '%'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Contains("whose fraction would be lost", ex.Message);
        Assert.Empty(h.Reads);
        Assert.Empty(h.Writes);
    }

    /// <summary>The per-value rule of the pass refuses a fraction for a whole-number column from any source (its backstop).</summary>
    [Fact]
    public void ThePassRule_RefusesAFractionForAWholeNumberColumn()
    {
        var target = PPDS.Tests.Shared.DmlValueAdmissionTable.Targets.Single(t => t.Kind == "whole number").Metadata;

        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, DmlValueAdmission.CheckSentValue(2.5m, 2, target)?.Code);
        Assert.Null(DmlValueAdmission.CheckSentValue(3m, 3, target));
    }

    [Fact]
    public async Task InAScript_ARefusedColumnValue_EndsTheScriptAtThatStatement_AndKeepsWhatEarlierStatementsWrote()
    {
        var h = DmlTestHarness.Create(rows: 2, contactRows: 1, shapeRow: (i, row) => row["name"] = PPDS.Dataverse.Query.QueryValue.Simple(" lead" + i));

        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE contact SET lastname = 'b' WHERE lastname = 'y'; UPDATE account SET description = name WHERE name LIKE '%'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
        Assert.Equal(new[] { ("Update", "contact", 1) }, h.Writes);
        Assert.Contains("This statement wrote nothing.", ex.Message);
        Assert.Contains("#1 UPDATE contact: 1 affected", DmlOutcome.From(ex)!.DescribeCommitted());
    }

    // ---- MF-D: the row cap at its boundary ------------------------------------------------------------------------

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task Update_AtExactlyTheCap_IsWritten(int cap)
    {
        var h = DmlTestHarness.Create(rows: cap);

        await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'x' WHERE name LIKE '%'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = cap }
        });

        Assert.Equal(new[] { cap }, h.Updated);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task Update_OneRowOverTheCap_IsRefused_AndWritesNothing(int cap)
    {
        var h = DmlTestHarness.Create(rows: cap + 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'x' WHERE name LIKE '%'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = cap }
        }));

        Assert.Equal(ErrorCodes.Query.DmlRowCapExceeded, ex.ErrorCode);
        Assert.Empty(h.Writes);
    }

    [Fact]
    public async Task InsertSelect_AtExactlyTheCap_IsWritten_AndOneOver_IsRefused()
    {
        var atCap = DmlTestHarness.Create(rows: 3);
        await atCap.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "INSERT INTO account (description) SELECT name FROM account WHERE name LIKE '%'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 3 }
        });
        Assert.Equal(new[] { 3 }, atCap.Created);

        var over = DmlTestHarness.Create(rows: 4);
        await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => over.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "INSERT INTO account (description) SELECT name FROM account WHERE name LIKE '%'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 3 }
        }));
        Assert.Empty(over.Writes);
    }

    // ---- should-fix 1: no literal or GUID from the script in any message ------------------------------------------

    private const string Marker = "ZqMARKqZ";
    private const string MarkerGuid = "a1b2c3d4-e5f6-4071-8293-a4b5c6d7e8f9";

    /// <summary>A distinctive marker in every refusable position: values, conditions, dates, GUIDs, numbers, LIKE sets.</summary>
    public static TheoryData<string> MarkedStatements => new()
    {
        $"UPDATE account SET name = UPPER('{Marker}') WHERE name = 'n1'",
        $"UPDATE account SET name = '{Marker}' + name WHERE name = 'n1'",
        $"UPDATE account SET name = 'caf\u00E9{Marker}' WHERE name = 'n1'",
        $"UPDATE account SET name = ' {Marker}' WHERE name = 'n1'",
        $"UPDATE account SET numberofemployees = '{Marker}' WHERE name = 'n1'",
        $"UPDATE account SET followupby = '{Marker}' WHERE name = 'n1'",
        $"UPDATE account SET followupby = '2024-01-15 09:30:00Z{Marker}' WHERE name = 'n1'",
        $"UPDATE account SET birthday = '2024-01-15{Marker}' WHERE name = 'n1'",
        $"UPDATE account SET parentaccountid = ' {MarkerGuid}' WHERE name = 'n1'",
        $"UPDATE account SET parentaccountid = '{MarkerGuid}x' WHERE name = 'n1'",
        $"UPDATE account SET parentaccountid = CAST('{MarkerGuid}x' AS uniqueidentifier) WHERE name = 'n1'",
        $"UPDATE account SET customertypecode = 987654321 WHERE name = 'n1'",
        $"UPDATE account SET numberofemployees = 98765432109876543210 WHERE name = 'n1'",
        $"UPDATE account SET revenue = 0.98765432109876543210987654321 WHERE name = 'n1'",
        $"UPDATE account SET name = CAST('{Marker}' AS nvarchar(5)) WHERE name = 'n1'",
        $"DELETE FROM account WHERE name LIKE '[{Marker}]'",
        $"DELETE FROM account WHERE UPPER(name) = '{Marker}'",
        $"DELETE FROM account WHERE NOT (name = '{Marker}')",
        $"DELETE FROM account WHERE name = '{Marker}' + 'x'",
        $"DELETE FROM account WHERE name = 'caf\u00E9{Marker}'",
        $"DELETE FROM account WHERE accountid = ' {MarkerGuid}'",
        $"DELETE FROM account WHERE parentaccountid = '{MarkerGuid}x'",
        $"DELETE FROM account WHERE followupby < '2024-01-15{Marker}'",
        $"DELETE FROM account WHERE numberofemployees = 98765432109876543210",
        $"DELETE FROM account WHERE numberofemployees IN (1, '{Marker}')",
        $"INSERT INTO account (name) VALUES (CONCAT('{Marker}', 'x'))",
        $"INSERT INTO account (name) VALUES ('{Marker} ')",
        $"INSERT INTO account (name) SELECT '{Marker}' FROM account WHERE name = 'n1'",
        $"IF '{Marker}' = 'x' DELETE FROM account WHERE name = 'n1'",
        $"UPDATE account SET name = 'x' WHERE name = 'n1' OPTION (MAXDOP 987654321)",
    };

    [Theory]
    [MemberData(nameof(MarkedStatements))]
    public async Task NoRefusalMessage_RepeatsALiteralOrAGuidFromTheScript(string sql)
    {
        foreach (var options in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 1);

            var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.True(ErrorCodes.Query.DmlRefusalCodes.Contains(ex.ErrorCode), $"{ex.ErrorCode}: {ex.Message}");
            Assert.DoesNotContain(Marker, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MarkerGuid, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("987654321", ex.Message);
            Assert.DoesNotContain("9876543210", ex.Message);
            Assert.Matches(@"line \d+, column \d+|This script changes data|this statement", ex.Message);
            h.AssertNothingChanged();
        }
    }

    // ---- should-fix 2: the guard's own refusals come first -----------------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = ''")]
    [InlineData("DELETE TOP (1) FROM account")]
    [InlineData("UPDATE account SET name = UPPER(name)")]
    [InlineData("DELETE FROM account OPTION (MAXDOP 1)")]
    public async Task WithoutAWhere_TheGuardsOwnRefusal_ComesBeforeTheOthers(string sql)
    {
        foreach (var options in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 1);

            var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
            Assert.Contains("without WHERE is not allowed", ex.Message);
            Assert.Empty(h.Reads);
        }
    }

    // ---- compared operands go through the admission functions ---------------------------------------------------

    [Theory]
    [InlineData("followupby < '2024-01-15T09:30:00Z'", true)]
    [InlineData("followupby < '2024-01-15T09:30:00+01:00'", true)]
    [InlineData("followupby BETWEEN '2024-01-01T00:00:00Z' AND '2024-12-31T00:00:00Z'", true)]
    [InlineData("followupby < '01/02/2024 10:00Z'", false)]
    [InlineData("followupby < 'Jan 15 2024 9:30AM Z'", false)]
    [InlineData("followupby < '2024-01-15 09:30Z'", false)]
    [InlineData("followupby < '2024-01-15T09:30:00.1234567Z'", false)]
    [InlineData("followupby < '2024-01-15T09:30:00'", false)]
    [InlineData("birthday = '2024-01-15'", true)]
    [InlineData("birthday = '15/01/2024'", false)]
    [InlineData("numberofemployees = 5", true)]
    [InlineData("numberofemployees = 2147483648", false)]
    [InlineData("numberofemployees = 5.0", false)]
    [InlineData("revenue > 2.5", true)]
    [InlineData("revenue > 0.000000000000000000000000000015", false)]
    [InlineData("address1_latitude > 0.000000000000000000000000000015", false)]
    [InlineData("donotemail = 1", true)]
    [InlineData("donotemail = 2", false)]
    [InlineData("parentaccountid = '6F9619FF-8B86-D011-B42D-00C04FC964FF'", true)]
    [InlineData("parentaccountid = '6F9619FF8B86D011B42D00C04FC964FF'", false)]
    [InlineData("customertypecode IN (1, 2)", true)]
    [InlineData("customertypecode IN (1, 2147483648)", false)]
    public async Task AComparedOperand_IsAdmittedOnlyInItsExactForm(string condition, bool admitted)
    {
        var h = DmlTestHarness.Create(rows: 1);
        var sql = $"DELETE FROM account WHERE {condition}";

        if (admitted)
        {
            await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
            Assert.NotEmpty(h.Reads);
            return;
        }

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));
        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Empty(h.Reads);
        h.AssertNothingChanged();
    }

    // ---- the admission functions themselves -------------------------------------------------------------------

    [Fact]
    public void EveryTargetKind_HasAnAdmissionFunction_AndAnUnknownKindIsRefused()
    {
        var unknown = new PPDS.Dataverse.Metadata.Models.AttributeMetadataDto { LogicalName = "x", DisplayName = "x", SchemaName = "x", AttributeType = "BigInt" };

        Assert.Equal(DmlTargetKind.Unsupported, DmlValueAdmission.KindOf(unknown));
        Assert.False(DmlValueAdmission.AdmitLiteral(new DmlLiteral(DmlLiteralKind.WholeNumber, "5"), unknown, DmlValueUse.Write).IsAdmitted);
        Assert.All(PPDS.Tests.Shared.DmlValueAdmissionTable.Targets, t => Assert.NotEqual(DmlTargetKind.Unsupported, DmlValueAdmission.KindOf(t.Metadata)));
    }

    [Fact]
    public void TheWhiteSpaceRule_IsOneSwitch_OffUntilTheRowsAreInForce()
        => Assert.False(DmlValueAdmission.WhiteSpaceEdgedTextIsWritten);
}
