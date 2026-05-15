using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Schema.Snapshots;
using PPDS.Migration.Formats;
using Xunit;

namespace PPDS.Cli.Tests.Services.Schema;

public class DataPackageSnapshotLoaderTests
{
    private const string SampleSchemaXml = """
        <entities version="1.0">
          <entity name="account" displayname="Account" etc="1">
            <fields>
              <field displayname="Name" name="name" type="string" maxlength="160" isrequired="true" />
              <field displayname="Revenue" name="revenue" type="money" precision="2" />
              <field displayname="Primary Contact" name="primarycontactid" type="lookup" lookupType="contact" />
            </fields>
            <relationships>
              <relationship name="account_contacts" referencingEntity="contact" referencedEntity="account" />
            </relationships>
          </entity>
          <entity name="contact" displayname="Contact" />
        </entities>
        """;

    private static async Task<string> CreateZipAsync(string contents, string entryName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"schema-test-{System.Guid.NewGuid():N}.zip");
        await using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(entryName);
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(contents);
        }
        return path;
    }

    [Fact]
    public async Task LoadAsync_WithDataSchemaXml_ReadsEntities()
    {
        var path = await CreateZipAsync(SampleSchemaXml, "data_schema.xml");
        try
        {
            var loader = new DataPackageSnapshotLoader(path, new CmtSchemaReader());

            var snapshot = await loader.LoadAsync();

            snapshot.IncludesOptionSetValues.Should().BeFalse();
            snapshot.Source.Should().StartWith("data:").And.EndWith(".zip");
            snapshot.Entities.Should().HaveCount(2);

            var account = snapshot.Entities.First(e => e.LogicalName == "account");
            account.Attributes.Should().HaveCount(3);

            var name = account.Attributes.First(a => a.LogicalName == "name");
            name.AttributeType.Should().Be("string");
            name.MaxLength.Should().Be(160);
            name.RequiredLevel.Should().Be("ApplicationRequired");

            var lookup = account.Attributes.First(a => a.LogicalName == "primarycontactid");
            lookup.AttributeType.Should().Be("lookup");
            lookup.LookupTargets.Should().NotBeNull();
            // PPDS.Migration.Formats.CmtSchemaReader maps "lookupType" attribute to LookupEntity.

            account.Relationships.Should().ContainSingle()
                .Which.SchemaName.Should().Be("account_contacts");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_AcceptsLegacySchemaXmlEntryName()
    {
        var path = await CreateZipAsync(SampleSchemaXml, "schema.xml");
        try
        {
            var loader = new DataPackageSnapshotLoader(path, new CmtSchemaReader());

            var snapshot = await loader.LoadAsync();

            snapshot.Entities.Should().HaveCount(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_FileMissing_ThrowsPpdsExceptionWithFileNotFoundCode()
    {
        var loader = new DataPackageSnapshotLoader("Z:/does-not-exist.zip", new CmtSchemaReader());

        var ex = await Assert.ThrowsAsync<PpdsException>(() => loader.LoadAsync());
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.FileNotFound);
    }

    [Fact]
    public async Task LoadAsync_ZipMissingSchemaEntry_ThrowsPpdsExceptionWithSchemaInvalid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schema-test-{System.Guid.NewGuid():N}.zip");
        await using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }

        try
        {
            var loader = new DataPackageSnapshotLoader(path, new CmtSchemaReader());

            var ex = await Assert.ThrowsAsync<PpdsException>(() => loader.LoadAsync());
            ex.ErrorCode.Should().Be(ErrorCodes.Validation.SchemaInvalid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_WithEntityFilter_RestrictsResult()
    {
        var path = await CreateZipAsync(SampleSchemaXml, "data_schema.xml");
        try
        {
            var loader = new DataPackageSnapshotLoader(path, new CmtSchemaReader());

            var snapshot = await loader.LoadAsync(entityFilter: new[] { "account" });

            snapshot.Entities.Should().HaveCount(1)
                .And.Subject.Single().LogicalName.Should().Be("account");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
