using System.Xml.Schema;
using PPDS.Cli.Services.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Services.ModelDrivenApps;

public class SitemapSchemaResourcesTests
{
    private readonly SitemapSchemaResources _resources = new();

    [Fact]
    public void LoadSchemaSet_ReturnsCompiledSchemaSet()
    {
        var schemaSet = _resources.LoadSchemaSet();

        Assert.NotNull(schemaSet);
        Assert.True(schemaSet.IsCompiled);
    }

    [Fact]
    public void LoadSchemaSet_ContainsSiteMapSchema()
    {
        var schemaSet = _resources.LoadSchemaSet();

        Assert.True(schemaSet.Count > 0);
    }

    [Fact]
    public void LoadSchemaSet_CalledTwice_ReturnsCachedInstance()
    {
        var set1 = _resources.LoadSchemaSet();
        var set2 = _resources.LoadSchemaSet();

        Assert.Same(set1, set2);
    }
}
