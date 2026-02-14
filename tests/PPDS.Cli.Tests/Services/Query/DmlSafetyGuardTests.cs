using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Unit tests for <see cref="DmlSafetyGuard"/>.
/// Validates that DELETE/UPDATE without WHERE are blocked and that
/// DML safety options (confirm, dry-run, no-limit, row cap) work correctly.
/// Uses ScriptDom types parsed via <see cref="QueryParser"/>.
/// </summary>
[Trait("Category", "TuiUnit")]
public class DmlSafetyGuardTests
{
    private readonly DmlSafetyGuard _guard = new();
    private readonly QueryParser _parser = new();

    // ── Helper methods ──────────────────────────────────────────────

    private TSqlStatement Parse(string sql) => _parser.ParseStatement(sql);

    // ── DELETE Safety ────────────────────────────────────────────────

    #region DELETE Tests

    [Fact]
    public void Check_DeleteWithoutWhere_IsBlocked()
    {
        var result = _guard.Check(Parse("DELETE FROM account"), new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "DELETE without WHERE should be blocked");
        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
    }

    [Fact]
    public void Check_DeleteWithoutWhere_BlockReasonMentionsPpdsTruncate()
    {
        var result = _guard.Check(Parse("DELETE FROM account"), new DmlSafetyOptions());

        Assert.Contains("ppds truncate", result.BlockReason);
    }

    [Fact]
    public void Check_DeleteWithoutWhere_BlockReasonContainsTableName()
    {
        var result = _guard.Check(Parse("DELETE FROM lead"), new DmlSafetyOptions());

        Assert.Contains("lead", result.BlockReason!);
    }

    [Fact]
    public void Check_DeleteWithWhere_IsNotBlocked()
    {
        var result = _guard.Check(Parse("DELETE FROM account WHERE statecode = 1"), new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "DELETE with WHERE should not be blocked");
    }

    [Fact]
    public void Check_DeleteWithWhere_RequiresConfirmationByDefault()
    {
        var result = _guard.Check(Parse("DELETE FROM account WHERE statecode = 1"), new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "DELETE with WHERE should require confirmation by default");
    }

