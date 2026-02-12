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
        => Check(statement, options, settings: null);

    /// <summary>
    /// Checks a DML statement against safety rules with environment-specific settings.
    /// </summary>
    /// <param name="statement">The parsed SQL statement (ScriptDom AST).</param>
    /// <param name="options">Safety check options.</param>
    /// <param name="settings">Per-environment safety settings (null = defaults).</param>
    /// <returns>The safety check result.</returns>
    public DmlSafetyResult Check(TSqlStatement statement, DmlSafetyOptions options, QuerySafetySettings? settings)
    {
        var s = settings ?? new QuerySafetySettings();

        return statement switch
        {
            DeleteStatement delete => CheckDelete(delete, options, s),
            UpdateStatement update => CheckUpdate(update, options, s),
            InsertStatement => CheckRowCap(options),
            SelectStatement => new DmlSafetyResult { IsBlocked = false },
            BeginEndBlockStatement block => CheckBlock(block, options, s),
            IfStatement ifStmt => CheckIf(ifStmt, options, s),
            _ => new DmlSafetyResult { IsBlocked = false }
        };
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

    /// <summary>Active row cap.</summary>
    public int RowCap { get; init; } = DmlSafetyGuard.DefaultRowCap;

    /// <summary>Whether the estimated rows exceed the cap.</summary>
    public bool ExceedsRowCap { get; init; }

    /// <summary>Whether this is a dry run (no execution).</summary>
    public bool IsDryRun { get; init; }
}
