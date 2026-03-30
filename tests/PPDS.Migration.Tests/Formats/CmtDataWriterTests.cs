using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using Xunit;

// ReSharper disable UseObjectOrCollectionInitializer

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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteAsync_FileColumnValue_WritesMetadataAttributes()
    {
        // Arrange — AC-34: field element carries filename and mimetype attributes
        // AC-33: file column data should be stored in files/ directory
        var writer = new CmtDataWriter();
        var recordId = Guid.NewGuid();
        var entity = new Entity("account", recordId);

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new EntitySchema
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    PrimaryIdField = "accountid",
                    Fields = new List<FieldSchema>
                    {
                        new FieldSchema { LogicalName = "accountid", Type = "guid", IsPrimaryKey = true },
                        new FieldSchema { LogicalName = "name", Type = "string" },
                        new FieldSchema { LogicalName = "cr_document", Type = "file" }
                    }
                }
            }
        };

        var fileBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 };
        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = recordId,
                        FieldName = "cr_document",
                        FileName = "report.pdf",
                        MimeType = "application/pdf",
                        Data = fileBytes
                    }
                }
            }
        };

        var data = new MigrationData
        {
            Schema = schema,
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                { "account", new List<Entity> { entity } }
            },
            FileData = fileData
        };

        // Act
        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;

        // Assert — verify files/ entry exists in ZIP (AC-33)
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var fileEntry = archive.GetEntry($"files/account/{recordId}_cr_document.bin");
        fileEntry.Should().NotBeNull("file column data should be stored in files/ directory (AC-33)");

        // Read the binary and verify it matches
        using var entryStream = fileEntry!.Open();
        using var memoryStream = new MemoryStream();
        await entryStream.CopyToAsync(memoryStream);
        memoryStream.ToArray().Should().Equal(fileBytes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteAsync_WithFileData_CreatesFilesDirectoryInZip()
    {
        // Arrange — AC-33: files stored in files/{entity}/{recordid}_{field}.bin in ZIP
        var writer = new CmtDataWriter();
        var recordId1 = Guid.NewGuid();
        var recordId2 = Guid.NewGuid();

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new EntitySchema
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    PrimaryIdField = "accountid",
                    Fields = new List<FieldSchema>
                    {
                        new FieldSchema { LogicalName = "accountid", Type = "guid", IsPrimaryKey = true }
                    }
                }
            }
        };

        var data = new MigrationData
        {
            Schema = schema,
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>(),
            FileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
            {
                {
                    "account", new List<FileColumnData>
                    {
                        new FileColumnData { RecordId = recordId1, FieldName = "doc1", FileName = "a.pdf", MimeType = "application/pdf", Data = new byte[] { 1, 2, 3 } },
                        new FileColumnData { RecordId = recordId2, FieldName = "doc2", FileName = "b.pdf", MimeType = "application/pdf", Data = new byte[] { 4, 5 } }
                    }
                }
            }
        };

        // Act
        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;

        // Assert
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(e => e.FullName.StartsWith("files/")).ToList();
        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.FullName == $"files/account/{recordId1}_doc1.bin");
        entries.Should().Contain(e => e.FullName == $"files/account/{recordId2}_doc2.bin");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteAsync_EmptyFileData_NoFilesEntriesInZip()
    {
        var writer = new CmtDataWriter();
        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema { LogicalName = "account", DisplayName = "Account", PrimaryIdField = "accountid" }
                }
            },
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>()
        };

        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var fileEntries = archive.Entries.Where(e => e.FullName.StartsWith("files/")).ToList();
        fileEntries.Should().BeEmpty();
    }
}
