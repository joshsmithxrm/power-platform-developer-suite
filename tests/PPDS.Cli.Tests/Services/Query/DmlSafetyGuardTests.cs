using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Unit tests for <see cref="DmlSafetyGuard"/>.
/// Validates that DELETE/UPDATE without WHERE are blocked and that
/// DML safety options (confirm, dry-run, no-limit, row cap) work correctly.
/// </summary>
[Trait("Category", "PlanUnit")]
public class DmlSafetyGuardTests
{
    private readonly DmlSafetyGuard _guard = new();

    #region DELETE Tests

    [Fact]
    public void Check_DeleteWithoutWhere_IsBlocked()
    {
        var delete = new SqlDeleteStatement(
            new SqlTableRef("account"),
            where: null);

        var result = _guard.Check(delete, new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "DELETE without WHERE should be blocked");
        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
        Assert.Contains("ppds truncate", result.BlockReason);
        Assert.Contains("account", result.BlockReason!);
    }

    [Fact]
    public void Check_DeleteWithWhere_IsNotBlocked()
    {
        var delete = new SqlDeleteStatement(
            new SqlTableRef("account"),
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("statecode"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("1", SqlLiteralType.Number)));

        var result = _guard.Check(delete, new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "DELETE with WHERE should not be blocked");
        Assert.True(result.RequiresConfirmation, "DELETE with WHERE should require confirmation by default");
    }

    [Fact]
    public void Check_DeleteWithWhereAndConfirm_DoesNotRequireConfirmation()
    {
        var delete = new SqlDeleteStatement(
            new SqlTableRef("account"),
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("statecode"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("1", SqlLiteralType.Number)));

        var result = _guard.Check(delete, new DmlSafetyOptions { IsConfirmed = true });

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation, "DELETE with --confirm should not require confirmation");
    }

    #endregion

    #region UPDATE Tests

    [Fact]
    public void Check_UpdateWithoutWhere_IsBlocked()
    {
        var update = new SqlUpdateStatement(
            new SqlTableRef("contact"),
            new[]
            {
                new SqlSetClause("firstname", new SqlLiteralExpression(
                    new SqlLiteral("Test", SqlLiteralType.String)))
            },
            where: null);

        var result = _guard.Check(update, new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "UPDATE without WHERE should be blocked");
        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
        Assert.Contains("UPDATE without WHERE", result.BlockReason);
    }

    [Fact]
    public void Check_UpdateWithWhere_IsNotBlocked()
    {
        var update = new SqlUpdateStatement(
            new SqlTableRef("contact"),
            new[]
            {
                new SqlSetClause("firstname", new SqlLiteralExpression(
                    new SqlLiteral("Test", SqlLiteralType.String)))
            },
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("contactid"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("00000000-0000-0000-0000-000000000001", SqlLiteralType.String)));

        var result = _guard.Check(update, new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "UPDATE with WHERE should not be blocked");
        Assert.True(result.RequiresConfirmation, "UPDATE with WHERE should require confirmation by default");
    }

    #endregion

    #region INSERT Tests

    [Fact]
    public void Check_Insert_IsNeverBlocked()
    {
        var insert = new SqlInsertStatement(
            "account",
            new[] { "name" },
            new[] { new ISqlExpression[] { new SqlLiteralExpression(new SqlLiteral("Test", SqlLiteralType.String)) } },
            sourceQuery: null,
            sourcePosition: 0);

        var result = _guard.Check(insert, new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "INSERT should never be blocked");
    }

    #endregion

    #region SELECT Tests

    [Fact]
    public void Check_Select_IsNeverBlocked()
    {
        var select = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Wildcard() },
            new SqlTableRef("account"));

        var result = _guard.Check(select, new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "SELECT should never be blocked");
        Assert.False(result.RequiresConfirmation, "SELECT should not require confirmation");
    }

    #endregion

    #region DryRun Tests

    [Fact]
    public void Check_DeleteWithWhereAndDryRun_SetsIsDryRun()
    {
        var delete = new SqlDeleteStatement(
            new SqlTableRef("account"),
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("statecode"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("1", SqlLiteralType.Number)));

        var result = _guard.Check(delete, new DmlSafetyOptions { IsDryRun = true });

        Assert.False(result.IsBlocked);
        Assert.True(result.IsDryRun, "--dry-run should set IsDryRun on the result");
    }

    #endregion

    #region NoLimit Tests

    [Fact]
    public void Check_DeleteWithWhereAndNoLimit_SetsRowCapToMaxValue()
    {
        var delete = new SqlDeleteStatement(
            new SqlTableRef("account"),
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("statecode"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("1", SqlLiteralType.Number)));

        var result = _guard.Check(delete, new DmlSafetyOptions { NoLimit = true });

        Assert.False(result.IsBlocked);
        Assert.Equal(int.MaxValue, result.RowCap);
    }

    #endregion

    #region Custom RowCap Tests

    [Fact]
    public void Check_DeleteWithWhereAndCustomRowCap_UsesCustomCap()
    {
        var delete = new SqlDeleteStatement(
            new SqlTableRef("account"),
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("statecode"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("1", SqlLiteralType.Number)));

        var result = _guard.Check(delete, new DmlSafetyOptions { RowCap = 500 });

        Assert.False(result.IsBlocked);
        Assert.Equal(500, result.RowCap);
    }

    [Fact]
    public void Check_DefaultRowCap_Is10000()
    {
        var delete = new SqlDeleteStatement(
            new SqlTableRef("account"),
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("statecode"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("1", SqlLiteralType.Number)));

        var result = _guard.Check(delete, new DmlSafetyOptions());

        Assert.Equal(DmlSafetyGuard.DefaultRowCap, result.RowCap);
        Assert.Equal(10_000, result.RowCap);
    }

    #endregion
}
