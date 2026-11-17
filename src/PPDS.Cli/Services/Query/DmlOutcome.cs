using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Security;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Outcome of the DML statements executed by one query request. Single source of truth for whether
/// rows failed, how that is classified, and how it is worded, so every surface (CLI, RPC, TUI,
/// history replay) reports the same thing.
/// </summary>
public sealed class DmlOutcome
{
    private const int MaxStatementsListed = 5;

    private DmlOutcome(IReadOnlyList<DmlStatementOutcome> statements)
    {
        Statements = statements;
        AffectedRows = statements.Sum(s => s.AffectedRows);
        FailedRows = statements.Sum(s => s.FailedRows);
    }

    /// <summary>Per-statement outcomes in execution order (one entry per executed DML statement).</summary>
    public IReadOnlyList<DmlStatementOutcome> Statements { get; }

    /// <summary>Total rows that succeeded across all statements.</summary>
    public long AffectedRows { get; }

    /// <summary>Total rows that failed across all statements.</summary>
    public long FailedRows { get; }

    /// <summary>True when any row of any statement failed.</summary>
    public bool HasFailures => FailedRows > 0;

    /// <summary>True when rows failed and none succeeded in any statement.</summary>
    public bool IsTotalFailure => HasFailures && AffectedRows == 0;

    /// <summary>Error code: <c>Query.ExecutionFailed</c> for total failure, <c>Operation.PartialFailure</c> otherwise.</summary>
    public string ErrorCode => IsTotalFailure ? ErrorCodes.Query.ExecutionFailed : ErrorCodes.Operation.PartialFailure;

    /// <summary>Process exit code: 0 no failures, 1 (PartialSuccess) some rows succeeded, 2 (Failure) none did.</summary>
    public int ExitCode => !HasFailures ? ExitCodes.Success : IsTotalFailure ? ExitCodes.Failure : ExitCodes.PartialSuccess;

    /// <summary>
    /// Describes the failure with counts and a representative error tied to the statement it came from.
    /// Null when no row failed. Error text is redacted.
    /// </summary>
    public string? Message => HasFailures ? BuildMessage() : null;

    /// <summary>Builds an outcome from per-statement results; null when no DML statement ran.</summary>
    public static DmlOutcome? From(IReadOnlyList<DmlStatementOutcome> statements)
        => statements.Count == 0 ? null : new DmlOutcome(statements);

    private string BuildMessage()
    {
        var failed = Statements
            .Select((s, i) => (Statement: s, Number: i + 1))
            .Where(x => x.Statement.FailedRows > 0)
            .ToList();

        if (Statements.Count == 1)
        {
            var only = failed[0].Statement;
            var head = only.AffectedRows > 0
                ? $"DML partially succeeded: {only.AffectedRows} row(s) affected, {only.FailedRows} row(s) failed."
                : $"DML failed: all {only.FailedRows} row(s) failed.";
            return head + ErrorSuffix(only.ErrorMessage, " Example error: ");
        }

        var summary = IsTotalFailure
            ? $"DML failed across {Statements.Count} statements: all {FailedRows} row(s) failed."
            : $"DML partially succeeded across {Statements.Count} statements: {AffectedRows} row(s) affected, {FailedRows} row(s) failed.";
        var details = failed.Take(MaxStatementsListed).Select(x =>
            $"#{x.Number} {x.Statement.Operation.ToString().ToUpperInvariant()} {x.Statement.EntityLogicalName}: " +
            $"{x.Statement.AffectedRows} affected, {x.Statement.FailedRows} failed" +
            ErrorSuffix(x.Statement.ErrorMessage, " (example error: ", ")"));
        var more = failed.Count > MaxStatementsListed ? $"; and {failed.Count - MaxStatementsListed} more" : string.Empty;
        return $"{summary} Failed statements: {string.Join("; ", details)}{more}.";
    }

    private static string ErrorSuffix(string? error, string prefix, string suffix = "")
        => string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : prefix + ConnectionStringRedactor.RedactExceptionMessage(error) + suffix;
}
