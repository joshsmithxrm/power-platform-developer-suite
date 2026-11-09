using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

public partial class PluginReconciliationWriteTests
{
    [Fact]
    public async Task Compatibility_UpsertStep_EmptyLegacyPlaceholdersStillPreserve()
    {
        var config = Step();
        config.Description = "";
        config.FilteringAttributes = "";
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        var write = Assert.Single(_writes);
        Assert.False(write.Contains("description"));
        Assert.False(write.Contains("filteringattributes"));
    }

    [Fact]
    public async Task Compatibility_UpsertImage_EmptyLegacyPlaceholderStillPreserves()
    {
        await _service.UpsertImageAsync(Guid.NewGuid(),
            new PluginImageConfig { Name = "before", ImageType = "PreImage", Attributes = "" }, "Update");
        var write = Assert.Single(_writes);
        Assert.False(write.Contains("attributes"));
    }

    [Fact]
    public async Task Compatibility_UpdateStep_EmptyDescriptionWritesExactEmpty()
    {
        await _service.UpdateStepAsync(_existing.Id, new StepUpdateRequest(Description: ""));
        var write = Assert.Single(_writes);
        Assert.True(write.Contains("description"));
        Assert.Equal("", Assert.IsType<string>(write["description"]));
        Assert.False(write.Contains("filteringattributes"));
    }

    [Fact]
    public async Task Compatibility_UpdateStep_EmptyFilteringAttributesWritesExactEmpty()
    {
        await _service.UpdateStepAsync(_existing.Id, new StepUpdateRequest(FilteringAttributes: ""));
        var write = Assert.Single(_writes);
        Assert.True(write.Contains("filteringattributes"));
        Assert.Equal("", Assert.IsType<string>(write["filteringattributes"]));
        Assert.False(write.Contains("description"));
    }

    [Fact]
    public async Task Compatibility_UpdateImage_EmptyAttributesWritesExactEmpty()
    {
        await _service.UpdateImageAsync(_existing.Id, new ImageUpdateRequest(Attributes: ""));
        var write = Assert.Single(_writes);
        Assert.True(write.Contains("attributes"));
        Assert.Equal("", Assert.IsType<string>(write["attributes"]));
        Assert.False(write.Contains("description"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatibility_UpdateStep_NullOrOmittedOptionalFieldsPreserve(bool explicitNull)
    {
        var request = explicitNull
            ? new StepUpdateRequest(Rank: 7, Description: null, FilteringAttributes: null)
            : new StepUpdateRequest(Rank: 7);
        await _service.UpdateStepAsync(_existing.Id, request);
        var write = Assert.Single(_writes);
        Assert.Equal(7, write["rank"]);
        Assert.False(write.Contains("description"));
        Assert.False(write.Contains("filteringattributes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatibility_UpdateImage_NullOrOmittedAttributesPreserve(bool explicitNull)
    {
        var request = explicitNull
            ? new ImageUpdateRequest(Attributes: null, Name: "renamed")
            : new ImageUpdateRequest(Name: "renamed");
        await _service.UpdateImageAsync(_existing.Id, request);
        var write = Assert.Single(_writes);
        Assert.Equal("renamed", write["name"]);
        Assert.False(write.Contains("attributes"));
    }
}
