using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Result of cursor context analysis for SQL IntelliSense.
/// Describes what kind of completion is appropriate at the cursor position.
/// </summary>
public sealed class SqlCursorContextResult
{
    /// <summary>
    /// The kind of completion appropriate at this cursor position.
    /// </summary>
    public SqlCompletionContextKind Kind { get; init; }

    /// <summary>
    /// Map of alias to entity logical name for all tables in scope.
    /// Keys are aliases (or table names if no alias); values are entity logical names.
    /// </summary>
    public IReadOnlyDictionary<string, string> AliasMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The entity logical name for attribute lookup when <see cref="Kind"/> is <see cref="SqlCompletionContextKind.Attribute"/>.
    /// Null means attributes from all in-scope entities should be shown.
    /// </summary>
    public string? CurrentEntity { get; init; }

    /// <summary>
    /// Context-filtered keyword suggestions when <see cref="Kind"/> is <see cref="SqlCompletionContextKind.Keyword"/>.
    /// </summary>
    public IReadOnlyList<string>? KeywordSuggestions { get; init; }

    /// <summary>
    /// The text prefix the user has already typed at the cursor position (for filtering).
    /// </summary>
    public string Prefix { get; init; } = "";

    /// <summary>
    /// Creates a result indicating no completions are available.
    /// </summary>
    public static SqlCursorContextResult None() => new() { Kind = SqlCompletionContextKind.None };
}
