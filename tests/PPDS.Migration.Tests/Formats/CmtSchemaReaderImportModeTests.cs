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
    private static async Task<PPDS.Migration.Models.MigrationSchema> ReadAsync(string entityAttributes, string fieldAttributes = "")
    {
        var xml = $@"<?xml version=""1.0""?>
<entities>
  <entity name=""account"" displayname=""Account"" etc=""1"" primaryidfield=""accountid"" primarynamefield=""name"" {entityAttributes}>
    <fields>
      <field displayname=""Account ID"" name=""accountid"" type=""uniqueidentifier"" primaryKey=""true"" />
      <field displayname=""Created"" name=""createdon"" type=""datetime"" {fieldAttributes} />
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
        var schema = await ReadAsync(attribute);

        // An import-only problem: recorded for the import to refuse, not thrown (an export reads the schema too).
        schema.ImportIssues.Should().ContainSingle().Which.Should().Contain("account").And.Contain("create, update, upsert or skip");
    }

    [Fact]
    public async Task TwoImportModeAttributesInDifferentCase_AreRefused()
    {
        var schema = await ReadAsync(@"importMode=""skip"" importmode=""upsert""");

        schema.ImportIssues.Should().ContainSingle();
    }

    // #1914 review should-fix 3: CMT attributes PPDS does not apply.
    [Theory]
    [InlineData(@"skipupdate=""true""")]
    [InlineData(@"SkipUpdate=""1""")]
    [InlineData(@"forcecreate=""true""")]
    [InlineData(@"skipupdate=""maybe""")]
    public async Task CmtSkipUpdateOrForceCreate_AskingForSomething_IsRefused(string attribute)
    {
        var schema = await ReadAsync(attribute);

        // An import-only problem: recorded for the import to refuse, not thrown (an export reads the schema too).
        schema.ImportIssues.Should().ContainSingle().Which.Should().Contain("account").And.Contain("which PPDS does not apply").And.Contain("importMode");
    }

    [Theory]
    [InlineData(@"skipupdate=""false""")]
    [InlineData(@"forcecreate=""0""")]
    public async Task CmtSkipUpdateOrForceCreate_False_IsAccepted(string attribute)
    {
        var schema = await ReadAsync(attribute);

        schema.Entities[0].ImportMode.Should().BeNull();
    }

    // #1921 review should-fix 4: disableplugins values.
    [Theory]
    [InlineData(@"disableplugins=""true""", true)]
    [InlineData(@"disableplugins="" True """, true)]
    [InlineData(@"DisablePlugins=""1""", true)]
    [InlineData(@"disableplugins=""yes""", true)]
    [InlineData(@"disableplugins=""false""", false)]
    [InlineData(@"disableplugins=""0""", false)]
    [InlineData(@"disableplugins=""no""", false)]
    [InlineData("", false)]
    public async Task DisablePlugins_AcceptedSpellings(string attribute, bool expected)
    {
        var schema = await ReadAsync(attribute);

        schema.Entities[0].DisablePlugins.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"disableplugins=""on""")]
    [InlineData(@"disableplugins=""""")]
    [InlineData(@"disableplugins=""2""")]
    [InlineData(@"disableplugins=""true"" DISABLEPLUGINS=""false""")]
    public async Task DisablePlugins_AnyOtherValue_IsRefused(string attribute)
    {
        var schema = await ReadAsync(attribute);

        schema.ImportIssues.Should().ContainSingle();
    }

    // #1914 review should-fix 5: dateMode values.
    [Theory]
    [InlineData(@"dateMode=""absolute""", PPDS.Migration.Models.DateMode.Absolute)]
    [InlineData(@"dateMode=""relative""", PPDS.Migration.Models.DateMode.Relative)]
    [InlineData(@"datemode=""RelativeDaily""", PPDS.Migration.Models.DateMode.RelativeDaily)]
    [InlineData(@"dateMode="" relativeExact """, PPDS.Migration.Models.DateMode.RelativeExact)]
    [InlineData("", PPDS.Migration.Models.DateMode.Absolute)]
    public async Task DateMode_AcceptedSpellings(string attribute, PPDS.Migration.Models.DateMode expected)
    {
        var schema = await ReadAsync("", attribute);

        schema.Entities[0].Fields.Single(f => f.LogicalName == "createdon").DateMode.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"dateMode=""relativeweekly""")]
    [InlineData(@"dateMode=""""")]
    [InlineData(@"dateMode=""1""")]
    public async Task DateMode_AnyOtherValue_IsRefused(string attribute)
    {
        var schema = await ReadAsync("", attribute);

        // An import-only problem: recorded for the import to refuse, not thrown (an export reads the schema too).
        schema.ImportIssues.Should().ContainSingle().Which.Should().Contain("createdon");
    }

    [Fact]
    public async Task ASchemaWithImportIssues_StillReads_ForAnExport()
    {
        // #1934 r2 should-fix 4: skipupdate concerns only an import; the schema stays usable for an export.
        var schema = await ReadAsync(@"skipupdate=""true"" importMode=""skp""", @"dateMode=""weekly""");

        schema.Entities.Should().ContainSingle().Which.LogicalName.Should().Be("account");
        schema.ImportIssues.Should().HaveCount(3);
    }

    [Fact]
    public async Task ACleanSchema_HasNoImportIssues()
    {
        var schema = await ReadAsync(@"importMode=""skip"" disableplugins=""true""", @"dateMode=""relative""");

        schema.ImportIssues.Should().BeEmpty();
    }
}
