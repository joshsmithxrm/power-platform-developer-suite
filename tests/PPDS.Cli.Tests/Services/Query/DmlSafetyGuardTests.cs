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
[Trait("Category", "TuiUnit")]
public class DmlSafetyGuardTests
{
    private readonly DmlSafetyGuard _guard = new();

    // ── Helper methods ──────────────────────────────────────────────

    private static SqlDeleteStatement DeleteWithoutWhere(string table = "account") =>
        new(new SqlTableRef(table), where: null);

    private static SqlDeleteStatement DeleteWithWhere(string table = "account") =>
        new(new SqlTableRef(table),
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("statecode"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("1", SqlLiteralType.Number)));

    private static SqlUpdateStatement UpdateWithoutWhere(string table = "contact") =>
        new(new SqlTableRef(table),
            new[]
            {
                new SqlSetClause("firstname",
                    new SqlLiteralExpression(new SqlLiteral("Test", SqlLiteralType.String)))
            },
            where: null);

    private static SqlUpdateStatement UpdateWithWhere(string table = "contact") =>
        new(new SqlTableRef(table),
            new[]
            {
                new SqlSetClause("firstname",
                    new SqlLiteralExpression(new SqlLiteral("Test", SqlLiteralType.String)))
            },
            where: new SqlComparisonCondition(
                SqlColumnRef.Simple("contactid"),
                SqlComparisonOperator.Equal,
                new SqlLiteral("00000000-0000-0000-0000-000000000001", SqlLiteralType.String)));

    private static SqlInsertStatement SimpleInsert() =>
        new("account",
            new[] { "name" },
            new[] { new ISqlExpression[] { new SqlLiteralExpression(new SqlLiteral("Test", SqlLiteralType.String)) } },
            sourceQuery: null,
            sourcePosition: 0);

    private static SqlSelectStatement SimpleSelect() =>
        new(new ISqlSelectColumn[] { SqlColumnRef.Wildcard() },
            new SqlTableRef("account"));

    // ── DELETE Safety ────────────────────────────────────────────────

    #region DELETE Tests

    [Fact]
    public void Check_DeleteWithoutWhere_IsBlocked()
    {
        var result = _guard.Check(DeleteWithoutWhere(), new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "DELETE without WHERE should be blocked");
        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
    }

    [Fact]
    public void Check_DeleteWithoutWhere_BlockReasonMentionsPpdsTruncate()
    {
        var result = _guard.Check(DeleteWithoutWhere("account"), new DmlSafetyOptions());

        Assert.Contains("ppds truncate", result.BlockReason);
    }

    [Fact]
    public void Check_DeleteWithoutWhere_BlockReasonContainsTableName()
    {
        var result = _guard.Check(DeleteWithoutWhere("lead"), new DmlSafetyOptions());

        Assert.Contains("lead", result.BlockReason!);
    }

