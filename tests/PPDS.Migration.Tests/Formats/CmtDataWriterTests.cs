using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Formats;

public class CmtDataWriterTests
{
    [Fact]
    public void Constructor_InitializesSuccessfully()
    {
        var writer = new CmtDataWriter();

        writer.Should().NotBeNull();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenDataIsNull()
    {
        var writer = new CmtDataWriter();

        var act = async () => await writer.WriteAsync(null!, "output.zip");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenPathIsNull()
    {
        var writer = new CmtDataWriter();
        var data = new MigrationData();

        var act = async () => await writer.WriteAsync(data, (string)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenPathIsEmpty()
    {
        var writer = new CmtDataWriter();
        var data = new MigrationData();

        var act = async () => await writer.WriteAsync(data, string.Empty);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(true, "True")]
    [InlineData(false, "False")]
    public async Task WriteAsync_BooleanValues_WritesStringFormat(bool value, string expected)
    {
        // Arrange
        var writer = new CmtDataWriter();
        var entity = new Entity("testentity", Guid.NewGuid());
        entity["boolfield"] = value;

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new EntitySchema
                {
                    LogicalName = "testentity",
                    DisplayName = "Test Entity",
                    PrimaryIdField = "testentityid",
                    Fields = new List<FieldSchema>
                    {
                        new FieldSchema { LogicalName = "boolfield", Type = "bool" }
                    }
                }
            }
        };

        var data = new MigrationData
        {
            Schema = schema,
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                { "testentity", new List<Entity> { entity } }
            }
        };

        // Act
        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;

        // Assert
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var dataEntry = archive.GetEntry("data.xml");
        using var dataStream = dataEntry!.Open();
        var doc = XDocument.Load(dataStream);

        var fieldValue = doc.Descendants("field")
            .Where(f => f.Attribute("name")?.Value == "boolfield")
            .Select(f => f.Attribute("value")?.Value)
            .FirstOrDefault();

        fieldValue.Should().Be(expected, $"CMT format uses '{expected}' for boolean {value} values");
    }

    [Fact]
    public async Task WriteAsync_SchemaWithRelationships_IncludesRelationshipsSection()
    {
        // Arrange
        var writer = new CmtDataWriter();

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new EntitySchema
                {
                    LogicalName = "team",
                    DisplayName = "Team",
                    PrimaryIdField = "teamid",
                    Fields = new List<FieldSchema>
                    {
                        new FieldSchema { LogicalName = "teamid", Type = "guid", IsPrimaryKey = true },
                        new FieldSchema { LogicalName = "name", Type = "string" }
                    },
                    Relationships = new List<RelationshipSchema>
                    {
                        new RelationshipSchema
                        {
                            Name = "teamroles",
                            IsManyToMany = true,
                            Entity1 = "team",
                            Entity2 = "role",
                            TargetEntityPrimaryKey = "roleid"
                        },
                        new RelationshipSchema
                        {
                            Name = "teammembership",
                            IsManyToMany = true,
                            Entity1 = "team",
                            Entity2 = "systemuser",
                            TargetEntityPrimaryKey = "systemuserid"
                        }
                    }
                }
            }
        };

        var data = new MigrationData
        {
            Schema = schema,
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>()
        };

        // Act
        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;

        // Assert
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var schemaEntry = archive.GetEntry("data_schema.xml");
        using var schemaStream = schemaEntry!.Open();
        var doc = XDocument.Load(schemaStream);

        var relationshipsElement = doc.Descendants("relationships").FirstOrDefault();
        relationshipsElement.Should().NotBeNull("schema should contain <relationships> section");

        var relationships = relationshipsElement!.Elements("relationship").ToList();
        relationships.Should().HaveCount(2);

        var teamrolesRel = relationships.FirstOrDefault(r => r.Attribute("name")?.Value == "teamroles");
        teamrolesRel.Should().NotBeNull();
        teamrolesRel!.Attribute("manyToMany")?.Value.Should().Be("true");
        teamrolesRel.Attribute("m2mTargetEntity")?.Value.Should().Be("role");
        teamrolesRel.Attribute("m2mTargetEntityPrimaryKey")?.Value.Should().Be("roleid");
    }

    [Fact]
    public async Task WriteAsync_SchemaWithoutRelationships_OmitsRelationshipsSection()
    {
        // Arrange
        var writer = new CmtDataWriter();

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new EntitySchema
                {
                    LogicalName = "testentity",
                    DisplayName = "Test Entity",
                    PrimaryIdField = "testentityid",
                    Fields = new List<FieldSchema>
                    {
                        new FieldSchema { LogicalName = "testentityid", Type = "guid", IsPrimaryKey = true }
                    },
                    Relationships = new List<RelationshipSchema>() // Empty
                }
            }
        };

        var data = new MigrationData
        {
            Schema = schema,
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>()
        };

        // Act
        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;

        // Assert
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var schemaEntry = archive.GetEntry("data_schema.xml");
        using var schemaStream = schemaEntry!.Open();
        var doc = XDocument.Load(schemaStream);

        var relationshipsElement = doc.Descendants("relationships").FirstOrDefault();
        relationshipsElement.Should().BeNull("schema without relationships should not have empty <relationships> section");
    }
}
