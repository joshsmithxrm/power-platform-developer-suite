using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure.Output;

/// <summary>
/// Interface for writing command results to output.
/// Implementations handle formatting for different output modes (text, JSON, RPC).
/// </summary>
/// <remarks>
/// <para>
/// Results are written to stdout, while logs go to stderr. This separation
/// enables piping command output to other tools (e.g., jq) without interference
/// from operational messages.
/// </para>
/// <para>
/// Implementations should respect the NO_COLOR environment variable and
/// handle output redirection appropriately.
/// </para>
/// </remarks>
public interface IOutputWriter
{
    /// <summary>
    /// Gets whether this writer is in debug mode (shows full error details).
    /// </summary>
    bool DebugMode { get; }

    /// <summary>
    /// Gets whether this writer outputs JSON format.
    /// </summary>
    bool IsJsonMode { get; }

    /// <summary>
    /// Writes a complete command result.
    /// </summary>
    /// <typeparam name="T">The type of the result data.</typeparam>
    /// <param name="result">The command result to write.</param>
    void WriteResult<T>(CommandResult<T> result);

    /// <summary>
    /// Writes a successful result with data.
    /// </summary>
    /// <typeparam name="T">The type of the data.</typeparam>
    /// <param name="data">The data to write.</param>
    void WriteSuccess<T>(T data);

    /// <summary>
    /// Writes an error result.
    /// </summary>
    /// <param name="error">The structured error to write.</param>
    void WriteError(StructuredError error);

    /// <summary>
    /// Writes a partial success result with both data and item-level results.
    /// </summary>
    /// <typeparam name="T">The type of the data.</typeparam>
    /// <param name="data">The partial result data.</param>
    /// <param name="results">The individual item results.</param>
    /// <param name="error">
    /// Optional summary error. The JSON writer emits it as the standard top-level <c>error</c> so consumers that
    /// read <c>.error</c> on <c>success: false</c> keep working.
    /// </param>
    void WritePartialSuccess<T>(T data, IEnumerable<ItemResult> results, StructuredError? error = null);

    /// <summary>
    /// Writes a simple message. In JSON mode, this becomes a structured message object.
    /// </summary>
    /// <param name="message">The message to write.</param>
    void WriteMessage(string message);

    /// <summary>
    /// Writes a warning message.
    /// </summary>
    /// <param name="message">The warning message to write.</param>
    void WriteWarning(string message);
}
