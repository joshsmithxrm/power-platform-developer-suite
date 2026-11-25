using System.Text;
using FluentAssertions;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Migration.Tests.Formats;

/// <summary>
/// A per-table <c>importMode</c> the reader does not recognise must not fall back to the global
/// mode (Upsert by default), which would overwrite the existing records of a table the user meant to
/// skip (#1914 sibling).
/// </summary>
[Trait("Category", "Unit")]
public class CmtSchemaReaderImportModeTests
{
    private static async Task<PPDS.Migration.Models.MigrationSchema> ReadAsync(string entityAttributes)
    {
        var xml = $@"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" etc=""1"" primaryidfield=""accountid"" primarynamefield=""name"" {entityAttributes}>
    <fields>
      <field displayname=""Account ID"" name=""accountid"" type=""uniqueidentifier"" primaryKey=""true"" />
    </fields>
    <relationships />
  </entity>
</entities>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await new CmtSchemaReader().ReadAsync(stream);
    }

    [Theory]
    [InlineData(@"importMode=""create""", ImportMode.Create)]
    [InlineData(@"importMode=""update""", ImportMode.Update)]
    [InlineData(@"importMode=""upsert""", ImportMode.Upsert)]
    [InlineData(@"importMode=""skip""", ImportMode.Skip)]
    [InlineData(@"importMode=""Skip""", ImportMode.Skip)]
    [InlineData(@"importMode="" skip """, ImportMode.Skip)]
    [InlineData(@"importmode=""skip""", ImportMode.Skip)]
    [InlineData(@"IMPORTMODE=""skip""", ImportMode.Skip)]
    public async Task KnownModes_AreRead(string attribute, ImportMode expected)
    {
        var schema = await ReadAsync(attribute);

        schema.Entities[0].ImportMode.Should().Be(expected);
    }

    [Fact]
    public async Task NoImportMode_FallsBackToTheGlobalMode()
    {
        var schema = await ReadAsync("");

        schema.Entities[0].ImportMode.Should().BeNull();
    }

    [Theory]
    [InlineData(@"importMode=""skp""")]
    [InlineData(@"importMode=""skipupdate""")]
    [InlineData(@"importMode=""""")]
    [InlineData(@"importMode=""3""")]
    public async Task UnknownMode_IsRefused_NotTreatedAsTheGlobalMode(string attribute)
    {
        var act = () => ReadAsync(attribute);

        var ex = await act.Should().ThrowAsync<InvalidDataException>();
        ex.Which.Message.Should().Contain("account").And.Contain("create, update, upsert or skip");
    }

    [Fact]
    public async Task TwoImportModeAttributesInDifferentCase_AreRefused()
    {
        var act = () => ReadAsync(@"importMode=""skip"" importmode=""upsert""");

        await act.Should().ThrowAsync<InvalidDataException>();
    }
}
