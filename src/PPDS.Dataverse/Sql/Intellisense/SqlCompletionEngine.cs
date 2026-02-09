using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata;

namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Produces completion items for SQL IntelliSense by combining cursor context
/// analysis with Dataverse metadata from <see cref="ICachedMetadataProvider"/>.
/// </summary>
public sealed class SqlCompletionEngine
{
    private readonly ICachedMetadataProvider _metadataProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlCompletionEngine"/> class.
    /// </summary>
    /// <param name="metadataProvider">Cached metadata provider for entity/attribute lookups.</param>
    public SqlCompletionEngine(ICachedMetadataProvider metadataProvider)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
    }

    /// <summary>
    /// Gets completion items for the given SQL text at the specified cursor offset.
    /// </summary>
    /// <param name="sql">The SQL text being edited.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of completion items sorted by relevance.</returns>
    public async Task<IReadOnlyList<SqlCompletion>> GetCompletionsAsync(
        string sql, int cursorOffset, CancellationToken cancellationToken = default)
    {
        var context = SqlCursorContext.Analyze(sql, cursorOffset);
        var completions = new List<SqlCompletion>();

        switch (context.Kind)
        {
            case SqlCompletionContextKind.Keyword:
                completions.AddRange(GetKeywordCompletions(context));
                break;

            case SqlCompletionContextKind.Entity:
                completions.AddRange(await GetEntityCompletionsAsync(context, cancellationToken));
                break;

            case SqlCompletionContextKind.Attribute:
                completions.AddRange(await GetAttributeCompletionsAsync(context, cancellationToken));
                break;

            case SqlCompletionContextKind.None:
                break;
        }

        // Filter by prefix if present
        if (!string.IsNullOrEmpty(context.Prefix))
        {
            completions = completions
                .Where(c => c.Label.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Sort: by SortOrder, then alphabetically
        completions.Sort((a, b) =>
        {
            var cmp = a.SortOrder.CompareTo(b.SortOrder);
            return cmp != 0 ? cmp : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });

        return completions;
    }

    #region Keyword Completions

    private static IEnumerable<SqlCompletion> GetKeywordCompletions(SqlCursorContextResult context)
    {
        if (context.KeywordSuggestions == null)
            yield break;

        var sortOrder = 0;
        foreach (var keyword in context.KeywordSuggestions)
        {
            yield return new SqlCompletion(
                Label: keyword,
                InsertText: keyword,
                Kind: SqlCompletionKind.Keyword,
                SortOrder: sortOrder++);
        }
    }

    #endregion

    #region Entity Completions

    private async Task<IEnumerable<SqlCompletion>> GetEntityCompletionsAsync(
        SqlCursorContextResult context, CancellationToken ct)
    {
        var entities = await _metadataProvider.GetEntitiesAsync(ct);
        var completions = new List<SqlCompletion>(entities.Count);

        foreach (var entity in entities)
        {
            var description = !string.IsNullOrEmpty(entity.DisplayName)
                ? entity.DisplayName
                : null;

            completions.Add(new SqlCompletion(
                Label: entity.LogicalName,
                InsertText: entity.LogicalName,
                Kind: SqlCompletionKind.Entity,
                Description: description,
                Detail: entity.IsCustomEntity ? "Custom" : "System",
                SortOrder: entity.IsCustomEntity ? 0 : 1));
        }

        return completions;
    }

    #endregion

    #region Attribute Completions

    private async Task<IEnumerable<SqlCompletion>> GetAttributeCompletionsAsync(
        SqlCursorContextResult context, CancellationToken ct)
    {
        var completions = new List<SqlCompletion>();

        if (context.CurrentEntity != null)
        {
            // Specific entity — get its attributes
            var attributes = await _metadataProvider.GetAttributesAsync(context.CurrentEntity, ct);
            completions.AddRange(attributes.Select(attr => CreateAttributeCompletion(attr)));
        }
        else
        {
            // All in-scope entities
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in context.AliasMap)
            {
                var entityName = kvp.Value;
                if (!seen.Add(entityName))
                    continue;

                try
                {
                    var attributes = await _metadataProvider.GetAttributesAsync(entityName, ct);
                    completions.AddRange(attributes.Select(attr => CreateAttributeCompletion(attr)));
                }
                catch (OperationCanceledException)
                {
                    throw; // Respect cancellation
                }
                catch (Exception)
                {
                    // Skip entities where metadata lookup fails (e.g. network error)
                }
            }
        }

        return completions;
    }

    private static SqlCompletion CreateAttributeCompletion(Metadata.Models.AttributeMetadataDto attr)
    {
        var description = !string.IsNullOrEmpty(attr.DisplayName)
            ? attr.DisplayName
            : null;

        var sortOrder = attr.IsPrimaryId ? 0
            : attr.IsPrimaryName ? 1
            : attr.IsCustomAttribute ? 2
            : 3;

        return new SqlCompletion(
            Label: attr.LogicalName,
            InsertText: attr.LogicalName,
            Kind: SqlCompletionKind.Attribute,
            Description: description,
            Detail: attr.AttributeType,
            SortOrder: sortOrder);
    }

    #endregion
}
