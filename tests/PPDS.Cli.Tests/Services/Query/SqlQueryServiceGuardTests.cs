using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Guard-wiring regression test for <see cref="SqlQueryService"/> DML branch —
/// asserts that the shakedown guard is invoked ONLY when DML will actually
/// execute (not dry-run, not SELECT). Covers AC-38.
/// </summary>
/// <remarks>
/// Placement contract (see Phase C.5 of the shakedown-guard plan):
/// the guard call lives in <see cref="SqlQueryService.ExecuteAsync"/> AFTER
/// <c>PrepareExecutionAsync</c> returns and BEFORE
/// <c>_planExecutor.ExecuteAsync</c>, gated on
/// <c>safetyResult != null &amp;&amp; !safetyResult.IsDryRun</c>.
///
/// The test uses a <see cref="RecordingShakedownGuard"/> test double to
/// observe whether <c>EnsureCanMutate</c> was invoked. The plan-executor
/// itself is not stubbed out — when the guard is called, the recording
/// guard throws to halt execution and preserve the test's granularity.
/// </remarks>
[Trait("Category", "Unit")]
public class SqlQueryServiceGuardTests
{
    [Theory]
    [InlineData("SELECT name FROM account", false, false, false)]
    [InlineData("DELETE FROM account WHERE name = 'x'", true, false, true)]
    [InlineData("DELETE FROM account WHERE name = 'x'", true, true, false)]
    public async Task DmlBranch_Blocks_AndSelectBranch_DoesNot(
        string sql,
        bool useDmlSafety,
        bool isDryRun,
        bool expectGuardCalled)
    {
        // Arrange
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult
            {
                EntityLogicalName = "account",
                Columns = new List<QueryColumn>(),
                Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Count = 0
            });

        var guard = new RecordingShakedownGuard();
        var service = new SqlQueryService(
            mockExecutor.Object,
            tdsQueryExecutor: null,
            bulkOperationExecutor: null,
            metadataQueryExecutor: null,
            poolCapacity: 1,
            metadataProvider: null,
            guard: guard)
        {
            // Use Development so confirmed DML proceeds past RequiresConfirmation
            // without tripping the Production-level confirmation gate.
            EnvironmentProtectionLevel = PPDS.Auth.Profiles.ProtectionLevel.Development
        };

        var request = new SqlQueryRequest
        {
            Sql = sql,
            DmlSafety = useDmlSafety
                ? new DmlSafetyOptions { IsConfirmed = true, IsDryRun = isDryRun }
                : null
        };

        // Act — swallow downstream exceptions; we only care about whether the
        // guard was invoked before anything else executed.
        try
        {
            await service.ExecuteAsync(request, CancellationToken.None);
        }
        catch
        {
            // Expected in some rows: plan execution may fail without a bulk executor,
            // or the guard itself may throw. Either way the recording flag is authoritative.
        }

        // Assert
        Assert.Equal(expectGuardCalled, guard.WasCalled);
        if (expectGuardCalled)
        {
            Assert.Equal("query.dml", guard.LastOperation);
        }
    }

    /// <summary>
    /// Test double for <see cref="IShakedownGuard"/> that records whether
    /// <see cref="EnsureCanMutate"/> was invoked (and with what operation
    /// descriptor). Throws <see cref="PpdsException"/> when called so the
    /// remaining plan-executor path does not also run under test.
    /// </summary>
    private sealed class RecordingShakedownGuard : IShakedownGuard
    {
        public bool WasCalled { get; private set; }
        public string? LastOperation { get; private set; }

        public void EnsureCanMutate(string operationDescription)
        {
            WasCalled = true;
            LastOperation = operationDescription;
            throw new PpdsException(
                ErrorCodes.Safety.ShakedownActive,
                "Recording guard — halting so the plan executor does not run.");
        }
    }
}
