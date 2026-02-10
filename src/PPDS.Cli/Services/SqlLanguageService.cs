using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Sql.Intellisense;
using SqlCompletionEngine = PPDS.Query.Intellisense.SqlCompletionEngine;
using SqlCursorContext = PPDS.Query.Intellisense.SqlCursorContext;
using SqlSourceTokenizer = PPDS.Query.Intellisense.SqlSourceTokenizer;
using SqlValidator = PPDS.Query.Intellisense.SqlValidator;

namespace PPDS.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ISqlLanguageService"/>.
/// Composes <see cref="SqlSourceTokenizer"/> for syntax highlighting,
/// <see cref="SqlCompletionEngine"/> for IntelliSense completions,
/// and <see cref="SqlValidator"/> for diagnostics.
/// </summary>
public sealed class SqlLanguageService : ISqlLanguageService
{
    private readonly SqlSourceTokenizer _tokenizer = new();
    private readonly SqlCompletionEngine? _completionEngine;
    private readonly SqlValidator _validator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlLanguageService"/> class.
    /// </summary>
    /// <param name="metadataProvider">
    /// Cached metadata provider for entity/attribute lookups.
    /// May be null if no environment is connected (completions will return keywords only).
    /// </param>
    public SqlLanguageService(ICachedMetadataProvider? metadataProvider)
    {
        _validator = new SqlValidator(metadataProvider);
        _completionEngine = metadataProvider != null
            ? new SqlCompletionEngine(metadataProvider)
            : null;
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

        try
        {
            return await _completionEngine.GetCompletionsAsync(sql, cursorOffset, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PpdsException(
                ErrorCodes.Query.CompletionFailed,
                "Failed to retrieve IntelliSense completions.",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SqlDiagnostic>> ValidateAsync(string sql, CancellationToken ct = default)
    {
        try
        {
            return await _validator.ValidateAsync(sql, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(
                ErrorCodes.Query.ValidationFailed,
                "Failed to validate SQL.",
                ex);
        }
    }
}
