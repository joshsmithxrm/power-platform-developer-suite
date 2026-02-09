using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

/// <summary>
/// Unit tests for <see cref="CachedMetadataProvider"/>.
/// Uses inline test doubles for <see cref="IMetadataService"/> and
/// <see cref="CachedMetadataProvider.ITimeProvider"/> to test caching,
/// TTL expiry, invalidation, and thread safety.
/// </summary>
public class CachedMetadataProviderTests
{
    #region Test Infrastructure

    /// <summary>
    /// Stub metadata service that tracks call counts per method.
    /// </summary>
    private sealed class StubMetadataService : IMetadataService
    {
        private int _getEntitiesCount;
        private readonly Dictionary<string, int> _getAttributesCount = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _getRelationshipsCount = new(StringComparer.OrdinalIgnoreCase);

        public int GetEntitiesCallCount => _getEntitiesCount;
        public int GetAttributesCallCount(string entity) => _getAttributesCount.GetValueOrDefault(entity);
        public int GetRelationshipsCallCount(string entity) => _getRelationshipsCount.GetValueOrDefault(entity);

        /// <summary>
        /// Optional delay to simulate network latency for concurrency tests.
        /// </summary>
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(
            bool customOnly = false, string? filter = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getEntitiesCount);
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            return new List<EntitySummary>
            {
                new() { LogicalName = "account", DisplayName = "Account", SchemaName = "Account" },
                new() { LogicalName = "contact", DisplayName = "Contact", SchemaName = "Contact" },
            };
        }

