using System.Globalization;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// DML against the faithful fake (it evaluates the FetchXML filter, count/top and aliases like Dataverse), asserting the
/// exact rows a statement changes and the exact values it writes. Rows n0..n3: revenue 500/1000/1500/2000,
/// numberofemployees 10..40, description null/d1/null/d3, statecode 0/1/0/1, customertypecode 1/2/1/2, creditlimit
/// 0/5/10/15, parentaccountid null/P/null/P, donotemail yes/no/yes/no, followupby 2024-01-01..04Z, birthday 2024-01-15..18.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlFaithfulRowsTests : IDisposable
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    public DmlFaithfulRowsTests() => DmlTestHarness.FaithfulMode.Value = true;

    public void Dispose() => DmlTestHarness.FaithfulMode.Value = false;

    private static async Task<DmlTestHarness> Run(string sql)
    {
        var h = DmlTestHarness.Create(rows: 4);
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
        return h;
    }

    private static string Values(DmlTestHarness h, string attribute)
        => string.Join("|", h.WrittenEntities.Select(e => e.Attributes.TryGetValue(attribute, out var v) ? Text(v) : "<unset>"));

    private static string Text(object? value) => value switch
    {
        null => "<null>",
        decimal d => d.ToString("G29", CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
        Microsoft.Xrm.Sdk.Money m => m.Value.ToString("G29", CultureInfo.InvariantCulture),
        Microsoft.Xrm.Sdk.OptionSetValue o => o.Value.ToString(CultureInfo.InvariantCulture),
        Microsoft.Xrm.Sdk.EntityReference r => r.Id.ToString(),
        _ => value.ToString()!
    };

    // ---- C-M6 / C-S2: WHERE selects exactly the rows its text says ---------------------------------------
    // SET description = name records which rows were changed.

    [Theory]
    [InlineData("name = 'n1'", "n1")]
    [InlineData("name = 'N1'", "n1")]
    [InlineData("name <> 'n1'", "n0|n2|n3")]
    [InlineData("name != 'n1'", "n0|n2|n3")]
    [InlineData("revenue < 1000", "n0")]
    [InlineData("revenue > 1000", "n2|n3")]
    [InlineData("revenue <= 1000", "n0|n1")]
    [InlineData("revenue >= 1500", "n2|n3")]
    [InlineData("revenue = 1000.0", "n1")]
    [InlineData("revenue = -5", "")]
    [InlineData("name IN ('n0', 'n3', 'zz')", "n0|n3")]
    [InlineData("name NOT IN ('n0', 'n3')", "n1|n2")]
    [InlineData("name LIKE 'n%'", "n0|n1|n2|n3")]
    [InlineData("name NOT LIKE '%1'", "n0|n2|n3")]
    [InlineData("name LIKE '%a_b%'", "")]
    [InlineData("name LIKE 'n_'", "n0|n1|n2|n3")]
    [InlineData("revenue BETWEEN 1000 AND 1500", "n1|n2")]
    [InlineData("revenue NOT BETWEEN 1000 AND 1500", "n0|n3")]
    [InlineData("description IS NULL", "n0|n2")]
    [InlineData("description IS NOT NULL", "n1|n3")]
    [InlineData("description <> 'd1'", "n3")]
    [InlineData("description NOT IN ('d1')", "n3")]
    [InlineData("description NOT LIKE 'd1%'", "n3")]
    [InlineData("name = 'n0' AND (revenue > 5 OR revenue IS NULL)", "n0")]
    [InlineData("account.name = 'n2'", "n2")]
    // a negated condition inside OR: rows the first branch selects stay selected although the negated column is NULL
    [InlineData("name = 'n0' OR description <> 'd1'", "n0|n3")]
    [InlineData("name = 'n2' OR description NOT IN ('d3')", "n1|n2")]
    [InlineData("name = 'n0' OR description NOT LIKE 'd1%'", "n0|n3")]
    [InlineData("(revenue > 1500 OR (name = 'n0' AND description NOT LIKE 'x%'))", "n3")]
    [InlineData("((name = 'n0') OR (description <> 'zz' AND revenue < 1500))", "n0|n1")]
    [InlineData("(name = 'n1' OR (name = 'n2' AND (description IS NULL OR revenue > 5000)))", "n1|n2")]
    // the other column types
    [InlineData("statecode = 0", "n0|n2")]
    [InlineData("statecode NOT IN (1)", "n0|n2")]
    [InlineData("statecode IN (1)", "n1|n3")]
    [InlineData("statuscode <> 1", "n1|n3")]
    [InlineData("customertypecode = 2", "n1|n3")]
    [InlineData("customertypecode <> 2", "n0|n2")]
    [InlineData("creditlimit >= 5", "n1|n2|n3")]
    [InlineData("numberofemployees IN (10, 30)", "n0|n2")]
    [InlineData("accountid = '00000000-0000-0000-0001-000000000002'", "n1")]
    [InlineData("accountid = '{00000000-0000-0000-0001-000000000002}'", "n1")]
    [InlineData("accountid IN ('{00000000-0000-0000-0001-000000000001}', '00000000-0000-0000-0001-000000000004')", "n0|n3")]
    [InlineData("accountid <> '00000000-0000-0000-0001-000000000002'", "n0|n2|n3")]
    [InlineData("parentaccountid IS NULL", "n0|n2")]
    [InlineData("parentaccountid = '11111111-1111-1111-1111-111111111111'", "n1|n3")]
    [InlineData("parentaccountid <> '11111111-1111-1111-1111-111111111111'", "")]
    [InlineData("donotemail = 1", "n0|n2")]
    [InlineData("donotemail <> 1", "n1|n3")]
    [InlineData("followupby < '2024-01-03T00:00:00Z'", "n0|n1")]
    [InlineData("followupby >= '2024-01-03T01:00:00+01:00'", "n2|n3")]
    [InlineData("birthday >= '2024-01-17'", "n2|n3")]
    public async Task Where_ChangesExactlyTheRowsItsTextSelects(string where, string expectedNames)
    {
        var h = await Run($"UPDATE account SET description = name WHERE {where}");

        Assert.Equal(expectedNames, Values(h, "description"));
    }

    [Theory]
    [InlineData("name = 'n1'", 1)]
    [InlineData("revenue >= 1500", 2)]
    [InlineData("name = 'n0' OR description <> 'd1'", 2)]
    [InlineData("name LIKE 'zz%'", 0)]
    public async Task Delete_DeletesExactlyTheRowsItsTextSelects(string where, int expected)
    {
        var h = await Run($"DELETE FROM account WHERE {where}");

        Assert.Equal(expected, h.DeletedIds.Count);
    }

    // ---- C-M6: the shapes the live run proved write exactly these values -------------------------------

    [Theory]
    [InlineData("UPDATE dbo.account SET name = 'x' WHERE name = 'n1'", "name", "x")]
    [InlineData("UPDATE ACCOUNT SET [NAME] = 'x' WHERE [Name] = 'n1'", "name", "x")]
    [InlineData("UPDATE account SET creditlimit = 5 WHERE name = 'n1'", "creditlimit", "5")]
    [InlineData("UPDATE account SET notes = 'x' WHERE name = 'n1'", "notes", "x")]
    [InlineData("UPDATE account SET followupby = '2024-01-01T00:00:00+02:00' WHERE name = 'n1'", "followupby", "2023-12-31T22:00:00Z")]
    [InlineData("UPDATE account SET followupby = NULL WHERE name = 'n1'", "followupby", "<null>")]
    [InlineData("UPDATE account SET birthday = '2024-01-15' WHERE name = 'n1'", "birthday", "2024-01-15T00:00:00")]
    [InlineData("UPDATE account SET customertypecode = 2 WHERE name = 'n0'", "customertypecode", "2")]
    [InlineData("UPDATE account SET statecode = 1, statuscode = 2 WHERE name = 'n0'", "statuscode", "2")]
    [InlineData("UPDATE account SET donotemail = 0 WHERE name = 'n0'", "donotemail", "0")]
    [InlineData("UPDATE account SET parentaccountid = '00000000-0000-0000-0000-000000000001' WHERE name = 'n1'", "parentaccountid", "00000000-0000-0000-0000-000000000001")]
    [InlineData("UPDATE account SET parentaccountid = CAST('00000000-0000-0000-0000-000000000001' AS uniqueidentifier) WHERE name = 'n1'", "parentaccountid", "00000000-0000-0000-0000-000000000001")]
    [InlineData("UPDATE account SET numberofemployees = 7 WHERE name = 'n1'", "numberofemployees", "7")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account WHERE revenue > 0 ORDER BY name DESC", "name", "n3|n2")]
    [InlineData("INSERT INTO account (name, numberofemployees) VALUES ('a', -5)", "numberofemployees", "-5")]
    [InlineData("UPDATE account SET revenue = -2.5 WHERE name = 'n1'", "revenue", "-2.5")]
    [InlineData("UPDATE account SET description = name WHERE name = 'n1'", "description", "n1")]
    [InlineData("UPDATE account SET parentaccountid = CAST('{00000000-0000-0000-0000-00000000000A}' AS uniqueidentifier) WHERE name = 'n1'", "parentaccountid", "00000000-0000-0000-0000-00000000000a")]
    public async Task ProvenShape_WritesExactlyTheseValues(string sql, string attribute, string expected)
    {
        var h = await Run(sql);

        Assert.Equal(expected, Values(h, attribute));
    }

    /// <summary>
    /// Computed values the live run proved before the interim rule; until the typed SQL value engine proves each construct,
    /// they are refused before anything is read or written.
    /// </summary>
    [Theory]
    [InlineData("UPDATE account SET revenue = revenue / 4 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET numberofemployees = CAST(revenue AS int) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET numberofemployees = TRY_CONVERT(int, revenue) WHERE name = 'n2'")]
    [InlineData("UPDATE account SET description = IIF(revenue > 1 AND revenue < 900, 'a', 'b') WHERE name = 'n0' OR name = 'n1'")]
    [InlineData("UPDATE account SET description = CONCAT(name, numberofemployees) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = CAST(numberofemployees AS nvarchar(10)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = CONCAT(name, '#', CONVERT(nvarchar(10), numberofemployees * 2)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = CAST(name AS nvarchar(1)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = SUBSTRING(name, 2, 5) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = TRY_CAST(revenue AS decimal(4, 1)) WHERE revenue IS NOT NULL")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal(10, 2)) / 3 WHERE name = 'n2'")]
    [InlineData("UPDATE account SET numberofemployees = NULLIF(numberofemployees, 20) WHERE name = 'n0' OR name = 'n1'")]
    [InlineData("UPDATE account SET numberofemployees = CASE numberofemployees WHEN 10 THEN 1 WHEN 20 THEN 2 ELSE 0 END WHERE revenue > 0")]
    public async Task ComputedShapeTheLiveRunProved_IsRefusedUnderTheInterimRule(string sql)
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    // ---- B-M2 / B-S10: a value T-SQL cannot produce is an error, and nothing is written ------------------

    [Theory]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal(4, 1)) WHERE revenue IS NOT NULL")]
    [InlineData("UPDATE account SET description = CAST(numberofemployees AS nvarchar(1)) WHERE revenue IS NOT NULL")]
    [InlineData("UPDATE account SET numberofemployees = numberofemployees * 1000000000 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET numberofemployees = numberofemployees / 0 WHERE name = 'n1'")]
    public async Task ValueTsqlWouldRefuse_FailsTheStatement_WithNothingWritten(string sql)
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.NotEmpty(ex.Message);
        h.AssertNothingChanged();
    }
}

