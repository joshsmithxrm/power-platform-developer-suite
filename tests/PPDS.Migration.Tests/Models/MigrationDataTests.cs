using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Models;

public class MigrationDataTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var data = new MigrationData();

        data.Schema.Should().NotBeNull();
        data.EntityData.Should().NotBeNull().And.BeEmpty();
        data.RelationshipData.Should().NotBeNull().And.BeEmpty();
        data.ExportedAt.Should().Be(default(DateTime));
        data.SourceEnvironment.Should().BeNull();
        data.FileData.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var schema = new MigrationSchema { Version = "1.0" };
        var timestamp = DateTime.UtcNow;
        var entityData = new Dictionary<string, IReadOnlyList<Entity>>
        {
            { "account", new List<Entity> { new Entity("account") } }
        };
        var relationshipData = new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
        {
            { "systemuser", new List<ManyToManyRelationshipData>() }
        };

        var data = new MigrationData
        {
            Schema = schema,
            ExportedAt = timestamp,
            SourceEnvironment = "https://test.crm.dynamics.com",
            EntityData = entityData,
            RelationshipData = relationshipData
        };

        data.Schema.Version.Should().Be("1.0");
        data.ExportedAt.Should().Be(timestamp);
        data.SourceEnvironment.Should().Be("https://test.crm.dynamics.com");
        data.EntityData.Should().HaveCount(1);
        data.RelationshipData.Should().HaveCount(1);
    }

    [Fact]
    public void TotalRecordCount_ReturnsSumAcrossAllEntities()
    {
        var entityData = new Dictionary<string, IReadOnlyList<Entity>>
        {
            { "account", new List<Entity> { new Entity("account"), new Entity("account") } },
            { "contact", new List<Entity> { new Entity("contact"), new Entity("contact"), new Entity("contact") } },
            { "lead", new List<Entity> { new Entity("lead") } }
        };

        var data = new MigrationData { EntityData = entityData };

        data.TotalRecordCount.Should().Be(6);
    }

    [Fact]
    public void TotalRecordCount_ReturnsZeroWhenNoData()
    {
        var data = new MigrationData();

        data.TotalRecordCount.Should().Be(0);
    }

    [Fact]
    public void EntityData_CanStoreMultipleEntities()
    {
        var account1 = new Entity("account");
        account1["accountid"] = Guid.NewGuid();
        var account2 = new Entity("account");
        account2["accountid"] = Guid.NewGuid();

        var entityData = new Dictionary<string, IReadOnlyList<Entity>>
        {
            { "account", new List<Entity> { account1, account2 } }
        };

        var data = new MigrationData { EntityData = entityData };

        data.EntityData["account"].Should().HaveCount(2);
    }

    [Fact]
    public void FileData_DefaultsToEmpty()
    {
        var data = new MigrationData();

        data.FileData.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void FileData_CanBeSetAndRetrieved()
    {
        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = Guid.NewGuid(),
                        FieldName = "myfilecolumn",
                        FileName = "report.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 1, 2, 3 }
                    }
                }
            }
        };

        var data = new MigrationData { FileData = fileData };

        data.FileData.Should().HaveCount(1);
        data.FileData["account"].Should().HaveCount(1);
        data.FileData["account"][0].FileName.Should().Be("report.pdf");
    }
}

public class ManyToManyRelationshipDataTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var relationshipData = new ManyToManyRelationshipData();

        relationshipData.RelationshipName.Should().BeEmpty();
        relationshipData.SourceEntityName.Should().BeEmpty();
        relationshipData.SourceId.Should().Be(Guid.Empty);
        relationshipData.TargetEntityName.Should().BeEmpty();
        relationshipData.TargetEntityPrimaryKey.Should().BeEmpty();
        relationshipData.TargetIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var sourceId = Guid.NewGuid();
        var targetId1 = Guid.NewGuid();
        var targetId2 = Guid.NewGuid();

        var relationshipData = new ManyToManyRelationshipData
        {
            RelationshipName = "systemuserroles_association",
            SourceEntityName = "systemuser",
            SourceId = sourceId,
            TargetEntityName = "role",
            TargetEntityPrimaryKey = "roleid",
            TargetIds = new List<Guid> { targetId1, targetId2 }
        };

        relationshipData.RelationshipName.Should().Be("systemuserroles_association");
        relationshipData.SourceEntityName.Should().Be("systemuser");
        relationshipData.SourceId.Should().Be(sourceId);
        relationshipData.TargetEntityName.Should().Be("role");
        relationshipData.TargetEntityPrimaryKey.Should().Be("roleid");
        relationshipData.TargetIds.Should().HaveCount(2);
        relationshipData.TargetIds.Should().Contain(targetId1);
        relationshipData.TargetIds.Should().Contain(targetId2);
    }

    [Fact]
    public void TargetIds_CanBePopulated()
    {
        var relationshipData = new ManyToManyRelationshipData();

        relationshipData.TargetIds.Add(Guid.NewGuid());
        relationshipData.TargetIds.Add(Guid.NewGuid());
        relationshipData.TargetIds.Add(Guid.NewGuid());

        relationshipData.TargetIds.Should().HaveCount(3);
    }
}
