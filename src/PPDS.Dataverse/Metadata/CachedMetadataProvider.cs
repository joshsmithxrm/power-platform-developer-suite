using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Provides cached access to Dataverse metadata, wrapping <see cref="IMetadataService"/>
/// with per-session caching to minimize round-trips.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Caching Strategy:</strong>
/// </para>
/// <list type="bullet">
/// <item><strong>Entity list:</strong> Loaded eagerly via <see cref="PreloadAsync"/> and cached
/// indefinitely for the session lifetime.</item>
/// <item><strong>Attributes per entity:</strong> Lazy-loaded on first access, cached with a
/// 5-minute TTL. After expiry, the next access re-fetches from Dataverse.</item>
/// <item><strong>Relationships per entity:</strong> Same as attributes — lazy, 5-minute TTL.</item>
/// </list>
/// <para>
/// <strong>Thread Safety:</strong> Uses <see cref="ConcurrentDictionary{TKey,TValue}"/> for cache
/// storage and <see cref="SemaphoreSlim"/> for load coordination to prevent duplicate fetches
/// when multiple callers request the same data concurrently.
/// </para>
/// </remarks>
public sealed class CachedMetadataProvider : ICachedMetadataProvider, IDisposable
{
    private readonly IMetadataService _metadataService;
    private readonly TimeSpan _ttl;
    private readonly ITimeProvider _timeProvider;

    // Entity list cache — loaded by PreloadAsync, indefinite TTL
    private IReadOnlyList<EntitySummary>? _entities;
    private readonly SemaphoreSlim _entityLock = new(1, 1);

    // Per-entity caches with TTL
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<AttributeMetadataDto>>> _attributeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry<EntityRelationshipsDto>> _relationshipCache = new(StringComparer.OrdinalIgnoreCase);

    // Per-entity load coordination to prevent concurrent duplicate fetches
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _attributeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _relationshipLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new <see cref="CachedMetadataProvider"/>.
    /// </summary>
    /// <param name="metadataService">The underlying metadata service.</param>
    public CachedMetadataProvider(IMetadataService metadataService)
        : this(metadataService, TimeSpan.FromMinutes(5), SystemTimeProvider.Instance)
    {
    }

    /// <summary>
    /// Creates a new <see cref="CachedMetadataProvider"/> with configurable TTL and time provider.
    /// Use this constructor in tests to control time and TTL.
    /// </summary>
    /// <param name="metadataService">The underlying metadata service.</param>
    /// <param name="ttl">Time-to-live for per-entity caches.</param>
    /// <param name="timeProvider">Time provider for cache expiry checks.</param>
    public CachedMetadataProvider(IMetadataService metadataService, TimeSpan ttl, ITimeProvider timeProvider)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _ttl = ttl;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default)
    {
        // Fast path: already cached
        var cached = _entities;
        if (cached != null)
            return cached;

        // Slow path: load with coordination
        await _entityLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after lock — CodeQL [cs/constant-condition] this is the standard double-check-lock pattern
            cached = _entities;
            if (cached != null)
                return cached;

            var entities = await _metadataService.GetEntitiesAsync(cancellationToken: ct).ConfigureAwait(false);
            _entities = entities;
            return entities;
        }
        finally
        {
            _entityLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(string entityLogicalName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        // Fast path: cached and not expired
        if (_attributeCache.TryGetValue(entityLogicalName, out var entry) && !entry.IsExpired(_timeProvider, _ttl))
            return entry.Value;

        // Slow path: load with per-entity coordination
        var semaphore = _attributeLocks.GetOrAdd(entityLogicalName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after lock
            if (_attributeCache.TryGetValue(entityLogicalName, out entry) && !entry.IsExpired(_timeProvider, _ttl))
                return entry.Value;

            var attributes = await _metadataService.GetAttributesAsync(entityLogicalName, cancellationToken: ct).ConfigureAwait(false);
            _attributeCache[entityLogicalName] = new CacheEntry<IReadOnlyList<AttributeMetadataDto>>(attributes, _timeProvider.UtcNow);
            return attributes;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EntityRelationshipsDto> GetRelationshipsAsync(string entityLogicalName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        // Fast path: cached and not expired
        if (_relationshipCache.TryGetValue(entityLogicalName, out var entry) && !entry.IsExpired(_timeProvider, _ttl))
            return entry.Value;

        // Slow path: load with per-entity coordination
        var semaphore = _relationshipLocks.GetOrAdd(entityLogicalName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after lock
            if (_relationshipCache.TryGetValue(entityLogicalName, out entry) && !entry.IsExpired(_timeProvider, _ttl))
                return entry.Value;

            var relationships = await _metadataService.GetRelationshipsAsync(entityLogicalName, cancellationToken: ct).ConfigureAwait(false);
            _relationshipCache[entityLogicalName] = new CacheEntry<EntityRelationshipsDto>(relationships, _timeProvider.UtcNow);
            return relationships;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task PreloadAsync(CancellationToken ct = default)
    {
        await _entityLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entities = await _metadataService.GetEntitiesAsync(cancellationToken: ct).ConfigureAwait(false);
            _entities = entities;
        }
        finally
        {
            _entityLock.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        _entities = null;
        _attributeCache.Clear();
        _relationshipCache.Clear();

        // Clear semaphore dictionaries — old semaphores will be GC'd
        // after any in-flight operations complete. Do NOT dispose them here
        // as concurrent GetAttributesAsync/GetRelationshipsAsync calls may
        // still hold references.
        _attributeLocks.Clear();
        _relationshipLocks.Clear();
    }

    /// <inheritdoc />
    public void InvalidateEntity(string entityLogicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        _attributeCache.TryRemove(entityLogicalName, out _);
        _relationshipCache.TryRemove(entityLogicalName, out _);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _entityLock.Dispose();

        foreach (var semaphore in _attributeLocks.Values)
            semaphore.Dispose();

        foreach (var semaphore in _relationshipLocks.Values)
            semaphore.Dispose();
    }

    /// <summary>
    /// Abstraction over time for testability of TTL expiry.
    /// </summary>
    public interface ITimeProvider
    {
        /// <summary>
        /// Gets the current UTC time.
        /// </summary>
        DateTimeOffset UtcNow { get; }
    }

    /// <summary>
    /// Default time provider using <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    private sealed class SystemTimeProvider : ITimeProvider
    {
        public static readonly SystemTimeProvider Instance = new();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// A cached value with its creation timestamp for TTL checks.
    /// </summary>
    private readonly struct CacheEntry<T>
    {
        public readonly T Value;
        public readonly DateTimeOffset CreatedAt;

        public CacheEntry(T value, DateTimeOffset createdAt)
        {
            Value = value;
            CreatedAt = createdAt;
        }

        public bool IsExpired(ITimeProvider timeProvider, TimeSpan ttl)
            => timeProvider.UtcNow - CreatedAt >= ttl;
    }
}
