using FluentAssertions;
using PPDS.Migration.Schema;
using Xunit;

namespace PPDS.Migration.Tests.Models;

public class SchemaGeneratorOptionsTests
{
    [Fact]
    public void EntityFilters_DefaultsToNull()
    {
        var options = new SchemaGeneratorOptions();

        options.EntityFilters.Should().BeNull();
    }

    [Fact]
    public void EntityFilters_CanBeSet()
    {
        var filters = new Dictionary<string, string>
        {
            ["account"] = "<filter><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter>"
        };

        var options = new SchemaGeneratorOptions
        {
            EntityFilters = filters
        };

        options.EntityFilters.Should().ContainKey("account");
        options.EntityFilters!["account"].Should().Contain("statecode");
    }
}
