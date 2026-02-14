using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Validates DML statements for safety before execution.
/// Blocks DELETE/UPDATE without WHERE, enforces row caps.
/// </summary>
public sealed class DmlSafetyGuard
{
    /// <summary>Default maximum rows affected by a DML operation.</summary>
    public const int DefaultRowCap = 10_000;

    /// <summary>
    /// Checks a DML statement for safety violations.
    /// </summary>
    /// <param name="statement">The parsed SQL statement (ScriptDom AST).</param>
    /// <param name="options">Safety check options.</param>
    /// <returns>The safety check result.</returns>
    public DmlSafetyResult Check(TSqlStatement statement, DmlSafetyOptions options)
        => Check(statement, options, settings: null, protectionLevel: ProtectionLevel.Production);

    /// <summary>
    /// Checks a DML statement against safety rules with environment-specific settings.
    /// </summary>
    /// <param name="statement">The parsed SQL statement (ScriptDom AST).</param>
    /// <param name="options">Safety check options.</param>
    /// <param name="settings">Per-environment safety settings (null = defaults).</param>
    /// <param name="protectionLevel">Environment protection level.</param>
    /// <returns>The safety check result.</returns>
    public DmlSafetyResult Check(
        TSqlStatement statement,
        DmlSafetyOptions options,
        QuerySafetySettings? settings = null,
        ProtectionLevel protectionLevel = ProtectionLevel.Production)
    {
        var s = settings ?? new QuerySafetySettings();

        var result = statement switch
        {
            DeleteStatement delete => CheckDelete(delete, options, s),
            UpdateStatement update => CheckUpdate(update, options, s),
            InsertStatement => CheckRowCap(options),
            SelectStatement => new DmlSafetyResult { IsBlocked = false },
            BeginEndBlockStatement block => CheckBlock(block, options, s),
            IfStatement ifStmt => CheckIf(ifStmt, options, s),
            _ => new DmlSafetyResult { IsBlocked = false }
        };

        return ApplyProtectionLevel(result, statement, options, protectionLevel);
    }

    /// <summary>
    /// Maps a Dataverse environment type to a protection level.
    /// Only Production environments are locked down; everything else is unrestricted.
    /// </summary>
    public static ProtectionLevel DetectProtectionLevel(EnvironmentType environmentType) => environmentType switch
    {
        EnvironmentType.Production => ProtectionLevel.Production,
        _ => ProtectionLevel.Development
    };

    /// <summary>
    /// Checks whether a cross-environment DML operation is allowed.
    /// </summary>
    /// <param name="statement">The parsed SQL statement.</param>
    /// <param name="settings">Per-environment safety settings.</param>
    /// <param name="sourceLabel">The source environment label.</param>
    /// <param name="targetLabel">The target environment label.</param>
    /// <param name="targetProtection">The target environment's protection level.</param>
    /// <returns>The safety check result.</returns>
    public DmlSafetyResult CheckCrossEnvironmentDml(
        TSqlStatement statement,
        QuerySafetySettings? settings,
        string sourceLabel,
        string targetLabel,
        ProtectionLevel targetProtection = ProtectionLevel.Production)
    {
        var effectiveSettings = settings ?? new QuerySafetySettings();

        // SELECT statements are always allowed cross-environment
        if (statement is SelectStatement)
            return new DmlSafetyResult { IsBlocked = false };

        if (effectiveSettings.CrossEnvironmentDmlPolicy == CrossEnvironmentDmlPolicy.ReadOnly)
        {
            return new DmlSafetyResult
            {
                IsBlocked = true,
                BlockReason = $"Cross-environment DML is set to read-only. Source: [{sourceLabel}], Target: [{targetLabel}]. Change cross_env_dml_policy to 'Prompt' or 'Allow' to enable.",
                ErrorCode = ErrorCodes.Query.DmlBlocked
            };
        }

        // Hard rule: Production target always prompts
        if (targetProtection == ProtectionLevel.Production)
        {
            return new DmlSafetyResult
            {
                RequiresConfirmation = true,
                ConfirmationMessage = $"Cross-environment DML: [{sourceLabel}] → [{targetLabel}] (Production). Confirm?"
            };
        }

        if (effectiveSettings.CrossEnvironmentDmlPolicy == CrossEnvironmentDmlPolicy.Prompt)
        {
            return new DmlSafetyResult
            {
                RequiresConfirmation = true,
                ConfirmationMessage = $"Cross-environment DML: [{sourceLabel}] → [{targetLabel}]. Confirm?"
            };
        }

        return new DmlSafetyResult { IsBlocked = false };
    }

    private static DmlSafetyResult ApplyProtectionLevel(
        DmlSafetyResult result, TSqlStatement statement, DmlSafetyOptions options, ProtectionLevel level)
    {
        // No DML detected (read-only or pass-through) — don't apply protection level
        if (!result.IsBlocked && !result.RequiresConfirmation)
            return result;

        // If already blocked, protection level doesn't change anything
        if (result.IsBlocked)
            return result;

        if (level == ProtectionLevel.Production && !options.IsConfirmed)
        {
            return new DmlSafetyResult
            {
                IsBlocked = result.IsBlocked,
                BlockReason = result.BlockReason,
                ErrorCode = result.ErrorCode,
                EstimatedAffectedRows = result.EstimatedAffectedRows,
                RequiresConfirmation = true,
                RequiresPreview = true,
                RowCap = result.RowCap,
                ExceedsRowCap = result.ExceedsRowCap,
                IsDryRun = result.IsDryRun
            };
        }

        if (level == ProtectionLevel.Development && options.IsConfirmed)
        {
            return new DmlSafetyResult
            {
                IsBlocked = result.IsBlocked,
                BlockReason = result.BlockReason,
                ErrorCode = result.ErrorCode,
                EstimatedAffectedRows = result.EstimatedAffectedRows,
                RequiresConfirmation = false,
                RowCap = result.RowCap,
                ExceedsRowCap = result.ExceedsRowCap,
                IsDryRun = result.IsDryRun
            };
        }

        return result;
    }

