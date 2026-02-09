using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Sql.Intellisense;

namespace PPDS.Cli.Services;

/// <summary>
/// Application service providing SQL language features for the TUI editor:
/// tokenization (syntax highlighting), completions (IntelliSense), and validation (diagnostics).
/// </summary>
/// <remarks>
/// Wraps <see cref="SqlSourceTokenizer"/> and <see cref="SqlCompletionEngine"/>
/// into a single service interface shared by CLI, TUI, and RPC consumers.
/// </remarks>
public interface ISqlLanguageService
{
    /// <summary>
    /// Tokenizes SQL text for syntax highlighting.
    /// Never throws — invalid input produces Error tokens.
    /// </summary>
    /// <param name="sql">The SQL text to tokenize.</param>
    /// <returns>Ordered list of source tokens covering the input.</returns>
    IReadOnlyList<SourceToken> Tokenize(string sql);

    /// <summary>
    /// Gets IntelliSense completion items at the given cursor position.
    /// </summary>
    /// <param name="sql">The SQL text being edited.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of completion items sorted by relevance.</returns>
    Task<IReadOnlyList<SqlCompletion>> GetCompletionsAsync(
        string sql, int cursorOffset, CancellationToken ct = default);

    /// <summary>
    /// Validates SQL text and returns diagnostics (errors, warnings, hints).
    /// </summary>
    /// <param name="sql">The SQL text to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of diagnostics. Empty if the SQL is valid.</returns>
    Task<IReadOnlyList<SqlDiagnostic>> ValidateAsync(string sql, CancellationToken ct = default);
}
