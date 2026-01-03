using FluentAssertions;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Models;

public class IdMappingCollectionTests
{
    [Fact]
    public void AddMapping_StoresMapping()
    {
        var collection = new IdMappingCollection();
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        collection.AddMapping("account", oldId, newId);

        collection.TryGetNewId("account", oldId, out var retrievedId).Should().BeTrue();
        retrievedId.Should().Be(newId);
    }

    [Fact]
    public void AddMapping_IsCaseInsensitiveForEntityName()
    {
        var collection = new IdMappingCollection();
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        collection.AddMapping("account", oldId, newId);

        collection.TryGetNewId("ACCOUNT", oldId, out var retrievedId).Should().BeTrue();
        retrievedId.Should().Be(newId);
    }

    [Fact]
    public void AddMapping_UpdatesExistingMapping()
    {
        var collection = new IdMappingCollection();
        var oldId = Guid.NewGuid();
        var newId1 = Guid.NewGuid();
        var newId2 = Guid.NewGuid();

        collection.AddMapping("account", oldId, newId1);
        collection.AddMapping("account", oldId, newId2);

        collection.TryGetNewId("account", oldId, out var retrievedId).Should().BeTrue();
        retrievedId.Should().Be(newId2);
    }

    [Fact]
    public void TryGetNewId_ReturnsFalseWhenEntityNotFound()
    {
        var collection = new IdMappingCollection();
        var oldId = Guid.NewGuid();

        var result = collection.TryGetNewId("account", oldId, out var newId);

        result.Should().BeFalse();
        newId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryGetNewId_ReturnsFalseWhenIdNotFound()
    {
        var collection = new IdMappingCollection();
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        collection.AddMapping("account", oldId, newId);

        var result = collection.TryGetNewId("account", Guid.NewGuid(), out var retrievedId);

        result.Should().BeFalse();
        retrievedId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void GetNewId_ReturnsIdWhenFound()
    {
        var collection = new IdMappingCollection();
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        collection.AddMapping("account", oldId, newId);

        var retrievedId = collection.GetNewId("account", oldId);

        retrievedId.Should().Be(newId);
    }

    [Fact]
    public void GetNewId_ThrowsWhenMappingNotFound()
    {
        var collection = new IdMappingCollection();
        var oldId = Guid.NewGuid();

        var act = () => collection.GetNewId("account", oldId);

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage($"No mapping found for account ID {oldId}");
    }

    [Fact]
    public void GetMappingCount_ReturnsCorrectCount()
    {
        var collection = new IdMappingCollection();

        collection.AddMapping("account", Guid.NewGuid(), Guid.NewGuid());
        collection.AddMapping("account", Guid.NewGuid(), Guid.NewGuid());
        collection.AddMapping("contact", Guid.NewGuid(), Guid.NewGuid());

        collection.GetMappingCount("account").Should().Be(2);
        collection.GetMappingCount("contact").Should().Be(1);
    }

    [Fact]
    public void GetMappingCount_ReturnsZeroWhenEntityNotFound()
    {
        var collection = new IdMappingCollection();

        collection.GetMappingCount("account").Should().Be(0);
    }

    [Fact]
    public void TotalMappingCount_ReturnsSumAcrossAllEntities()
    {
        var collection = new IdMappingCollection();

        collection.AddMapping("account", Guid.NewGuid(), Guid.NewGuid());
        collection.AddMapping("account", Guid.NewGuid(), Guid.NewGuid());
        collection.AddMapping("contact", Guid.NewGuid(), Guid.NewGuid());
        collection.AddMapping("lead", Guid.NewGuid(), Guid.NewGuid());

        collection.TotalMappingCount.Should().Be(4);
    }

    [Fact]
    public void TotalMappingCount_ReturnsZeroWhenEmpty()
    {
        var collection = new IdMappingCollection();

        collection.TotalMappingCount.Should().Be(0);
    }

    [Fact]
    public void GetMappingsForEntity_ReturnsMappings()
    {
        var collection = new IdMappingCollection();
        var oldId1 = Guid.NewGuid();
        var newId1 = Guid.NewGuid();
        var oldId2 = Guid.NewGuid();
        var newId2 = Guid.NewGuid();

        collection.AddMapping("account", oldId1, newId1);
        collection.AddMapping("account", oldId2, newId2);

        var mappings = collection.GetMappingsForEntity("account");

        mappings.Should().HaveCount(2);
        mappings[oldId1].Should().Be(newId1);
        mappings[oldId2].Should().Be(newId2);
    }

    [Fact]
    public void GetMappingsForEntity_ReturnsEmptyWhenEntityNotFound()
    {
        var collection = new IdMappingCollection();

        var mappings = collection.GetMappingsForEntity("account");

        mappings.Should().BeEmpty();
    }

    [Fact]
    public void GetMappedEntities_ReturnsAllEntityNames()
    {
        var collection = new IdMappingCollection();

        collection.AddMapping("account", Guid.NewGuid(), Guid.NewGuid());
        collection.AddMapping("contact", Guid.NewGuid(), Guid.NewGuid());
        collection.AddMapping("lead", Guid.NewGuid(), Guid.NewGuid());

        var entities = collection.GetMappedEntities().ToList();

        entities.Should().HaveCount(3);
        entities.Should().Contain("account");
        entities.Should().Contain("contact");
        entities.Should().Contain("lead");
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentAdds()
    {
        var collection = new IdMappingCollection();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                collection.AddMapping("account", Guid.NewGuid(), Guid.NewGuid());
            }));
        }

        await Task.WhenAll(tasks);

        collection.GetMappingCount("account").Should().Be(100);
    }
}
