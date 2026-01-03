using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Formats;

public class CmtSchemaReaderTests
{
    [Fact]
    public async Task ReadAsync_ParsesBasicEntitySchema()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" etc=""1"" primaryidfield=""accountid"" primarynamefield=""name"" disableplugins=""false"">
    <fields>
      <field displayname=""Account ID"" name=""accountid"" type=""uniqueidentifier"" primaryKey=""true"" />
      <field displayname=""Account Name"" name=""name"" type=""string"" />
    </fields>
    <relationships />
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        schema.Should().NotBeNull();
        schema.Entities.Should().HaveCount(1);
        schema.Entities[0].LogicalName.Should().Be("account");
        schema.Entities[0].DisplayName.Should().Be("Account");
        schema.Entities[0].PrimaryIdField.Should().Be("accountid");
        schema.Entities[0].PrimaryNameField.Should().Be("name");
        schema.Entities[0].DisablePlugins.Should().BeFalse();
        schema.Entities[0].ObjectTypeCode.Should().Be(1);
    }

    [Fact]
    public async Task ReadAsync_ParsesFields()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" primaryidfield=""accountid"" primarynamefield=""name"">
    <fields>
      <field displayname=""Account ID"" name=""accountid"" type=""uniqueidentifier"" primaryKey=""true"" />
      <field displayname=""Parent Account"" name=""parentaccountid"" type=""lookup"" lookupType=""account"" />
      <field displayname=""Name"" name=""name"" type=""string"" maxlength=""100"" />
      <field displayname=""Revenue"" name=""revenue"" type=""money"" precision=""2"" />
      <field displayname=""Custom Field"" name=""new_customfield"" type=""string"" customfield=""true"" isrequired=""true"" />
    </fields>
    <relationships />
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        var entity = schema.Entities[0];
        entity.Fields.Should().HaveCount(5);

        var idField = entity.Fields[0];
        idField.LogicalName.Should().Be("accountid");
        idField.Type.Should().Be("uniqueidentifier");
        idField.IsPrimaryKey.Should().BeTrue();

        var lookupField = entity.Fields[1];
        lookupField.LogicalName.Should().Be("parentaccountid");
        lookupField.Type.Should().Be("lookup");
        lookupField.LookupEntity.Should().Be("account");

        var stringField = entity.Fields[2];
        stringField.MaxLength.Should().Be(100);

        var moneyField = entity.Fields[3];
        moneyField.Precision.Should().Be(2);

        var customField = entity.Fields[4];
        customField.IsCustomField.Should().BeTrue();
        customField.IsRequired.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ParsesValidityFlags()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" primaryidfield=""accountid"" primarynamefield=""name"">
    <fields>
      <field name=""field1"" type=""string"" isValidForCreate=""true"" isValidForUpdate=""true"" />
      <field name=""field2"" type=""string"" isValidForCreate=""false"" isValidForUpdate=""true"" />
      <field name=""field3"" type=""string"" isValidForCreate=""true"" isValidForUpdate=""false"" />
      <field name=""field4"" type=""string"" />
    </fields>
    <relationships />
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        var entity = schema.Entities[0];
        entity.Fields[0].IsValidForCreate.Should().BeTrue();
        entity.Fields[0].IsValidForUpdate.Should().BeTrue();
        entity.Fields[1].IsValidForCreate.Should().BeFalse();
        entity.Fields[1].IsValidForUpdate.Should().BeTrue();
        entity.Fields[2].IsValidForCreate.Should().BeTrue();
        entity.Fields[2].IsValidForUpdate.Should().BeFalse();
        entity.Fields[3].IsValidForCreate.Should().BeTrue(); // Default
        entity.Fields[3].IsValidForUpdate.Should().BeTrue(); // Default
    }

    [Fact]
    public async Task ReadAsync_ParsesOneToManyRelationships()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""contact"" displayname=""Contact"" primaryidfield=""contactid"" primarynamefield=""fullname"">
    <fields />
    <relationships>
      <relationship name=""account_primary_contact"" manyToMany=""false"" relatedEntityName=""account""
                   referencingEntity=""contact"" referencingAttribute=""parentcustomerid""
                   referencedEntity=""account"" referencedAttribute=""accountid"" />
    </relationships>
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        var entity = schema.Entities[0];
        entity.Relationships.Should().HaveCount(1);
        var rel = entity.Relationships[0];
        rel.Name.Should().Be("account_primary_contact");
        rel.IsManyToMany.Should().BeFalse();
        rel.Entity1.Should().Be("contact");
        rel.Entity1Attribute.Should().Be("parentcustomerid");
        rel.Entity2.Should().Be("account");
        rel.Entity2Attribute.Should().Be("accountid");
    }

    [Fact]
    public async Task ReadAsync_ParsesManyToManyRelationships()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""systemuser"" displayname=""User"" primaryidfield=""systemuserid"" primarynamefield=""fullname"">
    <fields />
    <relationships>
      <relationship name=""systemuserroles_association"" manyToMany=""true"" isreflexive=""false""
                   relatedEntityName=""systemuserroles"" m2mTargetEntity=""role""
                   m2mTargetEntityPrimaryKey=""roleid"" />
    </relationships>
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        var entity = schema.Entities[0];
        entity.Relationships.Should().HaveCount(1);
        var rel = entity.Relationships[0];
        rel.Name.Should().Be("systemuserroles_association");
        rel.IsManyToMany.Should().BeTrue();
        rel.IntersectEntity.Should().Be("systemuserroles");
        rel.Entity2.Should().Be("role");
        rel.TargetEntityPrimaryKey.Should().Be("roleid");
        rel.IsReflexive.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_ParsesFetchXmlFilter()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" primaryidfield=""accountid"" primarynamefield=""name"">
    <fields />
    <relationships />
    <filter>&lt;filter&gt;&lt;condition attribute='statecode' operator='eq' value='0'/&gt;&lt;/filter&gt;</filter>
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        var entity = schema.Entities[0];
        entity.FetchXmlFilter.Should().NotBeNullOrEmpty();
        entity.FetchXmlFilter.Should().Contain("statecode");
    }

    [Fact]
    public async Task ReadAsync_ParsesMultipleEntities()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" primaryidfield=""accountid"" primarynamefield=""name"">
    <fields />
    <relationships />
  </entity>
  <entity name=""contact"" displayname=""Contact"" primaryidfield=""contactid"" primarynamefield=""fullname"">
    <fields />
    <relationships />
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        schema.Entities.Should().HaveCount(2);
        schema.Entities[0].LogicalName.Should().Be("account");
        schema.Entities[1].LogicalName.Should().Be("contact");
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenStreamIsNull()
    {
        var reader = new CmtSchemaReader();

        var act = async () => await reader.ReadAsync((Stream)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenPathIsNull()
    {
        var reader = new CmtSchemaReader();

        var act = async () => await reader.ReadAsync((string)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenFileNotFound()
    {
        var reader = new CmtSchemaReader();

        var act = async () => await reader.ReadAsync("nonexistent.xml");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ReadAsync_ParsesVersionAndTimestamp()
    {
        var xml = @"<?xml version=""1.0""?>
<entities version=""1.0"" timestamp=""2025-01-01T00:00:00Z"">
  <entity name=""account"" displayname=""Account"" primaryidfield=""accountid"" primarynamefield=""name"">
    <fields />
    <relationships />
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        schema.Version.Should().Be("1.0");
        schema.GeneratedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadAsync_DefaultsToV1WhenNoVersion()
    {
        var xml = @"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" primaryidfield=""accountid"" primarynamefield=""name"">
    <fields />
    <relationships />
  </entity>
</entities>";

        var reader = new CmtSchemaReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var schema = await reader.ReadAsync(stream);

        schema.Version.Should().Be("1.0");
    }
}
