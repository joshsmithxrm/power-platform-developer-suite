using FluentAssertions;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class StateTransitionCollectionTests
{
    [Fact]
    public void AddAndRetrieveTransitions()
    {
        var collection = new StateTransitionCollection();
        var id = Guid.NewGuid();
        var data = new StateTransitionData
        {
            EntityName = "account",
            RecordId = id,
            StateCode = 1,
            StatusCode = 2
        };

        collection.Add("account", id, data);

        var transitions = collection.GetTransitions("account");
        transitions.Should().ContainSingle();
        transitions[0].RecordId.Should().Be(id);
        transitions[0].StateCode.Should().Be(1);
        transitions[0].StatusCode.Should().Be(2);
    }

    [Fact]
    public void GetEntityNamesReturnsDistinctNames()
    {
        var collection = new StateTransitionCollection();
        collection.Add("account", Guid.NewGuid(), new StateTransitionData { EntityName = "account", StateCode = 1, StatusCode = 1 });
        collection.Add("contact", Guid.NewGuid(), new StateTransitionData { EntityName = "contact", StateCode = 1, StatusCode = 1 });
        collection.Add("account", Guid.NewGuid(), new StateTransitionData { EntityName = "account", StateCode = 0, StatusCode = 1 });

        var names = collection.GetEntityNames().ToList();

        names.Should().HaveCount(2);
        names.Should().Contain("account");
        names.Should().Contain("contact");
    }

    [Fact]
    public void CountReturnsTotal()
    {
        var collection = new StateTransitionCollection();
        collection.Add("account", Guid.NewGuid(), new StateTransitionData { EntityName = "account", StateCode = 1, StatusCode = 1 });
        collection.Add("contact", Guid.NewGuid(), new StateTransitionData { EntityName = "contact", StateCode = 1, StatusCode = 1 });
        collection.Add("account", Guid.NewGuid(), new StateTransitionData { EntityName = "account", StateCode = 0, StatusCode = 1 });

        collection.Count.Should().Be(3);
    }

    [Fact]
    public async Task ThreadSafeAdditions()
    {
        var collection = new StateTransitionCollection();
        const int itemsPerThread = 100;
        const int threadCount = 10;

        var tasks = Enumerable.Range(0, threadCount).Select(t =>
            Task.Run(() =>
            {
                for (var i = 0; i < itemsPerThread; i++)
                {
                    var entity = $"entity{t % 3}"; // 3 distinct entity names
                    collection.Add(entity, Guid.NewGuid(), new StateTransitionData
                    {
                        EntityName = entity,
                        StateCode = 1,
                        StatusCode = 1
                    });
                }
            })).ToArray();

        await Task.WhenAll(tasks);

        collection.Count.Should().Be(threadCount * itemsPerThread);
    }

    [Fact]
    public void GetTransitions_UnknownEntity_ReturnsEmptyList()
    {
        var collection = new StateTransitionCollection();

        var transitions = collection.GetTransitions("nonexistent");

        transitions.Should().BeEmpty();
    }
}
