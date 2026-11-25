using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.CsvLoader;
using Xunit;
using static PPDS.Cli.Tests.Services.Data.ColumnValueTestSupport;

namespace PPDS.Cli.Tests.CsvLoader;

/// <summary>
/// <c>data load --generate-mapping</c> gives a lookup to several tables no table (review round 1, must-fix 2, the
/// generator's site; review round 2, mutant m75), so <c>data load</c> refuses the column until the mapping names one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MappingGeneratorLookupTests : IDisposable
{
    private readonly TempCsvFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task A_lookup_to_several_tables_gets_no_table_and_needs_configuration()
    {
        var metadata = new EntityMetadata { LogicalName = "test_widget" };
        AttributeMetadata[] columns =
        [
            new LookupAttributeMetadata { LogicalName = "test_customerid", Targets = ["account", "contact"], IsValidForUpdate = true },
            new LookupAttributeMetadata { LogicalName = "test_accountid", Targets = ["account"], IsValidForUpdate = true },
        ];
        typeof(EntityMetadata).GetProperty("Attributes")!.SetValue(metadata, columns);
        var csv = _files.Csv("test_customerid,test_accountid\n8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab,8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab\n");

        var config = await new MappingGenerator().GenerateAsync(csv, metadata);

        var customer = config.Columns["test_customerid"];
        Assert.Equal("", customer.Lookup!.Entity);
        Assert.Equal("needs-configuration", customer.Status);
        Assert.Contains("account, contact", customer.Note, StringComparison.Ordinal);

        var account = config.Columns["test_accountid"];
        Assert.Equal("account", account.Lookup!.Entity);
        Assert.Equal("auto-matched", account.Status);
    }
}
