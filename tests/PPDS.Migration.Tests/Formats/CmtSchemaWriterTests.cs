using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Formats;

public class CmtSchemaWriterTests
{
    [Fact]
    public async Task WriteAsync_WritesBasicEntitySchema()
    {
        var schema = new MigrationSchema
        {
            Version = "1.0",
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    ObjectTypeCode = 1,
                    PrimaryIdField = "accountid",
                    PrimaryNameField = "name",
                    DisablePlugins = false,
                    Fields = new List<FieldSchema>
                    {
                        new() { LogicalName = "accountid", DisplayName = "Account ID", Type = "uniqueidentifier", IsPrimaryKey = true },
                        new() { LogicalName = "name", DisplayName = "Account Name", Type = "string" }
                    },
                    Relationships = new List<RelationshipSchema>()
                }
            }
        };

        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        await writer.WriteAsync(schema, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);

        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("entities");
        var entityElement = doc.Root.Element("entity");
        entityElement.Should().NotBeNull();
        entityElement!.Attribute("name")!.Value.Should().Be("account");
        entityElement.Attribute("displayname")!.Value.Should().Be("Account");
        entityElement.Attribute("etc")!.Value.Should().Be("1");
        entityElement.Attribute("primaryidfield")!.Value.Should().Be("accountid");
        entityElement.Attribute("primarynamefield")!.Value.Should().Be("name");
        entityElement.Attribute("disableplugins")!.Value.Should().Be("false");
    }

    [Fact]
    public async Task WriteAsync_WritesFields()
    {
        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    PrimaryIdField = "accountid",
                    PrimaryNameField = "name",
                    Fields = new List<FieldSchema>
                    {
                        new()
                        {
                            LogicalName = "accountid",
                            DisplayName = "Account ID",
                            Type = "uniqueidentifier",
                            IsPrimaryKey = true
                        },
                        new()
                        {
                            LogicalName = "parentaccountid",
                            DisplayName = "Parent Account",
                            Type = "lookup",
                            LookupEntity = "account"
                        }
                    },
                    Relationships = new List<RelationshipSchema>()
                }
            }
        };

        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        await writer.WriteAsync(schema, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);

        var fieldsElement = doc.Root!.Element("entity")!.Element("fields");
        fieldsElement.Should().NotBeNull();
        var fieldElements = fieldsElement!.Elements("field").ToList();
        fieldElements.Should().HaveCount(2);

        var idField = fieldElements[0];
        idField.Attribute("name")!.Value.Should().Be("accountid");
        idField.Attribute("primaryKey").Should().NotBeNull();
        idField.Attribute("primaryKey")!.Value.Should().Be("true");

        var lookupField = fieldElements[1];
        lookupField.Attribute("name")!.Value.Should().Be("parentaccountid");
        lookupField.Attribute("lookupType")!.Value.Should().Be("account");
    }

    [Fact]
    public async Task WriteAsync_WritesValidityFlags_OnlyWhenFalse()
    {
        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    PrimaryIdField = "accountid",
                    PrimaryNameField = "name",
                    Fields = new List<FieldSchema>
                    {
                        new()
                        {
                            LogicalName = "field1",
                            DisplayName = "Field 1",
                            Type = "string",
                            IsValidForCreate = true,
                            IsValidForUpdate = true
                        },
                        new()
                        {
                            LogicalName = "field2",
                            DisplayName = "Field 2",
                            Type = "string",
                            IsValidForCreate = false,
                            IsValidForUpdate = true
                        }
                    },
                    Relationships = new List<RelationshipSchema>()
                }
            }
        };

        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        await writer.WriteAsync(schema, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);

        var fieldElements = doc.Root!.Element("entity")!.Element("fields")!.Elements("field").ToList();

        // Field with both true should not have attributes
        fieldElements[0].Attribute("isValidForCreate").Should().BeNull();
        fieldElements[0].Attribute("isValidForUpdate").Should().BeNull();

        // Field with false should have attribute
        fieldElements[1].Attribute("isValidForCreate")!.Value.Should().Be("false");
        fieldElements[1].Attribute("isValidForUpdate").Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_WritesOneToManyRelationships()
    {
        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "contact",
                    DisplayName = "Contact",
                    PrimaryIdField = "contactid",
                    PrimaryNameField = "fullname",
                    Fields = new List<FieldSchema>(),
                    Relationships = new List<RelationshipSchema>
                    {
                        new()
                        {
                            Name = "account_primary_contact",
                            IsManyToMany = false,
                            Entity1 = "contact",
                            Entity1Attribute = "parentcustomerid",
                            Entity2 = "account",
                            Entity2Attribute = "accountid"
                        }
                    }
                }
            }
        };

        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        await writer.WriteAsync(schema, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);

        var relElement = doc.Root!.Element("entity")!.Element("relationships")!.Element("relationship");
        relElement.Should().NotBeNull();
        relElement!.Attribute("name")!.Value.Should().Be("account_primary_contact");
        relElement.Attribute("manyToMany")!.Value.Should().Be("false");
        relElement.Attribute("referencingEntity")!.Value.Should().Be("contact");
        relElement.Attribute("referencingAttribute")!.Value.Should().Be("parentcustomerid");
        relElement.Attribute("referencedEntity")!.Value.Should().Be("account");
        relElement.Attribute("referencedAttribute")!.Value.Should().Be("accountid");
    }

    [Fact]
    public async Task WriteAsync_WritesManyToManyRelationships()
    {
        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "systemuser",
                    DisplayName = "User",
                    PrimaryIdField = "systemuserid",
                    PrimaryNameField = "fullname",
                    Fields = new List<FieldSchema>(),
                    Relationships = new List<RelationshipSchema>
                    {
                        new()
                        {
                            Name = "systemuserroles_association",
                            IsManyToMany = true,
                            IsReflexive = false,
                            Entity1 = "systemuser",
                            Entity2 = "role",
                            IntersectEntity = "systemuserroles",
                            TargetEntityPrimaryKey = "roleid"
                        }
                    }
                }
            }
        };

        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        await writer.WriteAsync(schema, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);

        var relElement = doc.Root!.Element("entity")!.Element("relationships")!.Element("relationship");
        relElement.Should().NotBeNull();
        relElement!.Attribute("name")!.Value.Should().Be("systemuserroles_association");
        relElement.Attribute("manyToMany")!.Value.Should().Be("true");
        relElement.Attribute("relatedEntityName")!.Value.Should().Be("systemuserroles");
        relElement.Attribute("m2mTargetEntity")!.Value.Should().Be("role");
        relElement.Attribute("m2mTargetEntityPrimaryKey")!.Value.Should().Be("roleid");
    }

    [Fact]
    public async Task WriteAsync_WritesFetchXmlFilter()
    {
        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    PrimaryIdField = "accountid",
                    PrimaryNameField = "name",
                    Fields = new List<FieldSchema>(),
                    Relationships = new List<RelationshipSchema>(),
                    FetchXmlFilter = "<filter><condition attribute='statecode' operator='eq' value='0'/></filter>"
                }
            }
        };

        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        await writer.WriteAsync(schema, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);

        var filterElement = doc.Root!.Element("entity")!.Element("filter");
        filterElement.Should().NotBeNull();
        filterElement!.Value.Should().Contain("statecode");
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenSchemaIsNull()
    {
        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        var act = async () => await writer.WriteAsync(null!, stream);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenStreamIsNull()
    {
        var writer = new CmtSchemaWriter();
        var schema = new MigrationSchema();

        var act = async () => await writer.WriteAsync(schema, (Stream)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_ThrowsWhenPathIsNull()
    {
        var writer = new CmtSchemaWriter();
        var schema = new MigrationSchema();

        var act = async () => await writer.WriteAsync(schema, (string)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_WritesMultipleEntities()
    {
        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new() { LogicalName = "account", DisplayName = "Account", PrimaryIdField = "accountid", PrimaryNameField = "name", Fields = new List<FieldSchema>(), Relationships = new List<RelationshipSchema>() },
                new() { LogicalName = "contact", DisplayName = "Contact", PrimaryIdField = "contactid", PrimaryNameField = "fullname", Fields = new List<FieldSchema>(), Relationships = new List<RelationshipSchema>() }
            }
        };

        var writer = new CmtSchemaWriter();
        using var stream = new MemoryStream();

        await writer.WriteAsync(schema, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);

        var entities = doc.Root!.Elements("entity").ToList();
        entities.Should().HaveCount(2);
        entities[0].Attribute("name")!.Value.Should().Be("account");
        entities[1].Attribute("name")!.Value.Should().Be("contact");
    }
}
