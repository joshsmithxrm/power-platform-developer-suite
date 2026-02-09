using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Sql.Intellisense;

namespace PPDS.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ISqlLanguageService"/>.
/// Composes <see cref="SqlSourceTokenizer"/> for syntax highlighting
/// and <see cref="SqlCompletionEngine"/> for IntelliSense completions.
/// </summary>
public sealed class SqlLanguageService : ISqlLanguageService
{
    private readonly SqlSourceTokenizer _tokenizer = new();
    private readonly SqlCompletionEngine _completionEngine;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlLanguageService"/> class.
    /// </summary>
    /// <param name="metadataProvider">
    /// Cached metadata provider for entity/attribute lookups.
    /// May be null if no environment is connected (completions will return keywords only).
    /// </param>
    public SqlLanguageService(ICachedMetadataProvider? metadataProvider)
    {
        if (metadataProvider != null)
        {
            _completionEngine = new SqlCompletionEngine(metadataProvider);
        }
        else
        {
            // No metadata provider — engine will be null, completions limited to keywords
            _completionEngine = null!;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceToken> Tokenize(string sql)
    {
        return _tokenizer.Tokenize(sql);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SqlCompletion>> GetCompletionsAsync(
        string sql, int cursorOffset, CancellationToken ct = default)
    {
        if (_completionEngine == null)
        {
            // No metadata available — return keyword-only completions
            var context = SqlCursorContext.Analyze(sql, cursorOffset);
            if (context.Kind == SqlCompletionContextKind.Keyword && context.KeywordSuggestions != null)
            {
                var keywords = new List<SqlCompletion>();
                var sortOrder = 0;
                foreach (var kw in context.KeywordSuggestions)
                {
                    keywords.Add(new SqlCompletion(kw, kw, SqlCompletionKind.Keyword, SortOrder: sortOrder++));
                }
                return keywords;
            }
            return Array.Empty<SqlCompletion>();
        }

        return await _completionEngine.GetCompletionsAsync(sql, cursorOffset, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SqlDiagnostic>> ValidateAsync(string sql, CancellationToken ct = default)
    {
        // Phase 5 will implement full validation.
        // For now, return empty diagnostics.
        return Task.FromResult<IReadOnlyList<SqlDiagnostic>>(Array.Empty<SqlDiagnostic>());
    }
}