    private static DmlSafetyResult CheckDelete(DeleteStatement delete, DmlSafetyOptions options, QuerySafetySettings settings)
    {
        if (delete.DeleteSpecification.WhereClause == null)
        {
            if (settings.PreventDeleteWithoutWhere)
            {
                var targetName = delete.DeleteSpecification.Target is NamedTableReference namedTable
                    ? namedTable.SchemaObject.BaseIdentifier.Value
                    : "table";

                return new DmlSafetyResult
                {
                    IsBlocked = true,
                    BlockReason = $"DELETE without WHERE is not allowed. Use 'ppds truncate {targetName}' for bulk deletion.",
                    ErrorCode = ErrorCodes.Query.DmlBlocked
                };
            }

            // Prevention disabled — still require confirmation
            return CheckRowCap(options);
        }

        return CheckRowCap(options);
    }

    private static DmlSafetyResult CheckUpdate(UpdateStatement update, DmlSafetyOptions options, QuerySafetySettings settings)
    {
        if (update.UpdateSpecification.WhereClause == null)
        {
            if (settings.PreventUpdateWithoutWhere)
            {
                return new DmlSafetyResult
                {
                    IsBlocked = true,
                    BlockReason = "UPDATE without WHERE is not allowed. Add a WHERE clause to limit affected records.",
                    ErrorCode = ErrorCodes.Query.DmlBlocked
                };
            }

            // Prevention disabled — still require confirmation
            return CheckRowCap(options);
        }

        return CheckRowCap(options);
    }

    private DmlSafetyResult CheckBlock(BeginEndBlockStatement block, DmlSafetyOptions options, QuerySafetySettings settings)
    {
        // Return the most restrictive result from any contained statement
        DmlSafetyResult worst = new() { IsBlocked = false };
        foreach (var stmt in block.StatementList.Statements)
        {
            var result = Check(stmt, options, settings);
            if (result.IsBlocked) return result;
            if (result.RequiresConfirmation) worst = result;
        }
        return worst;
    }

    private DmlSafetyResult CheckIf(IfStatement ifStmt, DmlSafetyOptions options, QuerySafetySettings settings)
    {
        var thenResult = Check(ifStmt.ThenStatement, options, settings);
        if (thenResult.IsBlocked) return thenResult;

        if (ifStmt.ElseStatement != null)
        {
            var elseResult = Check(ifStmt.ElseStatement, options, settings);
            if (elseResult.IsBlocked) return elseResult;
            if (elseResult.RequiresConfirmation) return elseResult;
        }

        return thenResult;
    }

    private static DmlSafetyResult CheckRowCap(DmlSafetyOptions options)
    {
        var rowCap = options.NoLimit ? int.MaxValue : (options.RowCap ?? DefaultRowCap);

        return new DmlSafetyResult
        {
            IsBlocked = false,
            RequiresConfirmation = !options.IsConfirmed,
            RowCap = rowCap,
            ExceedsRowCap = false, // Set during execution when actual count is known
            IsDryRun = options.IsDryRun
        };
    }
}

/// <summary>
/// Options for DML safety checks.
/// </summary>
public sealed class DmlSafetyOptions
{
    /// <summary>Whether the user has confirmed the operation (--confirm).</summary>
    public bool IsConfirmed { get; init; }

    /// <summary>Whether to show the plan without executing (--dry-run).</summary>
    public bool IsDryRun { get; init; }

    /// <summary>Whether to remove the row cap (--no-limit).</summary>
    public bool NoLimit { get; init; }

    /// <summary>Custom row cap (default: 10,000).</summary>
    public int? RowCap { get; init; }
}

/// <summary>
/// Result of a DML safety check.
/// </summary>
public sealed class DmlSafetyResult
{
    /// <summary>Whether the operation is completely blocked (no WHERE).</summary>
    public bool IsBlocked { get; init; }

    /// <summary>Reason the operation is blocked.</summary>
    public string? BlockReason { get; init; }

    /// <summary>Error code for blocked operations.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Estimated affected rows (-1 if unknown).</summary>
    public long EstimatedAffectedRows { get; init; } = -1;

    /// <summary>Whether confirmation is required.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Confirmation message shown to the user.</summary>
    public string? ConfirmationMessage { get; init; }

    /// <summary>Whether the user must preview affected records before confirming (Production environments).</summary>
    public bool RequiresPreview { get; init; }

    /// <summary>Active row cap.</summary>
    public int RowCap { get; init; } = DmlSafetyGuard.DefaultRowCap;

    /// <summary>Whether the estimated rows exceed the cap.</summary>
    public bool ExceedsRowCap { get; init; }

    /// <summary>Whether this is a dry run (no execution).</summary>
    public bool IsDryRun { get; init; }
}