    [Fact]
    public void Check_DeleteWithWhere_IsNotBlocked()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "DELETE with WHERE should not be blocked");
    }

    [Fact]
    public void Check_DeleteWithWhere_RequiresConfirmationByDefault()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "DELETE with WHERE should require confirmation by default");
    }

    [Fact]
    public void Check_DeleteWithWhereAndConfirm_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions { IsConfirmed = true });

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation, "DELETE with --confirm should not require confirmation");
    }

    [Fact]
    public void Check_DeleteWithWhereAndDryRun_SetsIsDryRun()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions { IsDryRun = true });

        Assert.False(result.IsBlocked);
        Assert.True(result.IsDryRun, "--dry-run should set IsDryRun on the result");
    }

    [Fact]
    public void Check_DeleteWithoutWhere_IsBlockedEvenWithConfirm()
    {
        var result = _guard.Check(DeleteWithoutWhere(), new DmlSafetyOptions { IsConfirmed = true });

        Assert.True(result.IsBlocked, "DELETE without WHERE should be blocked even with --confirm");
    }

    [Fact]
    public void Check_DeleteWithoutWhere_IsBlockedEvenWithNoLimit()
    {
        var result = _guard.Check(DeleteWithoutWhere(), new DmlSafetyOptions { NoLimit = true });

        Assert.True(result.IsBlocked, "DELETE without WHERE should be blocked even with --no-limit");
    }

    #endregion

    // ── UPDATE Safety ────────────────────────────────────────────────

    #region UPDATE Tests

    [Fact]
    public void Check_UpdateWithoutWhere_IsBlocked()
    {
        var result = _guard.Check(UpdateWithoutWhere(), new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "UPDATE without WHERE should be blocked");
        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
    }

    [Fact]
    public void Check_UpdateWithoutWhere_BlockReasonMentionsUpdateWithoutWhere()
    {
        var result = _guard.Check(UpdateWithoutWhere(), new DmlSafetyOptions());

        Assert.Contains("UPDATE without WHERE", result.BlockReason);
    }

    [Fact]
    public void Check_UpdateWithWhere_IsNotBlocked()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "UPDATE with WHERE should not be blocked");
    }

    [Fact]
    public void Check_UpdateWithWhere_RequiresConfirmationByDefault()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "UPDATE with WHERE should require confirmation by default");
    }

    [Fact]
    public void Check_UpdateWithWhereAndConfirm_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions { IsConfirmed = true });

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation, "UPDATE with --confirm should not require confirmation");
    }

    [Fact]
    public void Check_UpdateWithWhereAndDryRun_SetsIsDryRun()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions { IsDryRun = true });

        Assert.False(result.IsBlocked);
        Assert.True(result.IsDryRun, "UPDATE --dry-run should set IsDryRun on the result");
    }

    [Fact]
    public void Check_UpdateWithoutWhere_IsBlockedEvenWithConfirm()
    {
        var result = _guard.Check(UpdateWithoutWhere(), new DmlSafetyOptions { IsConfirmed = true });

        Assert.True(result.IsBlocked, "UPDATE without WHERE should be blocked even with --confirm");
    }

    [Fact]
    public void Check_UpdateWithoutWhere_IsBlockedEvenWithNoLimit()
    {
        var result = _guard.Check(UpdateWithoutWhere(), new DmlSafetyOptions { NoLimit = true });

        Assert.True(result.IsBlocked, "UPDATE without WHERE should be blocked even with --no-limit");
    }

    #endregion

    // ── INSERT Safety ────────────────────────────────────────────────

    #region INSERT Tests

    [Fact]
    public void Check_Insert_IsNeverBlocked()
    {
        var result = _guard.Check(SimpleInsert(), new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "INSERT should never be blocked");
    }

    [Fact]
    public void Check_Insert_RequiresConfirmationByDefault()
    {
        var result = _guard.Check(SimpleInsert(), new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "INSERT should require confirmation by default");
    }

    [Fact]
    public void Check_InsertWithConfirm_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(SimpleInsert(), new DmlSafetyOptions { IsConfirmed = true });

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation, "INSERT with --confirm should not require confirmation");
    }

    [Fact]
    public void Check_Insert_HasUnknownEstimatedRows()
    {
        var result = _guard.Check(SimpleInsert(), new DmlSafetyOptions());

        Assert.Equal(-1, result.EstimatedAffectedRows);
    }

    #endregion

    // ── SELECT Pass-through ──────────────────────────────────────────

    #region SELECT Tests

    [Fact]
    public void Check_Select_IsNeverBlocked()
    {
        var result = _guard.Check(SimpleSelect(), new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "SELECT should never be blocked");
    }

    [Fact]
    public void Check_Select_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(SimpleSelect(), new DmlSafetyOptions());

        Assert.False(result.RequiresConfirmation, "SELECT should not require confirmation");
    }

    [Fact]
    public void Check_Select_IsDryRunFalse()
    {
        var result = _guard.Check(SimpleSelect(), new DmlSafetyOptions { IsDryRun = true });

        // SELECT pass-through does not propagate dry-run - it has no effect on reads
        Assert.False(result.IsDryRun, "SELECT should not be affected by --dry-run");
    }

    [Fact]
    public void Check_Select_HasNoErrorCode()
    {
        var result = _guard.Check(SimpleSelect(), new DmlSafetyOptions());

        Assert.Null(result.ErrorCode);
        Assert.Null(result.BlockReason);
    }

    #endregion

    // ── Row Cap ──────────────────────────────────────────────────────

    #region Row Cap Tests

    [Fact]
    public void DefaultRowCap_Is10000()
    {
        Assert.Equal(10_000, DmlSafetyGuard.DefaultRowCap);
    }

    [Fact]
    public void Check_DefaultOptions_UsesDefaultRowCap()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions());

        Assert.Equal(DmlSafetyGuard.DefaultRowCap, result.RowCap);
        Assert.Equal(10_000, result.RowCap);
    }

    [Fact]
    public void Check_NoLimit_SetsRowCapToMaxValue()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions { NoLimit = true });

        Assert.Equal(int.MaxValue, result.RowCap);
    }

    [Fact]
    public void Check_CustomRowCap_IsHonored()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions { RowCap = 500 });

        Assert.Equal(500, result.RowCap);
    }

    [Fact]
    public void Check_NoLimitOverridesCustomRowCap()
    {
        // When NoLimit is true, the custom RowCap should be ignored
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions { NoLimit = true, RowCap = 500 });

        Assert.Equal(int.MaxValue, result.RowCap);
    }

    [Fact]
    public void Check_UpdateWithWhere_DefaultRowCap()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions());

        Assert.Equal(DmlSafetyGuard.DefaultRowCap, result.RowCap);
    }

    [Fact]
    public void Check_UpdateWithWhereAndNoLimit_SetsRowCapToMaxValue()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions { NoLimit = true });

        Assert.Equal(int.MaxValue, result.RowCap);
    }

    [Fact]
    public void Check_UpdateWithWhereAndCustomRowCap_IsHonored()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions { RowCap = 250 });

        Assert.Equal(250, result.RowCap);
    }

    #endregion

    // ── Error Codes ──────────────────────────────────────────────────

    #region Error Code Tests

    [Fact]
    public void Check_BlockedDelete_UsesDmlBlockedErrorCode()
    {
        var result = _guard.Check(DeleteWithoutWhere(), new DmlSafetyOptions());

        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
        Assert.Equal("Query.DmlBlocked", result.ErrorCode);
    }

    [Fact]
    public void Check_BlockedUpdate_UsesDmlBlockedErrorCode()
    {
        var result = _guard.Check(UpdateWithoutWhere(), new DmlSafetyOptions());

        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
        Assert.Equal("Query.DmlBlocked", result.ErrorCode);
    }

    [Fact]
    public void Check_AllowedDelete_HasNoErrorCode()
    {
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions());

        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Check_AllowedUpdate_HasNoErrorCode()
    {
        var result = _guard.Check(UpdateWithWhere(), new DmlSafetyOptions());

        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Check_Insert_HasNoErrorCode()
    {
        var result = _guard.Check(SimpleInsert(), new DmlSafetyOptions());

        Assert.Null(result.ErrorCode);
    }

    #endregion

    // ── Script DML Detection ──────────────────────────────────────────

    #region Script DML Detection Tests

    [Fact]
    public void Check_IfStatement_ContainingDelete_RequiresConfirmation()
    {
        var delete = DeleteWithWhere();
        var block = new SqlBlockStatement(new ISqlStatement[] { delete }, 0);
        var ifStmt = new SqlIfStatement(
            new SqlComparisonCondition(SqlColumnRef.Simple("x"), SqlComparisonOperator.Equal, new SqlLiteral("1", SqlLiteralType.Number)),
            block, elseBlock: null, sourcePosition: 0);

        var result = _guard.Check(ifStmt, new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "DML inside IF should require confirmation");
    }

    [Fact]
    public void Check_IfStatement_ContainingDeleteWithoutWhere_IsBlocked()
    {
        var delete = DeleteWithoutWhere();
        var block = new SqlBlockStatement(new ISqlStatement[] { delete }, 0);
        var ifStmt = new SqlIfStatement(
            new SqlComparisonCondition(SqlColumnRef.Simple("x"), SqlComparisonOperator.Equal, new SqlLiteral("1", SqlLiteralType.Number)),
            block, elseBlock: null, sourcePosition: 0);

        var result = _guard.Check(ifStmt, new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "DELETE without WHERE inside IF should be blocked");
    }

    [Fact]
    public void Check_BlockStatement_ContainingUpdate_RequiresConfirmation()
    {
        var update = UpdateWithWhere();
        var block = new SqlBlockStatement(new ISqlStatement[] { update }, 0);

        var result = _guard.Check(block, new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "DML inside block should require confirmation");
    }

    [Fact]
    public void Check_BlockStatement_SelectOnly_NotBlocked()
    {
        var select = SimpleSelect();
        var block = new SqlBlockStatement(new ISqlStatement[] { select }, 0);

        var result = _guard.Check(block, new DmlSafetyOptions());

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void Check_IfElse_DmlInElse_IsDetected()
    {
        var selectBlock = new SqlBlockStatement(new ISqlStatement[] { SimpleSelect() }, 0);
        var deleteBlock = new SqlBlockStatement(new ISqlStatement[] { DeleteWithoutWhere() }, 0);
        var ifStmt = new SqlIfStatement(
            new SqlComparisonCondition(SqlColumnRef.Simple("x"), SqlComparisonOperator.Equal, new SqlLiteral("1", SqlLiteralType.Number)),
            selectBlock, deleteBlock, sourcePosition: 0);

        var result = _guard.Check(ifStmt, new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "DELETE without WHERE in ELSE block should be blocked");
    }

    #endregion

    // ── ExceedsRowCap default ────────────────────────────────────────

    #region ExceedsRowCap Tests

    [Fact]
    public void Check_AllowedDml_ExceedsRowCapIsFalseByDefault()
    {
        // ExceedsRowCap is always false at check time - it is set during execution
        var result = _guard.Check(DeleteWithWhere(), new DmlSafetyOptions());

        Assert.False(result.ExceedsRowCap, "ExceedsRowCap should be false at check time");
    }

    [Fact]
    public void Check_BlockedDml_HasDefaultEstimatedRows()
    {
        var result = _guard.Check(DeleteWithoutWhere(), new DmlSafetyOptions());

        // Blocked results use the default init value
        Assert.Equal(-1, result.EstimatedAffectedRows);
    }

    #endregion
}
