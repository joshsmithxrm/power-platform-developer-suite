using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

[Trait("Category", "Unit")]
public class DmlOutcomeTests
{
    private static DmlStatementOutcome S(DmlOperation op, string entity, long ok, long failed, string? err = null)
        => new(op, entity, ok, failed, err);

    [Fact]
    public void From_NoStatements_IsNull()
        => Assert.Null(DmlOutcome.From(Array.Empty<DmlStatementOutcome>()));

    [Fact]
    public void NoFailures_IsSuccess_WithNoMessage()
    {
        var o = DmlOutcome.From(new[] { S(DmlOperation.Insert, "account", 3, 0) })!;

        Assert.False(o.HasFailures);
        Assert.Equal(ExitCodes.Success, o.ExitCode);
        Assert.Null(o.Message);
    }

    [Fact]
    public void AllFailed_IsFailure_ExecutionFailedCode_NamesReason()
    {
        var o = DmlOutcome.From(new[] { S(DmlOperation.Insert, "solution", 0, 1, "CreateMultiple not supported") })!;

        Assert.True(o.IsTotalFailure);
        Assert.Equal(ExitCodes.Failure, o.ExitCode);
        Assert.Equal(ErrorCodes.Query.ExecutionFailed, o.ErrorCode);
        Assert.Contains("all 1 row(s) failed", o.Message);
        Assert.Contains("CreateMultiple not supported", o.Message);
    }

    [Fact]
    public void SomeFailed_IsPartial_PartialFailureCode_WithCounts()
    {
        var o = DmlOutcome.From(new[] { S(DmlOperation.Update, "account", 45, 5, "boom") })!;

        Assert.False(o.IsTotalFailure);
        Assert.Equal(ExitCodes.PartialSuccess, o.ExitCode);
        Assert.Equal(ErrorCodes.Operation.PartialFailure, o.ErrorCode);
        Assert.Contains("45 row(s) affected, 5 row(s) failed", o.Message);
    }

    [Fact]
    public void Script_FailedThenSucceeded_IsPartialAndNamesTheFailingStatement()
    {
        var o = DmlOutcome.From(new[]
        {
            S(DmlOperation.Update, "account", 0, 3, "plugin X threw"),
            S(DmlOperation.Update, "contact", 2, 0),
        })!;

        Assert.True(o.HasFailures);
        Assert.Equal(2, o.AffectedRows);
        Assert.Equal(3, o.FailedRows);
        Assert.Equal(ExitCodes.PartialSuccess, o.ExitCode);
        Assert.Contains("#1 UPDATE account: 0 affected, 3 failed (example error: plugin X threw)", o.Message);
        Assert.DoesNotContain("contact", o.Message);
    }

    [Fact]
    public void Script_TwoFailingStatements_EachReasonStaysWithItsStatement()
    {
        var o = DmlOutcome.From(new[]
        {
            S(DmlOperation.Update, "account", 0, 3, "plugin X threw"),
            S(DmlOperation.Delete, "contact", 0, 2, "privilege error"),
        })!;

        Assert.True(o.IsTotalFailure);
        Assert.Contains("#1 UPDATE account: 0 affected, 3 failed (example error: plugin X threw)", o.Message);
        Assert.Contains("#2 DELETE contact: 0 affected, 2 failed (example error: privilege error)", o.Message);
    }

    [Fact]
    public void Message_RedactsConnectionStrings()
    {
        var o = DmlOutcome.From(new[] { S(DmlOperation.Insert, "account", 0, 1, "failed with ClientSecret=supersecret123;Url=x") })!;

        Assert.DoesNotContain("supersecret123", o.Message);
    }

    [Fact]
    public void Message_CapsListedStatements()
    {
        var statements = Enumerable.Range(1, 8).Select(i => S(DmlOperation.Delete, "e" + i, 0, 1, "x")).ToArray();

        var message = DmlOutcome.From(statements)!.Message!;

        Assert.Contains("#5 DELETE e5", message);
        Assert.DoesNotContain("#6 DELETE", message);
        Assert.Contains("and 3 more", message);
    }
}
