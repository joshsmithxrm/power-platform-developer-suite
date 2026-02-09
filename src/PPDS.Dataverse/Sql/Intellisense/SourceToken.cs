namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// A token with position and classification for syntax highlighting.
/// Language-agnostic — the <see cref="SourceTokenType"/> determines rendering color.
/// </summary>
/// <param name="Start">Character offset where the token begins in the source text.</param>
/// <param name="Length">Number of characters in the token.</param>
/// <param name="Type">The classification of the token for highlighting purposes.</param>
public readonly record struct SourceToken(int Start, int Length, SourceTokenType Type);
