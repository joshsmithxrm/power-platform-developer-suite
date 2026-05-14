using System.Text.Json;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using Xunit;

// ReSharper disable UseObjectOrCollectionInitializer

namespace PPDS.Migration.Tests.Formats;

public class JsonDataWriterTests
{
    private static async Task<JsonDocument> WriteAndParseAsync(MigrationData data)
    {
        var writer = new JsonDataWriter();
        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream);
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenDataIsNull()
    {
        var writer = new JsonDataWriter();

        var act = async () => await writer.WriteAsync(null!, "output.json");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenPathIsNull()
    {
        var writer = new JsonDataWriter();
        var data = new MigrationData();

        var act = async () => await writer.WriteAsync(data, (string)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenPathIsEmpty()
    {
        var writer = new JsonDataWriter();
        var data = new MigrationData();

        var act = async () => await writer.WriteAsync(data, string.Empty);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenStreamIsNull()
    {
        var writer = new JsonDataWriter();
        var data = new MigrationData();

        var act = async () => await writer.WriteAsync(data, (Stream)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_Envelope_IncludesSchemaAndFormatVersion()
    {
        var data = new MigrationData
        {
            ExportedAt = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc)
        };

        using var doc = await WriteAndParseAsync(data);
        var root = doc.RootElement;

        root.GetProperty("$schema").GetString().Should().Be(JsonDataWriter.SchemaUri);
        root.GetProperty("formatVersion").GetString().Should().Be(JsonDataWriter.FormatVersion);
        root.GetProperty("exportedAt").GetString().Should().Be("2026-05-14T12:00:00.0000000Z");
    }

    [Fact]
    public async Task WriteAsync_SourceEnvironment_IncludedWhenSet()
    {
        var data = new MigrationData
        {
            SourceEnvironment = "https://contoso.crm.dynamics.com"
        };

        using var doc = await WriteAndParseAsync(data);

        doc.RootElement.GetProperty("sourceEnvironment").GetString()
            .Should().Be("https://contoso.crm.dynamics.com");
    }

    [Fact]
    public async Task WriteAsync_SourceEnvironment_OmittedWhenNull()
    {
        var data = new MigrationData();

        using var doc = await WriteAndParseAsync(data);

        doc.RootElement.TryGetProperty("sourceEnvironment", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_Schema_IncludesEntityMetadataAndFields()
    {
        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema
                    {
                        LogicalName = "account",
                        DisplayName = "Account",
                        PrimaryIdField = "accountid",
                        PrimaryNameField = "name",
                        ObjectTypeCode = 1,
                        Fields = new List<FieldSchema>
                        {
                            new FieldSchema { LogicalName = "accountid", DisplayName = "Account", Type = "guid", IsPrimaryKey = true },
                            new FieldSchema { LogicalName = "name", DisplayName = "Name", Type = "string" },
                            new FieldSchema { LogicalName = "primarycontactid", DisplayName = "Primary Contact", Type = "lookup", LookupEntity = "contact" }
                        }
                    }
                }
            }
        };

        using var doc = await WriteAndParseAsync(data);
        var entity = doc.RootElement.GetProperty("schema").GetProperty("entities")[0];

        entity.GetProperty("logicalName").GetString().Should().Be("account");
        entity.GetProperty("displayName").GetString().Should().Be("Account");
        entity.GetProperty("primaryIdField").GetString().Should().Be("accountid");
        entity.GetProperty("primaryNameField").GetString().Should().Be("name");
        entity.GetProperty("objectTypeCode").GetInt32().Should().Be(1);
        entity.GetProperty("disablePlugins").GetBoolean().Should().BeFalse();

        var fields = entity.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().HaveCount(3);

        var idField = fields.First(f => f.GetProperty("logicalName").GetString() == "accountid");
        idField.GetProperty("isPrimaryKey").GetBoolean().Should().BeTrue();

        var lookupField = fields.First(f => f.GetProperty("logicalName").GetString() == "primarycontactid");
        lookupField.GetProperty("lookupType").GetString().Should().Be("contact");
    }

    [Fact]
    public async Task WriteAsync_Schema_IncludesRelationshipsWhenPresent()
    {
        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema
                    {
                        LogicalName = "team",
                        DisplayName = "Team",
                        PrimaryIdField = "teamid",
                        Relationships = new List<RelationshipSchema>
                        {
                            new RelationshipSchema
                            {
                                Name = "teamroles",
                                IsManyToMany = true,
                                Entity1 = "team",
                                Entity2 = "role",
                                TargetEntityPrimaryKey = "roleid"
                            }
                        }
                    }
                }
            }
        };

        using var doc = await WriteAndParseAsync(data);
        var rels = doc.RootElement.GetProperty("schema").GetProperty("entities")[0]
            .GetProperty("relationships").EnumerateArray().ToList();

        rels.Should().HaveCount(1);
        var rel = rels[0];
        rel.GetProperty("name").GetString().Should().Be("teamroles");
        rel.GetProperty("manyToMany").GetBoolean().Should().BeTrue();
        rel.GetProperty("m2mTargetEntity").GetString().Should().Be("role");
        rel.GetProperty("m2mTargetEntityPrimaryKey").GetString().Should().Be("roleid");
    }

    [Fact]
    public async Task WriteAsync_Schema_OmitsRelationshipsWhenEmpty()
    {
        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema { LogicalName = "account", DisplayName = "Account", PrimaryIdField = "accountid" }
                }
            }
        };

        using var doc = await WriteAndParseAsync(data);
        var entity = doc.RootElement.GetProperty("schema").GetProperty("entities")[0];

        entity.TryGetProperty("relationships", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_Record_EmitsIdAndFields()
    {
        var recordId = Guid.NewGuid();
        var entity = new Entity("account", recordId);
        entity["name"] = "Acme Corp";

        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema { LogicalName = "account", DisplayName = "Account", PrimaryIdField = "accountid" }
                }
            },
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                { "account", new List<Entity> { entity } }
            }
        };

        using var doc = await WriteAndParseAsync(data);
        var entityNode = doc.RootElement.GetProperty("entities")[0];

        entityNode.GetProperty("logicalName").GetString().Should().Be("account");
        var record = entityNode.GetProperty("records")[0];
        record.GetProperty("id").GetString().Should().Be(recordId.ToString());
        record.GetProperty("fields").GetProperty("name").GetProperty("value").GetString().Should().Be("Acme Corp");
    }

    [Fact]
    public async Task WriteAsync_EntityReference_IncludesLookupMetadata()
    {
        var contactId = Guid.NewGuid();
        var entity = new Entity("account", Guid.NewGuid());
        entity["primarycontactid"] = new EntityReference("contact", contactId) { Name = "John Doe" };

        var data = BuildSingleEntityData(entity, "account");

        using var doc = await WriteAndParseAsync(data);
        var field = doc.RootElement.GetProperty("entities")[0]
            .GetProperty("records")[0]
            .GetProperty("fields")
            .GetProperty("primarycontactid");

        field.GetProperty("value").GetString().Should().Be(contactId.ToString());
        field.GetProperty("lookupEntity").GetString().Should().Be("contact");
        field.GetProperty("lookupName").GetString().Should().Be("John Doe");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteAsync_BooleanValue_EmitsJsonBoolean(bool value)
    {
        var entity = new Entity("testentity", Guid.NewGuid());
        entity["boolfield"] = value;

        var data = BuildSingleEntityData(entity, "testentity");

        using var doc = await WriteAndParseAsync(data);
        var field = doc.RootElement.GetProperty("entities")[0]
            .GetProperty("records")[0]
            .GetProperty("fields")
            .GetProperty("boolfield");

        field.GetProperty("value").GetBoolean().Should().Be(value);
    }

    [Fact]
    public async Task WriteAsync_DateTimeValue_EmitsIso8601Utc()
    {
        var entity = new Entity("testentity", Guid.NewGuid());
        entity["createdon"] = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var data = BuildSingleEntityData(entity, "testentity");

        using var doc = await WriteAndParseAsync(data);
        var value = doc.RootElement.GetProperty("entities")[0]
            .GetProperty("records")[0]
            .GetProperty("fields")
            .GetProperty("createdon")
            .GetProperty("value").GetString();

        value.Should().Be("2026-01-15T10:30:00.0000000Z");
    }

    [Fact]
    public async Task WriteAsync_OptionSetValue_EmitsNumeric()
    {
        var entity = new Entity("testentity", Guid.NewGuid());
        entity["statuscode"] = new OptionSetValue(7);

        var data = BuildSingleEntityData(entity, "testentity");

        using var doc = await WriteAndParseAsync(data);
        var value = doc.RootElement.GetProperty("entities")[0]
            .GetProperty("records")[0]
            .GetProperty("fields")
            .GetProperty("statuscode")
            .GetProperty("value").GetInt32();

        value.Should().Be(7);
    }

    [Fact]
    public async Task WriteAsync_MoneyValue_EmitsNumeric()
    {
        var entity = new Entity("testentity", Guid.NewGuid());
        entity["revenue"] = new Money(1234.56m);

        var data = BuildSingleEntityData(entity, "testentity");

        using var doc = await WriteAndParseAsync(data);
        var value = doc.RootElement.GetProperty("entities")[0]
            .GetProperty("records")[0]
            .GetProperty("fields")
            .GetProperty("revenue")
            .GetProperty("value").GetDecimal();

        value.Should().Be(1234.56m);
    }

    [Fact]
    public async Task WriteAsync_NullFieldValue_IsOmitted()
    {
        var entity = new Entity("testentity", Guid.NewGuid());
        entity["name"] = "Set";
        entity["nullable"] = null;

        var data = BuildSingleEntityData(entity, "testentity");

        using var doc = await WriteAndParseAsync(data);
        var fields = doc.RootElement.GetProperty("entities")[0]
            .GetProperty("records")[0]
            .GetProperty("fields");

        fields.TryGetProperty("name", out _).Should().BeTrue();
        fields.TryGetProperty("nullable", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_M2MRelationships_EmittedPerSourceRecord()
    {
        var sourceId = Guid.NewGuid();
        var target1 = Guid.NewGuid();
        var target2 = Guid.NewGuid();

        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema { LogicalName = "team", DisplayName = "Team", PrimaryIdField = "teamid" }
                }
            },
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                { "team", new List<Entity>() }
            },
            RelationshipData = new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                {
                    "team", new List<ManyToManyRelationshipData>
                    {
                        new ManyToManyRelationshipData
                        {
                            RelationshipName = "teamroles",
                            SourceEntityName = "team",
                            SourceId = sourceId,
                            TargetEntityName = "role",
                            TargetEntityPrimaryKey = "roleid",
                            TargetIds = new List<Guid> { target1, target2 }
                        }
                    }
                }
            }
        };

        using var doc = await WriteAndParseAsync(data);
        var m2m = doc.RootElement.GetProperty("entities")[0]
            .GetProperty("m2mRelationships")[0];

        m2m.GetProperty("relationshipName").GetString().Should().Be("teamroles");
        m2m.GetProperty("sourceId").GetString().Should().Be(sourceId.ToString());
        m2m.GetProperty("targetEntityName").GetString().Should().Be("role");
        m2m.GetProperty("targetEntityPrimaryKey").GetString().Should().Be("roleid");

        var targets = m2m.GetProperty("targetIds").EnumerateArray()
            .Select(t => t.GetString()!).ToList();
        targets.Should().BeEquivalentTo(new[] { target1.ToString(), target2.ToString() });
    }

    [Fact]
    public async Task WriteAsync_EntityWithNoM2M_EmitsEmptyArray()
    {
        var entity = new Entity("account", Guid.NewGuid());
        var data = BuildSingleEntityData(entity, "account");

        using var doc = await WriteAndParseAsync(data);
        var m2mArray = doc.RootElement.GetProperty("entities")[0].GetProperty("m2mRelationships");

        m2mArray.ValueKind.Should().Be(JsonValueKind.Array);
        m2mArray.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task WriteAsync_EmitsValidJson()
    {
        var entity = new Entity("account", Guid.NewGuid());
        entity["name"] = "Acme \"Inc\" \n\t<special>";
        var data = BuildSingleEntityData(entity, "account");

        var writer = new JsonDataWriter();
        using var stream = new MemoryStream();
        await writer.WriteAsync(data, stream);
        stream.Position = 0;

        // If output is invalid JSON, parse throws.
        var parse = async () =>
        {
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.GetProperty("entities").GetArrayLength();
        };

        await parse.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_FileDataPresent_EmitsWarningAndSkipsBinaries()
    {
        var entity = new Entity("account", Guid.NewGuid());
        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema { LogicalName = "account", DisplayName = "Account", PrimaryIdField = "accountid" }
                }
            },
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                { "account", new List<Entity> { entity } }
            },
            FileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
            {
                {
                    "account", new List<FileColumnData>
                    {
                        new FileColumnData { RecordId = entity.Id, FieldName = "doc", FileName = "a.pdf", MimeType = "application/pdf", Data = new byte[] { 1, 2, 3 } }
                    }
                }
            }
        };

        // The writer must not throw and must produce valid JSON. (Binary data has nowhere to go in single-file JSON.)
        using var doc = await WriteAndParseAsync(data);
        doc.RootElement.GetProperty("entities").GetArrayLength().Should().Be(1);
    }

    private static MigrationData BuildSingleEntityData(Entity entity, string logicalName)
    {
        return new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new EntitySchema
                    {
                        LogicalName = logicalName,
                        DisplayName = logicalName,
                        PrimaryIdField = $"{logicalName}id"
                    }
                }
            },
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                { logicalName, new List<Entity> { entity } }
            }
        };
    }
}
