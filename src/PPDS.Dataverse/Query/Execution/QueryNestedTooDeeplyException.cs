using System;

namespace PPDS.Dataverse.Query.Execution;

/// <summary>
/// Which limit refused a statement in a <see cref="QueryNestedTooDeeplyException"/>.
/// </summary>
public enum QueryDepthLimit
{
    /// <summary>The text holds more <c>/*</c> than the SQL lexer can nest on the parse thread's stack.</summary>
    CommentNesting,

    /// <summary>
    /// The text holds more counted tokens (every token but literals and commas) than the SQL parser can be
    /// shown to handle on the parse thread's stack.
    /// </summary>
    TokenCount,

    /// <summary>A recursive walk over the statement, its plan or its expressions ran out of stack.</summary>
    ExecutionStack
}

/// <summary>
/// Thrown when a SQL statement is nested too deeply, or is too large, to parse, plan or run without
/// running out of stack (#1952). A stack overflow cannot be caught and ends the process, so every
/// recursive step checks the stack first and refuses with this exception instead.
/// </summary>
/// <remarks>
/// The message never contains text, values or positions from the statement. Its error code is
/// <see cref="QueryErrorCode.NestedTooDeeply"/>.
/// </remarks>
public sealed class QueryNestedTooDeeplyException : QueryExecutionException
{
    /// <summary>The message every surface shows for a refused statement.</summary>
    public const string DefaultMessage =
        "The statement is nested too deeply or is too large to process safely. " +
        "Split it into smaller statements, or use an IN list instead of a long chain of OR conditions.";

    /// <summary>Which limit refused the statement.</summary>
    public QueryDepthLimit Limit { get; }

    /// <summary>
    /// Creates the exception with <see cref="DefaultMessage"/>.
    /// </summary>
    /// <param name="limit">Which limit refused the statement.</param>
    public QueryNestedTooDeeplyException(QueryDepthLimit limit)
        : this(limit, DefaultMessage)
    {
    }

    /// <summary>
    /// Creates the exception with a message that adds to <see cref="DefaultMessage"/>, for example
    /// to name the statements of a script that had already run.
    /// </summary>
    /// <param name="limit">Which limit refused the statement.</param>
    /// <param name="message">The full message. It must not contain text from the statement.</param>
    /// <param name="inner">The refusal this one wraps, if any.</param>
    public QueryNestedTooDeeplyException(QueryDepthLimit limit, string message, Exception? inner = null)
        : base(QueryErrorCode.NestedTooDeeply, message, inner)
    {
        Limit = limit;
    }
}
