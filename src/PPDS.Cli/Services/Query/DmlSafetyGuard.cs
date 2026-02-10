using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Sql.Ast;

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
    /// <param name="statement">The parsed SQL statement.</param>
    /// <param name="options">Safety check options.</param>
    /// <returns>The safety check result.</returns>
    public DmlSafetyResult Check(ISqlStatement statement, DmlSafetyOptions options)
    {
        return statement switch
        {
            SqlDeleteStatement delete => CheckDelete(delete, options),
            SqlUpdateStatement update => CheckUpdate(update, options),
            SqlInsertStatement => new DmlSafetyResult
            {
                IsBlocked = false,
                RequiresConfirmation = !options.IsConfirmed,
                EstimatedAffectedRows = -1
            },
            SqlSelectStatement => new DmlSafetyResult { IsBlocked = false },
            SqlBlockStatement block => CheckBlock(block, options),
            SqlIfStatement ifStmt => CheckIf(ifStmt, options),
            _ => new DmlSafetyResult { IsBlocked = false }
        };
    }

    private static DmlSafetyResult CheckDelete(SqlDeleteStatement delete, DmlSafetyOptions options)
    {
        // DELETE without WHERE is ALWAYS blocked
        if (delete.Where == null)
        {
            return new DmlSafetyResult
            {
                IsBlocked = true,
                BlockReason = $"DELETE without WHERE is not allowed. Use 'ppds truncate {delete.TargetTable.TableName}' for bulk deletion.",
                ErrorCode = ErrorCodes.Query.DmlBlocked
            };
        }

        return CheckRowCap(options);
    }

    private static DmlSafetyResult CheckUpdate(SqlUpdateStatement update, DmlSafetyOptions options)
    {
        // UPDATE without WHERE is ALWAYS blocked
        if (update.Where == null)
        {
            return new DmlSafetyResult
            {
                IsBlocked = true,
                BlockReason = "UPDATE without WHERE is not allowed. Add a WHERE clause to limit affected records.",
                ErrorCode = ErrorCodes.Query.DmlBlocked
            };
        }

        return CheckRowCap(options);
    }

    private DmlSafetyResult CheckBlock(SqlBlockStatement block, DmlSafetyOptions options)
    {
        // Return the most restrictive result from any contained statement
        DmlSafetyResult worst = new() { IsBlocked = false };
        foreach (var stmt in block.Statements)
        {
            var result = Check(stmt, options);
            if (result.IsBlocked) return result;
            if (result.RequiresConfirmation) worst = result;
        }
        return worst;
    }

    private DmlSafetyResult CheckIf(SqlIfStatement ifStmt, DmlSafetyOptions options)
    {
        var thenResult = Check(ifStmt.ThenBlock, options);
        if (thenResult.IsBlocked) return thenResult;

        if (ifStmt.ElseBlock != null)
        {
            var elseResult = Check(ifStmt.ElseBlock, options);
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

    /// <summary>Active row cap.</summary>
    public int RowCap { get; init; } = DmlSafetyGuard.DefaultRowCap;

    /// <summary>Whether the estimated rows exceed the cap.</summary>
    public bool ExceedsRowCap { get; init; }

    /// <summary>Whether this is a dry run (no execution).</summary>
    public bool IsDryRun { get; init; }
}
