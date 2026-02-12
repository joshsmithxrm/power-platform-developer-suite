using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Query.Execution;

/// <summary>
/// Exception thrown during query plan execution with a structured error code.
/// </summary>
/// <remarks>
/// <para>
/// This exception lives in the Dataverse layer (which cannot reference PPDS.Cli).
/// The CLI's <c>ExceptionMapper</c> recognizes <see cref="QueryExecutionException"/>
/// and maps it to the corresponding <c>PpdsException</c> with the proper
/// <c>ErrorCodes.Query.*</c> code.
/// </para>
/// <para>
/// Error codes use the same <c>Query.*</c> prefix as <c>ErrorCodes.Query</c> in PPDS.Cli
/// so they match when mapped. See <see cref="QueryErrorCode"/> for constants.
/// </para>
/// </remarks>
public class QueryExecutionException : InvalidOperationException
{
    /// <summary>
    /// Structured error code for programmatic handling.
    /// Uses the <c>Query.*</c> prefix (e.g., <c>"Query.TypeMismatch"</c>).
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Creates a new query execution exception.
    /// </summary>
    /// <param name="errorCode">The structured error code (use <see cref="QueryErrorCode"/> constants).</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="inner">Optional inner exception.</param>
    public QueryExecutionException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode ?? throw new ArgumentNullException(nameof(errorCode));
    }
}

/// <summary>
/// Structured error codes for query execution failures.
/// </summary>
/// <remarks>
/// These codes mirror the <c>ErrorCodes.Query.*</c> constants in PPDS.Cli
/// so the ExceptionMapper can translate them. They are duplicated here to
/// avoid a circular dependency (PPDS.Dataverse cannot reference PPDS.Cli).
/// </remarks>
public static class QueryErrorCode
{
    /// <summary>SQL parse error.</summary>
    public const string ParseError = "Query.ParseError";

    /// <summary>Query execution failed.</summary>
    public const string ExecutionFailed = "Query.ExecutionFailed";

    /// <summary>Aggregate query exceeded the Dataverse 50,000-record limit.</summary>
    public const string AggregateLimitExceeded = "Query.AggregateLimitExceeded";

    /// <summary>Query plan generation timed out.</summary>
    public const string PlanTimeout = "Query.PlanTimeout";

    /// <summary>Expression type mismatch (e.g., comparing string to int).</summary>
    public const string TypeMismatch = "Query.TypeMismatch";

    /// <summary>Query exceeded the in-memory working set limit.</summary>
    public const string MemoryLimitExceeded = "Query.MemoryLimitExceeded";

    /// <summary>DML statement blocked by safety guard.</summary>
    public const string DmlBlocked = "Query.DmlBlocked";

    /// <summary>DML operation would affect more rows than the configured cap.</summary>
    public const string DmlRowCapExceeded = "Query.DmlRowCapExceeded";

    /// <summary>Scalar subquery returned more than one row.</summary>
    public const string SubqueryMultipleRows = "Query.SubqueryMultipleRows";
}
