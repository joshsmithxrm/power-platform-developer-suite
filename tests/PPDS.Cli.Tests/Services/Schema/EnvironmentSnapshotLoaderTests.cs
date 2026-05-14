using System.Collections.Generic;
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
}
