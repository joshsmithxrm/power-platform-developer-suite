using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Cli.Tui.Screens;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>Per-statement positions, zero-row statements and interrupted scripts.</summary>
[Trait("Category", "Unit")]
public class DmlScriptReportingTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static (SqlQueryService service, Mock<IBulkOperationExecutor> bulk) Create(int rows = 2)
    {
        var executor = new Mock<IQueryExecutor>(MockBehavior.Loose);
        var records = Enumerable.Range(0, rows).Select(_ => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
        {
            ["accountid"] = QueryValue.Simple(Guid.NewGuid()),
            ["contactid"] = QueryValue.Simple(Guid.NewGuid()),
        }).ToArray();
        var fetched = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[] { new QueryColumn { LogicalName = "accountid" }, new QueryColumn { LogicalName = "contactid" } },
            Records = records,
            Count = rows
        };
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Loose);
        var service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object)
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development
        };
        return (service, bulk);
    }

    private static void SetupUpdate(Mock<IBulkOperationExecutor> bulk, string entity, Func<int, BulkOperationResult> result)
        => bulk.Setup(b => b.UpdateMultipleAsync(entity, It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Microsoft.Xrm.Sdk.Entity> e, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
                Task.FromResult(result(e.Count())));

    [Fact]
    public async Task Script_FailureMessageUsesTheStatementsRealPosition_AndNamesOperationAndEntity()
    {
        var (service, bulk) = Create();
        SetupUpdate(bulk, "account", n => new BulkOperationResult { SuccessCount = n });
        SetupUpdate(bulk, "contact", n => new BulkOperationResult
        {
            FailureCount = n,
            Errors = new[] { new BulkOperationError { Message = "contact privilege error" } }
        });

        var result = await service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "SELECT name FROM account; UPDATE account SET name = 'a' WHERE name = 'x'; UPDATE contact SET lastname = 'b' WHERE lastname = 'y'",
            DmlSafety = Confirmed
        });

        var message = result.DmlOutcome!.Message!;
        Assert.Contains("#3 UPDATE contact: 0 affected, 2 failed (example error: contact privilege error)", message);
        Assert.DoesNotContain("#2", message);
    }

    [Fact]
    public async Task Script_StatementThatMatchedNoRows_StillCountsSoPositionsStayReal()
    {
        var (service, bulk) = Create(rows: 0);

        var result = await service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'a' WHERE name = 'x'; UPDATE contact SET lastname = 'b' WHERE lastname = 'y'",
            DmlSafety = Confirmed
        });

        var outcome = result.DmlOutcome!;
        Assert.Equal(new[] { "1", "2" }, outcome.Statements.Select(s => s.Position));
        Assert.False(outcome.HasFailures);
        Assert.Equal(0, outcome.AffectedRows);
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SingleStatement_FailureNamesOperationAndEntity_WithoutAnOrdinal()
    {
        var (service, bulk) = Create();
        SetupUpdate(bulk, "account", n => new BulkOperationResult
        {
            FailureCount = n,
            Errors = new[] { new BulkOperationError { Message = "boom" } }
        });

        var result = await service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'a' WHERE name = 'x'",
            DmlSafety = Confirmed
        });

        Assert.Equal("DML failed (UPDATE account): all 2 row(s) failed. Example error: boom", result.DmlOutcome!.Message);
    }

    [Fact]
    public async Task Script_InterruptedAfterAStatementRan_AttachesTheCommittedOutcomeToTheException()
    {
        var (service, bulk) = Create();
        SetupUpdate(bulk, "account", n => new BulkOperationResult { SuccessCount = n });
        bulk.Setup(b => b.UpdateMultipleAsync("contact", It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection lost"));

        // Wrapped in a PpdsException with a code (Constitution D4), carrying what already ran.
        var ex = await Assert.ThrowsAsync<PPDS.Cli.Infrastructure.Errors.PpdsException>(() => service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'a' WHERE name = 'x'; UPDATE contact SET lastname = 'b' WHERE lastname = 'y'",
            DmlSafety = Confirmed
        }));

        var committed = DmlOutcome.From(ex);
        Assert.NotNull(committed);
        Assert.Equal(2, committed!.AffectedRows);
        Assert.Contains("#1 UPDATE account: 2 affected, 0 failed", committed.DescribeCommitted());
        Assert.Contains("not rolled back", committed.DescribeCommitted());
    }

    [Fact]
    public async Task Script_CancelledAfterAStatementRan_AttachesTheCommittedOutcome()
    {
        var (service, bulk) = Create();
        using var cts = new CancellationTokenSource();
        SetupUpdate(bulk, "account", n =>
        {
            cts.Cancel(); // cancel between statements
            return new BulkOperationResult { SuccessCount = n };
        });

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'a' WHERE name = 'x'; UPDATE contact SET lastname = 'b' WHERE lastname = 'y'",
            DmlSafety = Confirmed
        }, cts.Token));

        Assert.Equal(2, DmlOutcome.From(ex)!.AffectedRows);
    }

    [Fact]
    public async Task ExceptionWithoutDml_HasNoAttachedOutcome()
    {
        var (service, _) = Create();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "SELECT 1/0",
            DmlSafety = Confirmed
        }));

        Assert.Null(DmlOutcome.From(ex));
    }

    // ---- TUI status line (N4) ---------------------------------------------

    [Fact]
    public void TuiStatus_LongMultiLineMessage_IsCollapsedToOneLine()
    {
        var outcome = DmlOutcome.From(new[]
        {
            new DmlStatementOutcome(DmlOperation.Delete, "account", 0, 1, "line one\nline two\n" + new string('x', 400))
        });

        var status = SqlQueryPresenter.FormatCompletionStatus(1, 5, " via Dataverse", outcome);

        Assert.DoesNotContain("\n", status);
        Assert.True(status.Length < 220, status.Length.ToString());
        Assert.Contains("...", status);
    }
}

[Trait("Category", "Unit")]
public class TerminalTextTests
{
    [Fact]
    public void Printable_Text_IsUnchanged()
        => Assert.Equal("SELECT name FROM account WHERE x = 'a\tb'\nAND y = 1", TerminalText.Sanitize("SELECT name FROM account WHERE x = 'a\tb'\nAND y = 1"));

    [Fact]
    public void EscapeSequence_IsMadeVisible()
        => Assert.Equal("a\\u001B[2Kb", TerminalText.Sanitize("a" + (char)0x1B + "[2Kb"));

    [Fact]
    public void LoneCarriageReturn_IsEscaped_ButCrLfBecomesLf()
    {
        Assert.Equal("a\\u000Db", TerminalText.Sanitize("a\rb"));
        Assert.Equal("a\nb", TerminalText.Sanitize("a\r\nb"));
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x7F)]
    [InlineData(0x85)]
    [InlineData(0x202E)]
    [InlineData(0x2028)]
    public void OtherControlAndBidiCharacters_AreEscaped(int code)
    {
        var result = TerminalText.Sanitize("a" + (char)code + "b");

        Assert.Equal("a\\u" + code.ToString("X4") + "b", result);
    }

    [Fact]
    public void NullAndEmpty_AreSafe()
    {
        Assert.Equal(string.Empty, TerminalText.Sanitize(null));
        Assert.Equal(string.Empty, TerminalText.Sanitize(string.Empty));
    }
}