    [Fact]
    public void Check_DeleteWithWhereAndConfirm_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(
            Parse("DELETE FROM account WHERE statecode = 1"),
            new DmlSafetyOptions { IsConfirmed = true });

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation, "DELETE with --confirm should not require confirmation");
    }

    [Fact]
    public void Check_DeleteWithWhereAndDryRun_SetsIsDryRun()
    {
        var result = _guard.Check(
            Parse("DELETE FROM account WHERE statecode = 1"),
            new DmlSafetyOptions { IsDryRun = true });

        Assert.False(result.IsBlocked);
        Assert.True(result.IsDryRun, "--dry-run should set IsDryRun on the result");
    }

    [Fact]
    public void Check_DeleteWithoutWhere_IsBlockedEvenWithConfirm()
    {
        var result = _guard.Check(
            Parse("DELETE FROM account"),
            new DmlSafetyOptions { IsConfirmed = true });

        Assert.True(result.IsBlocked, "DELETE without WHERE should be blocked even with --confirm");
    }

    [Fact]
    public void Check_DeleteWithoutWhere_IsBlockedEvenWithNoLimit()
    {
        var result = _guard.Check(
            Parse("DELETE FROM account"),
            new DmlSafetyOptions { NoLimit = true });

        Assert.True(result.IsBlocked, "DELETE without WHERE should be blocked even with --no-limit");
    }

    #endregion

    // ── UPDATE Safety ────────────────────────────────────────────────

    #region UPDATE Tests

    [Fact]
    public void Check_UpdateWithoutWhere_IsBlocked()
    {
        var result = _guard.Check(Parse("UPDATE contact SET firstname = 'Test'"), new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "UPDATE without WHERE should be blocked");
        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
    }

    [Fact]
    public void Check_UpdateWithoutWhere_BlockReasonMentionsUpdateWithoutWhere()
    {
        var result = _guard.Check(Parse("UPDATE contact SET firstname = 'Test'"), new DmlSafetyOptions());

        Assert.Contains("UPDATE without WHERE", result.BlockReason);
    }

    [Fact]
    public void Check_UpdateWithWhere_IsNotBlocked()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "UPDATE with WHERE should not be blocked");
    }

    [Fact]
    public void Check_UpdateWithWhere_RequiresConfirmationByDefault()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "UPDATE with WHERE should require confirmation by default");
    }

    [Fact]
    public void Check_UpdateWithWhereAndConfirm_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions { IsConfirmed = true });

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation, "UPDATE with --confirm should not require confirmation");
    }

    [Fact]
    public void Check_UpdateWithWhereAndDryRun_SetsIsDryRun()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions { IsDryRun = true });

        Assert.False(result.IsBlocked);
        Assert.True(result.IsDryRun, "UPDATE --dry-run should set IsDryRun on the result");
    }

    [Fact]
    public void Check_UpdateWithoutWhere_IsBlockedEvenWithConfirm()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test'"),
            new DmlSafetyOptions { IsConfirmed = true });

        Assert.True(result.IsBlocked, "UPDATE without WHERE should be blocked even with --confirm");
    }

    [Fact]
    public void Check_UpdateWithoutWhere_IsBlockedEvenWithNoLimit()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test'"),
            new DmlSafetyOptions { NoLimit = true });

        Assert.True(result.IsBlocked, "UPDATE without WHERE should be blocked even with --no-limit");
    }

    #endregion

    // ── INSERT Safety ────────────────────────────────────────────────

    #region INSERT Tests

    [Fact]
    public void Check_Insert_IsNeverBlocked()
    {
        var result = _guard.Check(Parse("INSERT INTO account (name) VALUES ('Test')"), new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "INSERT should never be blocked");
    }

    [Fact]
    public void Check_Insert_RequiresConfirmationByDefault()
    {
        var result = _guard.Check(Parse("INSERT INTO account (name) VALUES ('Test')"), new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "INSERT should require confirmation by default");
    }

    [Fact]
    public void Check_InsertWithConfirm_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(
            Parse("INSERT INTO account (name) VALUES ('Test')"),
            new DmlSafetyOptions { IsConfirmed = true });

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation, "INSERT with --confirm should not require confirmation");
    }

    [Fact]
    public void Check_Insert_HasUnknownEstimatedRows()
    {
        var result = _guard.Check(Parse("INSERT INTO account (name) VALUES ('Test')"), new DmlSafetyOptions());

        Assert.Equal(-1, result.EstimatedAffectedRows);
    }

    #endregion

    // ── SELECT Pass-through ──────────────────────────────────────────

    #region SELECT Tests

    [Fact]
    public void Check_Select_IsNeverBlocked()
    {
        var result = _guard.Check(Parse("SELECT * FROM account"), new DmlSafetyOptions());

        Assert.False(result.IsBlocked, "SELECT should never be blocked");
    }

    [Fact]
    public void Check_Select_DoesNotRequireConfirmation()
    {
        var result = _guard.Check(Parse("SELECT * FROM account"), new DmlSafetyOptions());

        Assert.False(result.RequiresConfirmation, "SELECT should not require confirmation");
    }

    [Fact]
    public void Check_Select_IsDryRunFalse()
    {
        var result = _guard.Check(Parse("SELECT * FROM account"), new DmlSafetyOptions { IsDryRun = true });

        // SELECT pass-through does not propagate dry-run - it has no effect on reads
        Assert.False(result.IsDryRun, "SELECT should not be affected by --dry-run");
    }

    [Fact]
    public void Check_Select_HasNoErrorCode()
    {
        var result = _guard.Check(Parse("SELECT * FROM account"), new DmlSafetyOptions());

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
        var result = _guard.Check(Parse("DELETE FROM account WHERE statecode = 1"), new DmlSafetyOptions());

        Assert.Equal(DmlSafetyGuard.DefaultRowCap, result.RowCap);
        Assert.Equal(10_000, result.RowCap);
    }

    [Fact]
    public void Check_NoLimit_SetsRowCapToMaxValue()
    {
        var result = _guard.Check(
            Parse("DELETE FROM account WHERE statecode = 1"),
            new DmlSafetyOptions { NoLimit = true });

        Assert.Equal(int.MaxValue, result.RowCap);
    }

    [Fact]
    public void Check_CustomRowCap_IsHonored()
    {
        var result = _guard.Check(
            Parse("DELETE FROM account WHERE statecode = 1"),
            new DmlSafetyOptions { RowCap = 500 });

        Assert.Equal(500, result.RowCap);
    }

    [Fact]
    public void Check_NoLimitOverridesCustomRowCap()
    {
        // When NoLimit is true, the custom RowCap should be ignored
        var result = _guard.Check(
            Parse("DELETE FROM account WHERE statecode = 1"),
            new DmlSafetyOptions { NoLimit = true, RowCap = 500 });

        Assert.Equal(int.MaxValue, result.RowCap);
    }

    [Fact]
    public void Check_UpdateWithWhere_DefaultRowCap()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions());

        Assert.Equal(DmlSafetyGuard.DefaultRowCap, result.RowCap);
    }

    [Fact]
    public void Check_UpdateWithWhereAndNoLimit_SetsRowCapToMaxValue()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions { NoLimit = true });

        Assert.Equal(int.MaxValue, result.RowCap);
    }

    [Fact]
    public void Check_UpdateWithWhereAndCustomRowCap_IsHonored()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions { RowCap = 250 });

        Assert.Equal(250, result.RowCap);
    }

    #endregion

    // ── Error Codes ──────────────────────────────────────────────────

    #region Error Code Tests

    [Fact]
    public void Check_BlockedDelete_UsesDmlBlockedErrorCode()
    {
        var result = _guard.Check(Parse("DELETE FROM account"), new DmlSafetyOptions());

        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
        Assert.Equal("Query.DmlBlocked", result.ErrorCode);
    }

    [Fact]
    public void Check_BlockedUpdate_UsesDmlBlockedErrorCode()
    {
        var result = _guard.Check(Parse("UPDATE contact SET firstname = 'Test'"), new DmlSafetyOptions());

        Assert.Equal(ErrorCodes.Query.DmlBlocked, result.ErrorCode);
        Assert.Equal("Query.DmlBlocked", result.ErrorCode);
    }

    [Fact]
    public void Check_AllowedDelete_HasNoErrorCode()
    {
        var result = _guard.Check(Parse("DELETE FROM account WHERE statecode = 1"), new DmlSafetyOptions());

        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Check_AllowedUpdate_HasNoErrorCode()
    {
        var result = _guard.Check(
            Parse("UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001'"),
            new DmlSafetyOptions());

        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Check_Insert_HasNoErrorCode()
    {
        var result = _guard.Check(Parse("INSERT INTO account (name) VALUES ('Test')"), new DmlSafetyOptions());

        Assert.Null(result.ErrorCode);
    }

    #endregion

    // ── Script DML Detection ──────────────────────────────────────────

    #region Script DML Detection Tests

    [Fact]
    public void Check_IfStatement_ContainingDelete_RequiresConfirmation()
    {
        var stmt = Parse("IF 1 = 1 BEGIN DELETE FROM account WHERE statecode = 1 END");

        var result = _guard.Check(stmt, new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "DML inside IF should require confirmation");
    }

    [Fact]
    public void Check_IfStatement_ContainingDeleteWithoutWhere_IsBlocked()
    {
        var stmt = Parse("IF 1 = 1 BEGIN DELETE FROM account END");

        var result = _guard.Check(stmt, new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "DELETE without WHERE inside IF should be blocked");
    }

    [Fact]
    public void Check_BlockStatement_ContainingUpdate_RequiresConfirmation()
    {
        var stmt = Parse("BEGIN UPDATE contact SET firstname = 'Test' WHERE contactid = '00000000-0000-0000-0000-000000000001' END");

        var result = _guard.Check(stmt, new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "DML inside block should require confirmation");
    }

    [Fact]
    public void Check_BlockStatement_SelectOnly_NotBlocked()
    {
        var stmt = Parse("BEGIN SELECT * FROM account END");

        var result = _guard.Check(stmt, new DmlSafetyOptions());

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void Check_IfElse_DmlInElse_IsDetected()
    {
        var stmt = Parse("IF 1 = 1 BEGIN SELECT * FROM account END ELSE BEGIN DELETE FROM account END");

        var result = _guard.Check(stmt, new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "DELETE without WHERE in ELSE block should be blocked");
    }

    [Fact]
    public void Check_IfStatement_SingleStatementThen_RequiresConfirmation()
    {
        // IfStatement.ThenStatement can be a single statement, not wrapped in BEGIN/END
        var stmt = Parse("IF 1 = 1 DELETE FROM account WHERE statecode = 1");

        var result = _guard.Check(stmt, new DmlSafetyOptions());

        Assert.True(result.RequiresConfirmation, "Single-statement DML in IF THEN should require confirmation");
    }

    [Fact]
    public void Check_IfElse_SingleStatementElse_IsDetected()
    {
        // IfStatement.ElseStatement can be a single statement without BEGIN/END
        var stmt = Parse("IF 1 = 1 SELECT * FROM account ELSE DELETE FROM account");

        var result = _guard.Check(stmt, new DmlSafetyOptions());

        Assert.True(result.IsBlocked, "Single-statement DELETE without WHERE in ELSE should be blocked");
    }

    #endregion

    // ── Configurable Safety Settings ─────────────────────────────────

    #region Configurable Safety Settings Tests

    [Fact]
    public void Check_DeleteWithoutWhere_WhenPreventionDisabled_AllowsWithConfirmation()
    {
        var settings = new QuerySafetySettings { PreventDeleteWithoutWhere = false };
        var result = _guard.Check(Parse("DELETE FROM account"), new DmlSafetyOptions(), settings);

        Assert.False(result.IsBlocked);
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void Check_UpdateWithoutWhere_WhenPreventionDisabled_AllowsWithConfirmation()
    {
        var settings = new QuerySafetySettings { PreventUpdateWithoutWhere = false };
        var result = _guard.Check(Parse("UPDATE contact SET firstname = 'Test'"), new DmlSafetyOptions(), settings);

        Assert.False(result.IsBlocked);
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void Check_Delete_CustomThreshold_AlwaysPrompts()
    {
        var settings = new QuerySafetySettings { WarnDeleteThreshold = 0 }; // Always prompt
        var result = _guard.Check(Parse("DELETE FROM account WHERE statecode = 1"), new DmlSafetyOptions(), settings);

        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void Check_NullSettings_UsesDefaults()
    {
        // Null settings should behave identically to no settings (original behavior)
        var result = _guard.Check(Parse("DELETE FROM account"), new DmlSafetyOptions(), settings: null);

        Assert.True(result.IsBlocked, "DELETE without WHERE should still be blocked with null settings");
    }

    #endregion

    // ── Protection Level Enforcement ─────────────────────────────────

    #region Protection Level Tests

    [Fact]
    public void Check_ProductionEnvironment_RequiresConfirmationAndPreview()
    {
        var result = _guard.Check(
            Parse("UPDATE account SET name = 'x' WHERE accountid = '123'"),
            new DmlSafetyOptions(),
            protectionLevel: ProtectionLevel.Production);

        Assert.True(result.RequiresConfirmation);
        Assert.True(result.RequiresPreview);
    }

    [Fact]
    public void Check_DevelopmentEnvironment_UnrestrictedDml()
    {
        var settings = new QuerySafetySettings { PreventDeleteWithoutWhere = false };
        var result = _guard.Check(
            Parse("DELETE FROM account"),
            new DmlSafetyOptions { IsConfirmed = true },
            settings,
            protectionLevel: ProtectionLevel.Development);

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void Check_TestEnvironment_UsesThresholds()
    {
        var result = _guard.Check(
            Parse("DELETE FROM account WHERE statecode = 1"),
            new DmlSafetyOptions(),
            protectionLevel: ProtectionLevel.Test);

        Assert.False(result.IsBlocked);
        Assert.True(result.RequiresConfirmation);
        Assert.False(result.RequiresPreview);
    }

    [Fact]
    public void Check_ProductionEnvironment_SelectNotAffected()
    {
        var result = _guard.Check(
            Parse("SELECT * FROM account"),
            new DmlSafetyOptions(),
            protectionLevel: ProtectionLevel.Production);

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation);
        Assert.False(result.RequiresPreview);
    }

    [Theory]
    [InlineData(EnvironmentType.Production, ProtectionLevel.Production)]
    [InlineData(EnvironmentType.Sandbox, ProtectionLevel.Development)]
    [InlineData(EnvironmentType.Development, ProtectionLevel.Development)]
    [InlineData(EnvironmentType.Test, ProtectionLevel.Development)]
    [InlineData(EnvironmentType.Trial, ProtectionLevel.Development)]
    [InlineData(EnvironmentType.Unknown, ProtectionLevel.Development)]
    public void DetectProtectionLevel_MapsEnvironmentType_Correctly(EnvironmentType envType, ProtectionLevel expected)
    {
        Assert.Equal(expected, DmlSafetyGuard.DetectProtectionLevel(envType));
    }

    #endregion

    // ── ExceedsRowCap default ────────────────────────────────────────

    #region ExceedsRowCap Tests

    [Fact]
    public void Check_AllowedDml_ExceedsRowCapIsFalseByDefault()
    {
        // ExceedsRowCap is always false at check time - it is set during execution
        var result = _guard.Check(Parse("DELETE FROM account WHERE statecode = 1"), new DmlSafetyOptions());

        Assert.False(result.ExceedsRowCap, "ExceedsRowCap should be false at check time");
    }

    [Fact]
    public void Check_BlockedDml_HasDefaultEstimatedRows()
    {
        var result = _guard.Check(Parse("DELETE FROM account"), new DmlSafetyOptions());

        // Blocked results use the default init value
        Assert.Equal(-1, result.EstimatedAffectedRows);
    }

    #endregion

    // ── End-to-End Safety Policy ─────────────────────────────────────

    #region End-to-End Safety Policy Tests

    [Theory]
    [InlineData("DELETE FROM account", ProtectionLevel.Production, true, false)]
    [InlineData("DELETE FROM account", ProtectionLevel.Development, true, false)]
    [InlineData("DELETE FROM account WHERE x = 1", ProtectionLevel.Production, false, true)]
    [InlineData("DELETE FROM account WHERE x = 1", ProtectionLevel.Test, false, true)]
    [InlineData("UPDATE account SET name = 'x'", ProtectionLevel.Production, true, false)]
    [InlineData("INSERT INTO account (name) VALUES ('x')", ProtectionLevel.Production, false, true)]
    public void Check_ProtectionLevel_EnforcesCorrectPolicy(
        string sql, ProtectionLevel level, bool expectBlocked, bool expectConfirmation)
    {
        var result = _guard.Check(Parse(sql), new DmlSafetyOptions(), protectionLevel: level);

        Assert.Equal(expectBlocked, result.IsBlocked);
        if (!expectBlocked)
            Assert.Equal(expectConfirmation, result.RequiresConfirmation);
    }

    [Theory]
    [InlineData("DELETE FROM account WHERE x = 1", ProtectionLevel.Production, true)]
    [InlineData("DELETE FROM account WHERE x = 1", ProtectionLevel.Test, false)]
    [InlineData("UPDATE account SET name = 'x' WHERE accountid = '123'", ProtectionLevel.Production, true)]
    [InlineData("INSERT INTO account (name) VALUES ('x')", ProtectionLevel.Production, true)]
    public void Check_ProtectionLevel_PreviewRequirement(
        string sql, ProtectionLevel level, bool expectPreview)
    {
        var result = _guard.Check(Parse(sql), new DmlSafetyOptions(), protectionLevel: level);

        Assert.Equal(expectPreview, result.RequiresPreview);
    }

    [Fact]
    public void Check_ConfigurableSettings_WithProtectionLevel_Combined()
    {
        // Development + disabled prevention = allow DELETE without WHERE (with confirmation)
        var settings = new QuerySafetySettings { PreventDeleteWithoutWhere = false };
        var result = _guard.Check(
            Parse("DELETE FROM account"),
            new DmlSafetyOptions(),
            settings,
            ProtectionLevel.Development);

        Assert.False(result.IsBlocked);
        Assert.True(result.RequiresConfirmation);
    }

    #endregion

    // ── Cross-Environment DML Policy ─────────────────────────────────

    #region Cross-Environment DML Policy Tests

    [Fact]
    public void CheckCrossEnvDml_ReadOnlyPolicy_BlocksDml()
    {
        var settings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.ReadOnly };
        var result = _guard.CheckCrossEnvironmentDml(
            Parse("DELETE FROM account WHERE x = 1"), settings, "DEV", "UAT");

        Assert.True(result.IsBlocked);
        Assert.Contains("read-only", result.BlockReason!);
        Assert.Contains("DEV", result.BlockReason!);
        Assert.Contains("UAT", result.BlockReason!);
    }

    [Fact]
    public void CheckCrossEnvDml_ReadOnlyPolicy_AllowsSelect()
    {
        var settings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.ReadOnly };
        var result = _guard.CheckCrossEnvironmentDml(
            Parse("SELECT name FROM account"), settings, "DEV", "UAT");

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void CheckCrossEnvDml_PromptPolicy_RequiresConfirmation()
    {
        var settings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Prompt };
        var result = _guard.CheckCrossEnvironmentDml(
            Parse("UPDATE account SET name = 'x' WHERE accountid = '123'"),
            settings, "DEV", "UAT", ProtectionLevel.Development);

        Assert.False(result.IsBlocked);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("DEV", result.ConfirmationMessage!);
        Assert.Contains("UAT", result.ConfirmationMessage!);
    }

    [Fact]
    public void CheckCrossEnvDml_AllowPolicy_NoConfirmation()
    {
        var settings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Allow };
        var result = _guard.CheckCrossEnvironmentDml(
            Parse("DELETE FROM account WHERE x = 1"),
            settings, "DEV", "UAT", ProtectionLevel.Development);

        Assert.False(result.IsBlocked);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void CheckCrossEnvDml_ProductionTarget_AlwaysPrompts()
    {
        var settings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Allow };
        var result = _guard.CheckCrossEnvironmentDml(
            Parse("DELETE FROM account WHERE x = 1"),
            settings, "DEV", "PROD", ProtectionLevel.Production);

        Assert.True(result.RequiresConfirmation);
        Assert.Contains("Production", result.ConfirmationMessage!);
    }

    [Fact]
    public void CheckCrossEnvDml_DefaultSettings_IsReadOnly()
    {
        // Default CrossEnvironmentDmlPolicy should be ReadOnly
        var result = _guard.CheckCrossEnvironmentDml(
            Parse("DELETE FROM account WHERE x = 1"), null, "DEV", "UAT");

        Assert.True(result.IsBlocked);
        Assert.Contains("read-only", result.BlockReason!);
    }

    #endregion
}