/// <summary>Refusals added after the independent review, each before anything is read or written.</summary>
[Trait("Category", "Unit")]
public sealed class DmlRound1RefusalTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    [Theory]
    // A-M3: literals too large for a whole number are a clean refusal (the parser types them as decimal literals)
    [InlineData("UPDATE account SET customertypecode = 99999999999 WHERE name = 'n1'", "is a choice column")]
    [InlineData("UPDATE account SET statecode = 1, statuscode = 3000000000 WHERE name = 'n1'", "is a choice column")]
    [InlineData("UPDATE account SET statecode = 3000000000, statuscode = 2 WHERE name = 'n1'", "is a choice column")]
    [InlineData("UPDATE account SET customertypecode = 3 WHERE name = 'n1'", "is not a choice of this column")]
    // A decimal literal into a whole-number column loses its fraction (review round 2: admission is exact).
    [InlineData("UPDATE account SET numberofemployees = 2.7 WHERE name = 'n1'", "whose fraction would be lost")]
    // A decimal column copied to a whole number loses its fraction too (review round 2), whatever the values are.
    [InlineData("UPDATE account SET numberofemployees = revenue WHERE name = 'n1'", "whose fraction would be lost")]
    [InlineData("DELETE FROM account WHERE numberofemployees = 99999999999", "holds whole numbers")]
    [InlineData("DELETE FROM account WHERE numberofemployees = 2147483648", "holds whole numbers")]
    [InlineData("DELETE FROM account WHERE customertypecode IN (1, 3000000000)", "holds whole numbers")]
    // D-M4: only the table's own id among unique-identifier columns
    [InlineData("DELETE FROM account WHERE stageid = '00000000-0000-0000-0000-000000000001'", "not the table's id")]
    // GUIDs only with dashes (with or without braces), as proven live
    [InlineData("DELETE FROM account WHERE accountid = '00000000000000000000000000000001'", "holds row ids")]
    [InlineData("DELETE FROM account WHERE accountid = '(00000000-0000-0000-0000-000000000001)'", "holds row ids")]
    // D-M3 / B-M3: unverified date kinds in IS NULL and ORDER BY; ORDER BY on choice, lookup and yes/no
    [InlineData("DELETE FROM account WHERE legacydate IS NULL", "has not been verified")]
    [InlineData("DELETE FROM account WHERE tzidate IS NOT NULL", "has not been verified")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY legacydate", "has not been verified")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY customertypecode", "not supported in ORDER BY")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY parentaccountid", "not supported in ORDER BY")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY donotemail", "not supported in ORDER BY")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY revenue", "not supported in ORDER BY")]
    // D-M7, B-M2, D-M2, B-S1 (no change: [ ] still refused), floating-point literals, big whole numbers
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account", "TOP without ORDER BY")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("UPDATE account SET description = CAST(name AS nvarchar) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("UPDATE account SET description = CAST(name AS nvarchar(max)) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("INSERT INTO account (name) VALUES (UPPER('a'))", "literal values only")]
    [InlineData("DELETE FROM account WHERE revenue = 2.5E0", "floating-point literal")]
    [InlineData("UPDATE account SET aging30 = 5 WHERE name = 'n1'", "big whole number")]
    [InlineData("UPDATE account SET name = 'x' WHERE name = 'y' OPTION (MAXDOP 2)", "OPTION")]
    public async Task IsRefusedBeforeAnythingIsReadOrWritten(string sql, string named)
    {
        foreach (var options in new[] { Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true } })
        {
            var h = DmlTestHarness.Create(rows: 4);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Contains(named, ex.Message);
            h.AssertNothingChanged();
            Assert.Empty(h.Reads);
        }
    }

    // ---- A-S2 / C-M4: without column metadata DML is refused, before confirmation, in every mode -------------

    private static SqlQueryService WithMetadata(ICachedMetadataProvider? metadata, Mock<IBulkOperationExecutor> bulk, Mock<IQueryExecutor> executor)
        => new(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object, metadataProvider: metadata)
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development
        };

    public static TheoryData<string, string> NoMetadata => new()
    {
        { "none", "need the table's column metadata" },
        { "throws", "could not be read" },
        { "empty", "was not found, or has no columns" },
    };

    [Theory]
    [MemberData(nameof(NoMetadata))]
    public async Task WithoutColumnMetadata_DmlIsRefused_BeforeConfirmation_AndForADryRun(string metadata, string named)
    {
        var provider = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
        if (metadata == "throws")
            provider.Setup(m => m.GetAttributesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("metadata unavailable"));
        else
            provider.Setup(m => m.GetAttributesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AttributeMetadataDto>());

        foreach (var options in new[] { new DmlSafetyOptions(), Confirmed, new DmlSafetyOptions { IsDryRun = true } })
        {
            var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Strict);
            var executor = new Mock<IQueryExecutor>(MockBehavior.Strict);
            var service = WithMetadata(metadata == "none" ? null : provider.Object, bulk, executor);

            // Unconfirmed, the refusal comes instead of a confirmation request (nothing to confirm can run).
            var ex = await Assert.ThrowsAsync<PpdsException>(() => service.ExecuteAsync(new SqlQueryRequest
            {
                Sql = "UPDATE account SET name = 'y' WHERE name = 'x'",
                DmlSafety = options
            }));

            Assert.Equal(ErrorCodes.Query.DmlMetadataUnavailable, ex.ErrorCode);
            Assert.Contains(named, ex.Message);
            bulk.VerifyNoOtherCalls();
            executor.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task WithoutColumnMetadata_SelectStillWorks()
    {
        var executor = new Mock<IQueryExecutor>(MockBehavior.Loose);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(QueryResult.Empty("account"));
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(QueryResult.Empty("account"));
        var service = WithMetadata(null, new Mock<IBulkOperationExecutor>(MockBehavior.Strict), executor);

        var result = await service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name FROM account" });

        Assert.NotNull(result.Result);
    }

    // ---- A-S3 / D-M8: the bypass-hint gate fails closed ----------------------------------------------------

    private const string Update = "UPDATE account SET name = 'y' WHERE name = 'x'";

    [Fact]
    public async Task BypassPlugins_InAnEnvironmentWithNoSavedTypeOrProtection_CountsAsProduction()
    {
        var h = DmlTestHarness.Create(rows: 1);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com", new EnvironmentConfig { Url = "https://dev.crm.dynamics.com", Label = "DEV" });

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "-- ppds:BYPASS_PLUGINS\n" + Update, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("Production-protected", ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task BypassPlugins_InAnEnvironmentWhoseTypeIsKnownAsDevelopment_IsAllowed()
    {
        var h = DmlTestHarness.Create(rows: 1);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com", new EnvironmentConfig { Url = "https://dev.crm.dynamics.com", Type = EnvironmentType.Development });

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "-- ppds:BYPASS_PLUGINS\n" + Update, DmlSafety = Confirmed });

        Assert.Single(h.Writes);
    }

    [Theory]
    [InlineData(BypassPluginMode.Asynchronous, false)]
    [InlineData(BypassPluginMode.None, false)]
    [InlineData(BypassPluginMode.Synchronous, true)]
    [InlineData(BypassPluginMode.All, true)]
    public async Task BypassPlugins_InProduction_IsAllowedOnlyWhenTheSettingsBypassSynchronousPlugins(BypassPluginMode mode, bool allowed)
    {
        var h = DmlTestHarness.Create(rows: 1, protection: ProtectionLevel.Production);
        h.Service.EnvironmentSafetySettings = new QuerySafetySettings { BypassCustomPlugins = mode };

        var run = () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "-- ppds:BYPASS_PLUGINS\n" + Update, DmlSafety = Confirmed });

        if (allowed)
        {
            await run();
            Assert.Single(h.Writes);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<PpdsException>(run);
            Assert.Contains("Production-protected", ex.Message);
            h.AssertNothingChanged();
        }
    }

    // ---- A-M2 / C-S3: over RPC a confirmation names its environment, and runs only there -------------------

    private static readonly string Identity = new('a', 64);

    private static SqlQueryRequest Rpc(string sql, string? environmentUrl, bool confirmed = true, string? profileIdentity = "default", string? executingAs = "default")
        => RpcMethodHandler.BuildSqlRequest(new QuerySqlRequest
        {
            Sql = sql,
            EnvironmentUrl = environmentUrl,
            ProfileIdentity = profileIdentity == "default" ? Identity : profileIdentity,
            DmlSafety = new DmlSafetyRpcOptions { IsConfirmed = confirmed }
        }, executingAs == "default" ? Identity : executingAs);

    // A round-2 S2: a confirmed request names the profile it was confirmed for and runs only as that profile.
    [Theory]
    [InlineData(null, "must name the profile it was confirmed for")]
    [InlineData("  ", "must name the profile it was confirmed for")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "confirmed for another profile")]
    public async Task Rpc_ConfirmedDml_WithoutOrForAnotherProfile_IsRefused(string? profileIdentity, string named)
    {
        var h = DmlTestHarness.Create(rows: 2);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com", null);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(Rpc(Update, "https://dev.crm.dynamics.com", profileIdentity: profileIdentity)));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains(named, ex.Message);
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    [Fact]
    public async Task Rpc_UnconfirmedDml_WithoutAProfile_StillAsksForConfirmation()
    {
        var h = DmlTestHarness.Create(rows: 2);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com", null);

        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => h.Service.ExecuteAsync(Rpc(Update, "https://dev.crm.dynamics.com", confirmed: false, profileIdentity: null)));

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
        h.AssertNothingChanged();
    }

    [Theory]
    [InlineData(null, "must name the environment it was confirmed for")]
    [InlineData("https://other.crm.dynamics.com", "was confirmed for https://other.crm.dynamics.com")]
    public async Task Rpc_ConfirmedDml_ForAnotherOrUnnamedEnvironment_IsRefused(string? confirmedFor, string named)
    {
        var h = DmlTestHarness.Create(rows: 2);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com", null);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(Rpc(Update, confirmedFor)));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains(named, ex.Message);
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    [Theory]
    [InlineData("https://dev.crm.dynamics.com")]
    [InlineData("https://DEV.crm.dynamics.com/")]
    public async Task Rpc_ConfirmedDml_ForTheEnvironmentItRunsAgainst_Runs(string confirmedFor)
    {
        var h = DmlTestHarness.Create(rows: 2);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com", null);

        await h.Service.ExecuteAsync(Rpc(Update, confirmedFor));

        Assert.Single(h.Writes);
    }

    [Fact]
    public async Task Rpc_ConfirmedSelect_WithoutAnEnvironment_StillRuns()
    {
        var h = DmlTestHarness.Create(rows: 2);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com", null);

        var result = await h.Service.ExecuteAsync(Rpc("SELECT name FROM account", null));

        Assert.Equal(2, result.Result.Count);
    }

    [Fact]
    public void Cli_Request_DoesNotRequireAConfirmedTarget()
    {
        // The CLI runs against the environment it resolved itself; only RPC requests carry a target to check.
        Assert.False(new DmlSafetyOptions { IsConfirmed = true }.RequireConfirmedTarget);
        Assert.True(Rpc(Update, "https://dev.crm.dynamics.com").DmlSafety!.RequireConfirmedTarget);
    }

    // ---- A-S1 / D-M9: a refusal after earlier statements ran reports what they committed -------------------

    [Fact]
    public void RefusalAfterDmlRan_DoesNotSayNothingWasExecuted_AndCarriesWhatWasCommitted()
    {
        var context = new QueryPlanContext(new Mock<IQueryExecutor>().Object, CancellationToken.None);
        context.Statistics.AddDmlOutcome(new DmlStatementOutcome(DmlOperation.Update, "contact", 2, 0, null, "1"));
        var refusal = new PPDS.Dataverse.Query.Execution.DmlSafetyRefusalException(
            PPDS.Dataverse.Query.Execution.QueryErrorCode.DmlBlocked, "This DELETE is not supported. Nothing was executed. Write it differently.");

        var wrapped = SqlQueryService.WrapRefusal(refusal, context);

        Assert.DoesNotContain("Nothing was executed", wrapped.Message);
        Assert.Contains("This statement and the ones after it did not run.", wrapped.Message);
        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(wrapped, "sql", out var rpc));
        var data = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(rpc!.ErrorData, rpc.ErrorData!.GetType())).RootElement;
        Assert.True(data.GetProperty("dmlBlocked").GetBoolean());
        Assert.Contains("#1 UPDATE contact: 2 affected", data.GetProperty("dmlCommitted").GetString());
        Assert.Contains("#1 UPDATE contact: 2 affected", data.GetProperty("message").GetString());
        Assert.DoesNotContain("Nothing was executed", data.GetProperty("message").GetString());
    }

    [Fact]
    public void RefusalBeforeAnyDmlRan_KeepsNothingWasExecuted()
    {
        var context = new QueryPlanContext(new Mock<IQueryExecutor>().Object, CancellationToken.None);
        var refusal = new PPDS.Dataverse.Query.Execution.DmlSafetyRefusalException(
            PPDS.Dataverse.Query.Execution.QueryErrorCode.DmlBlocked, "This DELETE is not supported. Nothing was executed.");

        Assert.Contains("Nothing was executed", SqlQueryService.WrapRefusal(refusal, context).Message);
    }

    // ---- TDS: the read-only replica never selects the rows DML changes ------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = 'y' WHERE name = 'x'")]
    [InlineData("-- a comment first\nDELETE FROM account WHERE name = 'x'")]
    [InlineData("SELECT name FROM account; DELETE FROM account WHERE name = 'x'")]
    public async Task Dml_WithTdsSelected_IsRefused(string sql)
    {
        var h = DmlTestHarness.Create(rows: 2);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = sql,
            UseTdsEndpoint = true,
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.TdsIncompatible, ex.ErrorCode);
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    // ---- C-S8: explain plans DML without executing it ---------------------------------------------------------

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'x'")]
    [InlineData("UPDATE account SET name = 'y' WHERE name = 'x'")]
    [InlineData("INSERT INTO account (name) VALUES ('x')")]
    public async Task Explain_OfDml_NeverReadsOrWrites(string sql)
    {
        var executor = new Mock<IQueryExecutor>(MockBehavior.Strict);
        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Strict);
        var service = WithMetadata(TestMetadata.Provider, bulk, executor);

        var plan = await service.ExplainAsync(sql);

        Assert.NotNull(plan);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }
}