        public Task<EntityMetadataDto> GetEntityAsync(
            string logicalName, bool includeAttributes = true, bool includeRelationships = true,
            bool includeKeys = true, bool includePrivileges = true, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(
            string entityLogicalName, string? attributeType = null, CancellationToken cancellationToken = default)
        {
            lock (_getAttributesCount)
            {
                _getAttributesCount[entityLogicalName] = _getAttributesCount.GetValueOrDefault(entityLogicalName) + 1;
            }
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            return new List<AttributeMetadataDto>
            {
                new() { LogicalName = "name", DisplayName = "Name", SchemaName = "Name", AttributeType = "String" },
                new() { LogicalName = $"{entityLogicalName}id", DisplayName = "ID", SchemaName = "Id", AttributeType = "Uniqueidentifier" },
            };
        }

        public async Task<EntityRelationshipsDto> GetRelationshipsAsync(
            string entityLogicalName, string? relationshipType = null, CancellationToken cancellationToken = default)
        {
            lock (_getRelationshipsCount)
            {
                _getRelationshipsCount[entityLogicalName] = _getRelationshipsCount.GetValueOrDefault(entityLogicalName) + 1;
            }
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            return new EntityRelationshipsDto { EntityLogicalName = entityLogicalName };
        }

        public Task<IReadOnlyList<OptionSetSummary>> GetGlobalOptionSetsAsync(
            string? filter = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<OptionSetMetadataDto> GetOptionSetAsync(
            string name, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<EntityKeyDto>> GetKeysAsync(
            string entityLogicalName, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Controllable time provider for TTL expiry testing.
    /// </summary>
    private sealed class FakeTimeProvider : CachedMetadataProvider.ITimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private static CachedMetadataProvider CreateProvider(
        StubMetadataService service,
        FakeTimeProvider? time = null,
        TimeSpan? ttl = null)
    {
        return new CachedMetadataProvider(
            service,
            ttl ?? TimeSpan.FromMinutes(5),
            time ?? new FakeTimeProvider());
    }

    #endregion

    #region PreloadAsync Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task PreloadAsync_PopulatesEntityCache()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.PreloadAsync();

        var entities = await provider.GetEntitiesAsync();
        Assert.Equal(2, entities.Count);
        Assert.Equal("account", entities[0].LogicalName);
        Assert.Equal("contact", entities[1].LogicalName);
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task PreloadAsync_OnlyCallsServiceOnce()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.PreloadAsync();
        _ = await provider.GetEntitiesAsync();
        _ = await provider.GetEntitiesAsync();

        Assert.Equal(1, service.GetEntitiesCallCount);
    }

    #endregion

    #region GetEntitiesAsync Caching Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetEntitiesAsync_WithoutPreload_LoadsOnFirstCall()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        var entities = await provider.GetEntitiesAsync();

        Assert.Equal(2, entities.Count);
        Assert.Equal(1, service.GetEntitiesCallCount);
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetEntitiesAsync_SecondCallReturnsCachedData()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        var first = await provider.GetEntitiesAsync();
        var second = await provider.GetEntitiesAsync();

        Assert.Same(first, second);
        Assert.Equal(1, service.GetEntitiesCallCount);
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetEntitiesAsync_CachedIndefinitely_DoesNotExpire()
    {
        var service = new StubMetadataService();
        var time = new FakeTimeProvider();
        using var provider = CreateProvider(service, time);

        await provider.PreloadAsync();

        // Advance far beyond the attribute TTL
        time.Advance(TimeSpan.FromHours(1));

        _ = await provider.GetEntitiesAsync();

        // Still only 1 call — entity cache never expires
        Assert.Equal(1, service.GetEntitiesCallCount);
    }

    #endregion

    #region GetAttributesAsync Caching Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_LoadsOnFirstCall()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        var attrs = await provider.GetAttributesAsync("account");

        Assert.Equal(2, attrs.Count);
        Assert.Equal(1, service.GetAttributesCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_SecondCallReturnsCachedData()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        var first = await provider.GetAttributesAsync("account");
        var second = await provider.GetAttributesAsync("account");

        Assert.Same(first, second);
        Assert.Equal(1, service.GetAttributesCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_DifferentEntities_CachedSeparately()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.GetAttributesAsync("account");
        await provider.GetAttributesAsync("contact");

        Assert.Equal(1, service.GetAttributesCallCount("account"));
        Assert.Equal(1, service.GetAttributesCallCount("contact"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_CaseInsensitiveEntityName()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.GetAttributesAsync("Account");
        await provider.GetAttributesAsync("ACCOUNT");
        await provider.GetAttributesAsync("account");

        // All map to the same cache key
        Assert.Equal(1, service.GetAttributesCallCount("Account"));
    }

    #endregion

    #region GetRelationshipsAsync Caching Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetRelationshipsAsync_SecondCallReturnsCachedData()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        var first = await provider.GetRelationshipsAsync("account");
        var second = await provider.GetRelationshipsAsync("account");

        Assert.Same(first, second);
        Assert.Equal(1, service.GetRelationshipsCallCount("account"));
    }

    #endregion

    #region TTL Expiry Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_AfterTtlExpiry_RefetchesFromService()
    {
        var service = new StubMetadataService();
        var time = new FakeTimeProvider();
        using var provider = CreateProvider(service, time, ttl: TimeSpan.FromMinutes(5));

        // First call — populates cache
        await provider.GetAttributesAsync("account");
        Assert.Equal(1, service.GetAttributesCallCount("account"));

        // Before TTL — still cached
        time.Advance(TimeSpan.FromMinutes(4));
        await provider.GetAttributesAsync("account");
        Assert.Equal(1, service.GetAttributesCallCount("account"));

        // After TTL — re-fetched
        time.Advance(TimeSpan.FromMinutes(2)); // Total: 6 minutes
        await provider.GetAttributesAsync("account");
        Assert.Equal(2, service.GetAttributesCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetRelationshipsAsync_AfterTtlExpiry_RefetchesFromService()
    {
        var service = new StubMetadataService();
        var time = new FakeTimeProvider();
        using var provider = CreateProvider(service, time, ttl: TimeSpan.FromMinutes(5));

        await provider.GetRelationshipsAsync("account");
        Assert.Equal(1, service.GetRelationshipsCallCount("account"));

        // After TTL
        time.Advance(TimeSpan.FromMinutes(6));
        await provider.GetRelationshipsAsync("account");
        Assert.Equal(2, service.GetRelationshipsCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_JustBeforeTtl_StillCached()
    {
        var service = new StubMetadataService();
        var time = new FakeTimeProvider();
        using var provider = CreateProvider(service, time, ttl: TimeSpan.FromMinutes(5));

        await provider.GetAttributesAsync("account");

        // 4 minutes 59 seconds — still within TTL
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await provider.GetAttributesAsync("account");
        Assert.Equal(1, service.GetAttributesCallCount("account"));
    }

    #endregion

    #region InvalidateAll Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task InvalidateAll_ClearsEntityCache()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.PreloadAsync();
        Assert.Equal(1, service.GetEntitiesCallCount);

        provider.InvalidateAll();

        await provider.GetEntitiesAsync();
        Assert.Equal(2, service.GetEntitiesCallCount);
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task InvalidateAll_ClearsAttributeCache()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.GetAttributesAsync("account");
        Assert.Equal(1, service.GetAttributesCallCount("account"));

        provider.InvalidateAll();

        await provider.GetAttributesAsync("account");
        Assert.Equal(2, service.GetAttributesCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task InvalidateAll_ClearsRelationshipCache()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.GetRelationshipsAsync("account");
        Assert.Equal(1, service.GetRelationshipsCallCount("account"));

        provider.InvalidateAll();

        await provider.GetRelationshipsAsync("account");
        Assert.Equal(2, service.GetRelationshipsCallCount("account"));
    }

    #endregion

    #region InvalidateEntity Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task InvalidateEntity_ClearsOnlyThatEntity()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.GetAttributesAsync("account");
        await provider.GetAttributesAsync("contact");

        provider.InvalidateEntity("account");

        // account re-fetched, contact still cached
        await provider.GetAttributesAsync("account");
        await provider.GetAttributesAsync("contact");

        Assert.Equal(2, service.GetAttributesCallCount("account"));
        Assert.Equal(1, service.GetAttributesCallCount("contact"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task InvalidateEntity_ClearsRelationshipsForEntity()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.GetRelationshipsAsync("account");

        provider.InvalidateEntity("account");

        await provider.GetRelationshipsAsync("account");
        Assert.Equal(2, service.GetRelationshipsCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task InvalidateEntity_DoesNotClearEntityList()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.PreloadAsync();
        Assert.Equal(1, service.GetEntitiesCallCount);

        provider.InvalidateEntity("account");

        // Entity list should still be cached
        await provider.GetEntitiesAsync();
        Assert.Equal(1, service.GetEntitiesCallCount);
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public void InvalidateEntity_NonExistentEntity_DoesNotThrow()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        // Should not throw when invalidating an entity not in cache
        provider.InvalidateEntity("nonexistent");
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetEntitiesAsync_ConcurrentCalls_OnlySingleServiceCall()
    {
        var service = new StubMetadataService { Delay = TimeSpan.FromMilliseconds(50) };
        using var provider = CreateProvider(service);

        // Launch 10 concurrent calls
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => provider.GetEntitiesAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All should return the same instance
        Assert.All(results, r => Assert.Same(results[0], r));

        // Service should only be called once
        Assert.Equal(1, service.GetEntitiesCallCount);
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_ConcurrentCallsSameEntity_OnlySingleServiceCall()
    {
        var service = new StubMetadataService { Delay = TimeSpan.FromMilliseconds(50) };
        using var provider = CreateProvider(service);

        // Launch 10 concurrent calls for the same entity
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => provider.GetAttributesAsync("account"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All should return the same instance
        Assert.All(results, r => Assert.Same(results[0], r));

        // Service should only be called once for this entity
        Assert.Equal(1, service.GetAttributesCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetAttributesAsync_ConcurrentCallsDifferentEntities_IndependentLoads()
    {
        var service = new StubMetadataService { Delay = TimeSpan.FromMilliseconds(20) };
        using var provider = CreateProvider(service);

        // Launch concurrent calls for different entities
        var accountTask = provider.GetAttributesAsync("account");
        var contactTask = provider.GetAttributesAsync("contact");

        await Task.WhenAll(accountTask, contactTask);

        // Both should have been loaded independently
        Assert.Equal(1, service.GetAttributesCallCount("account"));
        Assert.Equal(1, service.GetAttributesCallCount("contact"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task GetRelationshipsAsync_ConcurrentCallsSameEntity_OnlySingleServiceCall()
    {
        var service = new StubMetadataService { Delay = TimeSpan.FromMilliseconds(50) };
        using var provider = CreateProvider(service);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => provider.GetRelationshipsAsync("account"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, service.GetRelationshipsCallCount("account"));
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task ConcurrentMixedOperations_DoNotThrow()
    {
        var service = new StubMetadataService { Delay = TimeSpan.FromMilliseconds(10) };
        using var provider = CreateProvider(service);

        // Mix of operations including invalidation
        var tasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(provider.GetEntitiesAsync());
            tasks.Add(provider.GetAttributesAsync("account"));
            tasks.Add(provider.GetRelationshipsAsync("account"));
            tasks.Add(Task.Run(() => provider.InvalidateEntity("contact")));
        }

        // Should complete without deadlocks or exceptions
        await Task.WhenAll(tasks);
    }

    #endregion

    #region Edge Cases

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task PreloadAsync_CalledTwice_SecondCallRefreshesCache()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);

        await provider.PreloadAsync();
        await provider.PreloadAsync();

        // PreloadAsync always fetches fresh data (useful for manual refresh)
        Assert.Equal(2, service.GetEntitiesCallCount);
    }

    [Fact]
    [Trait("Category", "TuiUnit")]
    public async Task Dispose_ThenGetEntities_ThrowsObjectDisposed()
    {
        var service = new StubMetadataService();
        using var provider = CreateProvider(service);
        await provider.PreloadAsync();

        provider.Dispose();

        // After dispose, the semaphore is disposed — operations should fail
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.PreloadAsync());
    }

    #endregion
}
