using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Produces source tokens for syntax highlighting.
/// Language-agnostic interface — implementations provide language-specific tokenization.
/// </summary>
public interface ISourceTokenizer
{
    /// <summary>
    /// Tokenizes the given text into source tokens for syntax highlighting.
    /// Must not throw — invalid input should produce Error tokens.
    /// </summary>
    /// <param name="text">The source text to tokenize.</param>
    /// <returns>An ordered list of tokens covering the entire input text.</returns>
    IReadOnlyList<SourceToken> Tokenize(string text);
}
