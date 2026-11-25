using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Resolves lookup field values by querying target entities.
/// </summary>
public sealed class LookupResolver
{
    private readonly IDataverseConnectionPool _pool;
    private readonly Dictionary<string, LookupCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoadError> _errors = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LookupResolver"/> class.
    /// </summary>
    public LookupResolver(IDataverseConnectionPool pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    /// <summary>
    /// Gets errors encountered during lookup resolution.
    /// </summary>
    public IReadOnlyList<LoadError> Errors => _errors;

    /// <summary>
    /// Preloads lookup caches for the specified lookup configurations.
    /// </summary>
    /// <param name="lookups">Lookup configurations to preload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PreloadLookupsAsync(
        IEnumerable<(string ColumnName, LookupConfig Config)> lookups,
        CancellationToken cancellationToken = default)
    {
        // Group by entity:keyField to avoid duplicate queries
        var groupedByEntity = lookups
            .Where(l => l.Config.MatchBy == "field" && !string.IsNullOrEmpty(l.Config.KeyField))
            .GroupBy(l => $"{l.Config.Entity}:{l.Config.KeyField}".ToLowerInvariant());

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        foreach (var group in groupedByEntity)
        {
            var cacheKey = group.Key;
            var first = group.First();
            var entityName = first.Config.Entity;
            var keyField = first.Config.KeyField!;

            if (_caches.ContainsKey(cacheKey))
            {
                continue;
            }

            var cache = await LoadLookupCacheAsync(client, entityName, keyField, cancellationToken);
            _caches[cacheKey] = cache;
        }
    }

    /// <summary>
    /// Resolves a lookup value to an EntityReference.
    /// </summary>
    /// <param name="value">The value to resolve.</param>
    /// <param name="config">The lookup configuration.</param>
    /// <param name="rowNumber">Row number for error reporting.</param>
    /// <param name="columnName">Column name for error reporting.</param>
    /// <returns>The resolved EntityReference, or null if not resolved.</returns>
    public EntityReference? Resolve(
        string? value,
        LookupConfig config,
        int rowNumber,
        string columnName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // matchBy guid: the record's GUID, read by the record id rule of the data commands (#1915, #1916).
        if (config.MatchBy == "guid")
        {
            if (RecordIdText.TryParse(value, out var guid))
            {
                return new EntityReference(config.Entity, guid);
            }

            return Fail(rowNumber, columnName, LoadErrorCodes.LookupNotResolved,
                $"The lookup to '{config.Entity}' takes the record's GUID: {RecordIdText.Rule}; not the empty (all-zero) GUID. " +
                "To match by another column, set matchBy: 'field' and keyField in the mapping file.");
        }

        // matchBy field: the key field only. A value shaped like a GUID is not taken as a GUID.
        if (config.MatchBy == "field" && !string.IsNullOrEmpty(config.KeyField))
        {
            var cacheKey = $"{config.Entity}:{config.KeyField}";

            if (_caches.TryGetValue(cacheKey, out var cache))
            {
                return ResolveFromCache(value, cache, config, rowNumber, columnName);
            }

            return Fail(rowNumber, columnName, LoadErrorCodes.LookupNotResolved,
                $"The lookup values of {config.Entity}.{config.KeyField} were not loaded.");
        }

        return Fail(rowNumber, columnName, LoadErrorCodes.LookupNotResolved,
            "The lookup has no usable configuration: set matchBy to 'guid', or to 'field' with a keyField.");
    }

    private EntityReference? Fail(int rowNumber, string columnName, string code, string message)
    {
        // The cell's content is not kept: errors name the row and column only.
        _errors.Add(new LoadError { RowNumber = rowNumber, Column = columnName, ErrorCode = code, Message = message });
        return null;
    }

    private EntityReference? ResolveFromCache(
        string value,
        LookupCache cache,
        LookupConfig config,
        int rowNumber,
        string columnName)
    {
        if (cache.SingleMatches.TryGetValue(value, out var id))
        {
            return new EntityReference(config.Entity, id);
        }

        if (cache.Duplicates.TryGetValue(value, out var duplicateIds))
        {
            return Fail(rowNumber, columnName, LoadErrorCodes.LookupDuplicate,
                $"{duplicateIds.Count} {config.Entity} records have this {config.KeyField}, so the record is not certain.");
        }

        return Fail(rowNumber, columnName, LoadErrorCodes.LookupNotResolved,
            $"No {config.Entity} record has this {config.KeyField}.");
    }

    private async Task<LookupCache> LoadLookupCacheAsync(
        IPooledClient client,
        string entityName,
        string keyField,
        CancellationToken cancellationToken)
    {
        var cache = new LookupCache();

        var query = new QueryExpression(entityName)
        {
            ColumnSet = new ColumnSet(keyField),
            PageInfo = new PagingInfo
            {
                Count = 5000,
                PageNumber = 1
            }
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await client.RetrieveMultipleAsync(query, cancellationToken);

            foreach (var entity in response.Entities)
            {
                var keyValue = GetKeyValue(entity, keyField);
                if (string.IsNullOrEmpty(keyValue))
                {
                    continue;
                }

                if (cache.SingleMatches.TryGetValue(keyValue, out var existingId))
                {
                    // Duplicate found - move to duplicates
                    cache.SingleMatches.Remove(keyValue);
                    cache.Duplicates[keyValue] = new List<Guid> { existingId, entity.Id };
                }
                else if (cache.Duplicates.TryGetValue(keyValue, out var duplicates))
                {
                    // Add to existing duplicates
                    duplicates.Add(entity.Id);
                }
                else
                {
                    // First occurrence
                    cache.SingleMatches[keyValue] = entity.Id;
                }
            }

            if (!response.MoreRecords)
            {
                break;
            }

            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = response.PagingCookie;
        }

        return cache;
    }

    private static string? GetKeyValue(Entity entity, string keyField)
    {
        if (!entity.Contains(keyField))
        {
            return null;
        }

        var value = entity[keyField];
        if (value is null)
        {
            return null;
        }

        // Only text is compared: the loader has refused a key field that is not a text column, and any other value would
        // be turned into text in the machine's culture (#1916).
        return value as string;
    }

    /// <summary>
    /// Clears all loaded caches.
    /// </summary>
    public void ClearCaches()
    {
        _caches.Clear();
        _errors.Clear();
    }

    private sealed class LookupCache
    {
        public Dictionary<string, Guid> SingleMatches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<Guid>> Duplicates { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
