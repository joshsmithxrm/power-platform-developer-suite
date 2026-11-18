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

        var result = CheckCore(statement, options, s);

        return ApplyProtectionLevel(result, options, protectionLevel);
    }

    private DmlSafetyResult CheckCore(
        TSqlStatement statement,
        DmlSafetyOptions options,
        QuerySafetySettings settings)
    {
        // A clause the engine cannot honour (TOP on UPDATE/DELETE, OUTPUT, MERGE, impersonation, ...) would be dropped
        // and change what the statement affects, so it is refused here, before any confirmation is asked for.
        var unsupported = PPDS.Query.Planning.DmlStatementSupport.FindUnsupported(statement);
        if (unsupported != null)
        {
            return new DmlSafetyResult
            {
                IsBlocked = true,
                ContainsDml = statement is not (ExecuteAsStatement or RevertStatement),
                BlockReason = unsupported,
                ErrorCode = ErrorCodes.Query.DmlBlocked
            };
        }

        return statement switch
        {
            DeleteStatement delete => CheckDelete(delete, options, settings),
            UpdateStatement update => CheckUpdate(update, options, settings),
            InsertStatement => CheckRowCap(options),
            SelectStatement => new DmlSafetyResult { IsBlocked = false },
            BeginEndBlockStatement block => CheckStatements(block.StatementList.Statements, options, settings),
            IfStatement ifStmt => CheckIf(ifStmt, options, settings),
            WhileStatement whileStmt => CheckCore(whileStmt.Statement, options, settings),
            TryCatchStatement tryCatch => CheckTryCatch(tryCatch, options, settings),
            _ => new DmlSafetyResult { IsBlocked = false }
        };
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

        // Read-only statements are always allowed cross-environment, including
        // compound scripts that contain no DML.
        if (!CheckCore(statement, new DmlSafetyOptions { IsConfirmed = true }, effectiveSettings).ContainsDml)
            return new DmlSafetyResult { IsBlocked = false };

        if (effectiveSettings.CrossEnvironmentDmlPolicy == CrossEnvironmentDmlPolicy.ReadOnly)
        {
            return new DmlSafetyResult
            {
                IsBlocked = true,
                ContainsDml = true,
                BlockReason = $"Cross-environment DML is set to read-only. Source: [{sourceLabel}], Target: [{targetLabel}]. Nothing was executed.",
                ErrorCode = ErrorCodes.Query.DmlBlocked
            };
        }

        // Hard rule: Production target always prompts
        if (targetProtection == ProtectionLevel.Production)
        {
            return new DmlSafetyResult
            {
                ContainsDml = true,
                RequiresConfirmation = true,
                ConfirmationMessage = $"Cross-environment DML: [{sourceLabel}] → [{targetLabel}] (Production). Confirm?"
            };
        }

        if (effectiveSettings.CrossEnvironmentDmlPolicy == CrossEnvironmentDmlPolicy.Prompt)
        {
            return new DmlSafetyResult
            {
                ContainsDml = true,
                RequiresConfirmation = true,
                ConfirmationMessage = $"Cross-environment DML: [{sourceLabel}] → [{targetLabel}]. Confirm?"
            };
        }

        return new DmlSafetyResult { IsBlocked = false, ContainsDml = true };
    }

    private static DmlSafetyResult ApplyProtectionLevel(
        DmlSafetyResult result, DmlSafetyOptions options, ProtectionLevel level)
    {
        // No DML detected (read-only or pass-through) — don't apply protection level.
        if (!result.ContainsDml)
            return result;

        // If already blocked, protection level doesn't change anything
        if (result.IsBlocked)
            return result;

        if (level == ProtectionLevel.Production && !options.IsConfirmed)
        {
            return new DmlSafetyResult
            {
                IsBlocked = result.IsBlocked,
                ContainsDml = result.ContainsDml,
                BlockReason = result.BlockReason,
                ErrorCode = result.ErrorCode,
                EstimatedAffectedRows = result.EstimatedAffectedRows,
                RequiresConfirmation = true,
                ConfirmationMessage = result.ConfirmationMessage,
                RequiresPreview = true,
                RowCap = result.RowCap,
                ExceedsRowCap = result.ExceedsRowCap,
                IsDryRun = result.IsDryRun
            };
        }

        if (level == ProtectionLevel.Test && !options.IsConfirmed)
        {
            return new DmlSafetyResult
            {
                IsBlocked = result.IsBlocked,
                ContainsDml = result.ContainsDml,
                BlockReason = result.BlockReason,
                ErrorCode = result.ErrorCode,
                EstimatedAffectedRows = result.EstimatedAffectedRows,
                RequiresConfirmation = true,
                ConfirmationMessage = result.ConfirmationMessage,
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
                ContainsDml = result.ContainsDml,
                BlockReason = result.BlockReason,
                ErrorCode = result.ErrorCode,
                EstimatedAffectedRows = result.EstimatedAffectedRows,
                RequiresConfirmation = result.RequiresConfirmation,
                ConfirmationMessage = result.ConfirmationMessage,
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
                    ContainsDml = true,
                    BlockReason = $"DELETE without WHERE is not allowed. Use 'ppds data truncate --entity {targetName}' for bulk deletion.",
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
                    ContainsDml = true,
                    BlockReason = "UPDATE without WHERE is not allowed. Add a WHERE clause to limit affected records.",
                    ErrorCode = ErrorCodes.Query.DmlBlocked
                };
            }

            // Prevention disabled — still require confirmation
            return CheckRowCap(options);
        }

        return CheckRowCap(options);
    }

    private DmlSafetyResult CheckStatements(
        IEnumerable<TSqlStatement> statements,
        DmlSafetyOptions options,
        QuerySafetySettings settings)
    {
        var aggregate = new DmlSafetyResult { IsBlocked = false };
        foreach (var statement in statements)
        {
            aggregate = Combine(aggregate, CheckCore(statement, options, settings));
            if (aggregate.IsBlocked)
                break;
        }

        return aggregate;
    }

    private DmlSafetyResult CheckIf(IfStatement ifStmt, DmlSafetyOptions options, QuerySafetySettings settings)
    {
        var result = CheckCore(ifStmt.ThenStatement, options, settings);
        if (result.IsBlocked || ifStmt.ElseStatement == null)
            return result;

        return Combine(result, CheckCore(ifStmt.ElseStatement, options, settings));
    }

    private DmlSafetyResult CheckTryCatch(
        TryCatchStatement tryCatch,
        DmlSafetyOptions options,
        QuerySafetySettings settings)
    {
        var tryResult = CheckStatements(tryCatch.TryStatements.Statements, options, settings);
        if (tryResult.IsBlocked)
            return tryResult;

        return Combine(
            tryResult,
            CheckStatements(tryCatch.CatchStatements.Statements, options, settings));
    }

    private static DmlSafetyResult Combine(DmlSafetyResult current, DmlSafetyResult candidate)
    {
        if (current.IsBlocked)
            return current;
        if (candidate.IsBlocked)
            return candidate;
        if (!current.ContainsDml)
            return candidate;
        if (!candidate.ContainsDml)
            return current;

        return new DmlSafetyResult
        {
            ContainsDml = true,
            EstimatedAffectedRows = current.EstimatedAffectedRows < 0 || candidate.EstimatedAffectedRows < 0
                ? -1
                : Math.Max(current.EstimatedAffectedRows, candidate.EstimatedAffectedRows),
            RequiresConfirmation = current.RequiresConfirmation || candidate.RequiresConfirmation,
            ConfirmationMessage = current.ConfirmationMessage ?? candidate.ConfirmationMessage,
            RequiresPreview = current.RequiresPreview || candidate.RequiresPreview,
            RowCap = Math.Min(current.RowCap, candidate.RowCap),
            ExceedsRowCap = current.ExceedsRowCap || candidate.ExceedsRowCap,
            IsDryRun = current.IsDryRun || candidate.IsDryRun
        };
    }

    private static DmlSafetyResult CheckRowCap(DmlSafetyOptions options)
    {
        var rowCap = options.NoLimit ? int.MaxValue : (options.RowCap ?? DefaultRowCap);

        return new DmlSafetyResult
        {
            IsBlocked = false,
            ContainsDml = true,
            // Dry-run is a preview, so its response always describes the confirmation
            // gate that will apply when the caller later requests actual execution.
            RequiresConfirmation = options.IsDryRun || !options.IsConfirmed,
            RowCap = rowCap,
            ExceedsRowCap = false, // The matching row count is unknown at check time; see ExceedsRowCap
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

    /// <summary>
    /// When true (RPC), a confirmed request that contains DML must name, in <see cref="ConfirmedEnvironmentUrl"/>, the
    /// environment it was confirmed for, and that must be the environment it runs against; otherwise it is refused, so a
    /// confirmation cannot be applied to an environment the user did not see.
    /// </summary>
    public bool RequireConfirmedTarget { get; init; }

    /// <summary>The environment URL the caller named for a confirmed request (RPC <c>environmentUrl</c>).</summary>
    public string? ConfirmedEnvironmentUrl { get; init; }

    /// <summary>
    /// When true (RPC), a confirmed request that contains DML must name, in <see cref="ConfirmedProfileIdentity"/>, the
    /// profile it was confirmed for, and that must be the profile it runs as (<see cref="ExecutingProfileIdentity"/>);
    /// otherwise it is refused, so a confirmation cannot run as whichever profile happens to be active.
    /// </summary>
    public bool RequireConfirmedProfile { get; init; }

    /// <summary>The profile identity the caller named for a confirmed request (RPC <c>profileIdentity</c>).</summary>
    public string? ConfirmedProfileIdentity { get; init; }

    /// <summary>The identity of the profile the request executes as (set by the daemon from the resolved profile).</summary>
    public string? ExecutingProfileIdentity { get; init; }
}

/// <summary>
/// Result of a DML safety check.
/// </summary>
public sealed class DmlSafetyResult
{
    /// <summary>Whether the checked statement or compound script contains executable DML.</summary>
    internal bool ContainsDml { get; init; }

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

    /// <summary>
    /// Always false at check time: the number of matching rows is unknown until the statement runs. A statement
    /// that would exceed <see cref="RowCap"/> is refused during execution, before anything changes, with
    /// <c>Query.DmlRowCapExceeded</c>.
    /// </summary>
    public bool ExceedsRowCap { get; init; }

    /// <summary>Whether this is a dry run (no execution).</summary>
    public bool IsDryRun { get; init; }
}
