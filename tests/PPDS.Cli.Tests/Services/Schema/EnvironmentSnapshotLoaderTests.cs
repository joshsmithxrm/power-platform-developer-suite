using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using PPDS.Cli.Services.Schema.Snapshots;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Xunit;

namespace PPDS.Cli.Tests.Services.Schema;

public class EnvironmentSnapshotLoaderTests
{
    private static EntitySummary Summary(string logical) => new()
    {
        LogicalName = logical,
        DisplayName = logical,
        SchemaName = logical,
        IsCustomEntity = false,
        IsManaged = true
    };

    private static EntityMetadataDto BuildEntity(string logical, params AttributeMetadataDto[] attrs) => new()
    {
        LogicalName = logical,
        DisplayName = logical,
        SchemaName = logical,
        Attributes = new List<AttributeMetadataDto>(attrs)
    };

    [Fact]
    public async Task LoadAsync_BuildsSnapshotFromMetadataService()
    {
        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Summary("account") });

        mock.Setup(m => m.GetEntityAsync("account", true, true, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildEntity("account",
                new AttributeMetadataDto
                {
                    LogicalName = "name",
                    DisplayName = "Name",
                    SchemaName = "Name",
                    AttributeType = "String",
                    RequiredLevel = "ApplicationRequired",
                    MaxLength = 200
                },
                new AttributeMetadataDto
                {
                    LogicalName = "status",
                    DisplayName = "Status",
                    SchemaName = "Status",
                    AttributeType = "Picklist",
                    Options = new List<OptionValueDto>
                    {
                        new() { Value = 1, Label = "Active" },
                        new() { Value = 2, Label = "Inactive" }
                    }
                }));

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:https://example.crm.dynamics.com");

        var snapshot = await loader.LoadAsync();

        snapshot.IncludesOptionSetValues.Should().BeTrue();
        snapshot.Source.Should().Be("env:https://example.crm.dynamics.com");

        var account = snapshot.Entities.Should().ContainSingle().Subject;
        account.LogicalName.Should().Be("account");

        var name = account.Attributes.First(a => a.LogicalName == "name");
        name.AttributeType.Should().Be("string"); // normalized to lowercase
        name.RequiredLevel.Should().Be("ApplicationRequired");
        name.MaxLength.Should().Be(200);

        var status = account.Attributes.First(a => a.LogicalName == "status");
        status.OptionValues.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public async Task LoadAsync_RecordsUnloadedEntities_WhenFilterApplied()
    {
        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Summary("account"), Summary("contact"), Summary("lead") });

        mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), true, true, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, bool _, bool __, bool ___, bool ____, CancellationToken _____) => BuildEntity(name));

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test");

        await loader.LoadAsync(entityFilter: new[] { "account" });

        loader.UnloadedEntities.Should().BeEquivalentTo(new[] { "contact", "lead" });
    }

    [Fact]
    public async Task LoadAsync_InvokesProgressCallback_PerEntity()
    {
        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Summary("account"), Summary("contact") });
        mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), true, true, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, bool _, bool __, bool ___, bool ____, CancellationToken _____) => BuildEntity(name));

        var messages = new List<string>();
        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test", messages.Add);

        await loader.LoadAsync();

        messages.Should().HaveCount(2);
        messages[0].Should().Contain("1/2").And.Contain("account");
        messages[1].Should().Contain("2/2").And.Contain("contact");
    }

    [Fact]
    public async Task LoadAsync_AppliesEntityFilter()
    {
        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Summary("account"), Summary("contact") });

        mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), true, true, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, bool _, bool __, bool ___, bool ____, CancellationToken _____) => BuildEntity(name));

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test");

        var snapshot = await loader.LoadAsync(entityFilter: new[] { "account" });

        snapshot.Entities.Should().ContainSingle()
            .Which.LogicalName.Should().Be("account");
        mock.Verify(m => m.GetEntityAsync("contact", It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoadAsync_IncludesOneToMany_ManyToOne_AndManyToMany_Relationships()
    {
        // Regression coverage for issue surfaced by /review on PR #1060:
        // ManyToOneRelationships had been silently omitted from the snapshot, so
        // package-vs-env compare could never detect ManyToOne drift and env-vs-env
        // only caught it via the inverse OneToMany row. This test asserts all three
        // relationship kinds appear in the snapshot's Relationships list.
        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Summary("account") });

        mock.Setup(m => m.GetEntityAsync("account", true, true, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityMetadataDto
            {
                LogicalName = "account",
                DisplayName = "Account",
                SchemaName = "Account",
                Attributes = new List<AttributeMetadataDto>(),
                OneToManyRelationships = new List<RelationshipMetadataDto>
                {
                    new()
                    {
                        SchemaName = "account_contacts",
                        RelationshipType = "OneToMany",
                        ReferencedEntity = "account",
                        ReferencedAttribute = "accountid",
                        ReferencingEntity = "contact",
                        ReferencingAttribute = "parentcustomerid"
                    }
                },
                ManyToOneRelationships = new List<RelationshipMetadataDto>
                {
                    new()
                    {
                        SchemaName = "account_parentaccount",
                        RelationshipType = "ManyToOne",
                        ReferencedEntity = "account",
                        ReferencedAttribute = "accountid",
                        ReferencingEntity = "account",
                        ReferencingAttribute = "parentaccountid"
                    }
                },
                ManyToManyRelationships = new List<ManyToManyRelationshipDto>
                {
                    new()
                    {
                        SchemaName = "account_competitors",
                        IntersectEntityName = "accountcompetitors",
                        Entity1LogicalName = "account",
                        Entity1IntersectAttribute = "accountid",
                        Entity2LogicalName = "competitor",
                        Entity2IntersectAttribute = "competitorid"
                    }
                }
            });

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test");

        var snapshot = await loader.LoadAsync();

        var account = snapshot.Entities.Should().ContainSingle().Subject;
        account.Relationships.Should().HaveCount(3);

        var oneToMany = account.Relationships.Should().ContainSingle(r => r.RelationshipType == "OneToMany").Subject;
        oneToMany.SchemaName.Should().Be("account_contacts");
        oneToMany.ReferencingEntity.Should().Be("contact");
        oneToMany.ReferencedEntity.Should().Be("account");

        var manyToOne = account.Relationships.Should().ContainSingle(r => r.RelationshipType == "ManyToOne").Subject;
        manyToOne.SchemaName.Should().Be("account_parentaccount");
        manyToOne.ReferencingEntity.Should().Be("account");
        manyToOne.ReferencedEntity.Should().Be("account");

        var manyToMany = account.Relationships.Should().ContainSingle(r => r.RelationshipType == "ManyToMany").Subject;
        manyToMany.SchemaName.Should().Be("account_competitors");
    }

    [Fact]
    public async Task LoadAsync_ResultsPreserveOrder_WhenParallel()
    {
        // AC-02: With parallelism=4, results must appear in the original list order
        // regardless of completion order. Each entity's mock delay is inversely
        // proportional to its position so later entities resolve first.
        var names = new[] { "a", "b", "c", "d", "e", "f" };

        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(names.Select(Summary).ToArray());

        mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), true, true, false, false, It.IsAny<CancellationToken>()))
            .Returns(async (string name, bool _, bool __, bool ___, bool ____, CancellationToken _____) =>
            {
                var delayMs = (names.Length - Array.IndexOf(names, name)) * 10;
                await Task.Delay(delayMs).ConfigureAwait(false);
                return BuildEntity(name);
            });

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test", progress: null, parallelism: 4);

        var snapshot = await loader.LoadAsync();

        snapshot.Entities.Select(e => e.LogicalName).Should().Equal(names);
    }

    [Fact]
    public async Task LoadAsync_BoundsParallelism_ByDegree()
    {
        // AC-03: Peak in-flight GetEntityAsync calls never exceeds parallelism.
        const int parallelism = 4;
        const int entityCount = 8;
        var peak = 0;
        var current = 0;

        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, entityCount).Select(i => Summary($"e{i}")).ToArray());

        mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), true, true, false, false, It.IsAny<CancellationToken>()))
            .Returns(async (string name, bool _, bool __, bool ___, bool ____, CancellationToken _____) =>
            {
                var c = Interlocked.Increment(ref current);
                int observedPeak;
                do
                {
                    observedPeak = Volatile.Read(ref peak);
                    if (c <= observedPeak) break;
                } while (Interlocked.CompareExchange(ref peak, c, observedPeak) != observedPeak);

                await Task.Delay(50).ConfigureAwait(false);
                Interlocked.Decrement(ref current);
                return BuildEntity(name);
            });

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test", progress: null, parallelism: parallelism);

        await loader.LoadAsync();

        peak.Should().BeLessThanOrEqualTo(parallelism);
    }

    [Fact]
    public async Task LoadAsync_AchievesConcurrency_WhenParallelismGreaterThanOne()
    {
        // AC-04: With parallelism > 1, peak concurrency must exceed 1 (real parallelism).
        const int parallelism = 4;
        const int entityCount = 8;
        var peak = 0;
        var current = 0;

        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, entityCount).Select(i => Summary($"e{i}")).ToArray());

        mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), true, true, false, false, It.IsAny<CancellationToken>()))
            .Returns(async (string name, bool _, bool __, bool ___, bool ____, CancellationToken _____) =>
            {
                var c = Interlocked.Increment(ref current);
                int observedPeak;
                do
                {
                    observedPeak = Volatile.Read(ref peak);
                    if (c <= observedPeak) break;
                } while (Interlocked.CompareExchange(ref peak, c, observedPeak) != observedPeak);

                await Task.Delay(50).ConfigureAwait(false);
                Interlocked.Decrement(ref current);
                return BuildEntity(name);
            });

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test", progress: null, parallelism: parallelism);

        await loader.LoadAsync();

        peak.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task LoadAsync_PropagatesCancellation_WhenTokenCancelled()
    {
        // AC-06: OperationCanceledException propagates out of LoadAsync.
        var mock = new Mock<IMetadataQueryService>();
        mock.Setup(m => m.GetEntitiesAsync(false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Summary("account"), Summary("contact") });
        mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), true, true, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, bool _, bool __, bool ___, bool ____, CancellationToken _____) => BuildEntity(name));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test", progress: null, parallelism: 2);

        await loader.Invoking(l => l.LoadAsync(cancellationToken: cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
