using PPDS.Cli.Commands.WebResources;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class WebResourceNameResolverTests
{
    private static readonly List<WebResourceInfo> TestResources =
    [
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "new_/scripts/app.js", "App Script", 3, false, "Josh", DateTime.UtcNow, "Josh", DateTime.UtcNow),
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "new_/scripts/utils.js", "Utils", 3, false, "Josh", DateTime.UtcNow, "Josh", DateTime.UtcNow),
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "new_/styles/main.css", "Main CSS", 2, false, "Jane", DateTime.UtcNow, "Jane", DateTime.UtcNow),
        new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "other_/scripts/app.js", "Other App", 3, false, "Josh", DateTime.UtcNow, "Josh", DateTime.UtcNow),
    ];

    [Fact]
    public void Resolve_WithGuid_ReturnsExactMatch()
    {
        var result = WebResourceNameResolver.Resolve("11111111-1111-1111-1111-111111111111", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
        Assert.Equal("new_/scripts/app.js", result.Matches[0].Name);
    }

    [Fact]
    public void Resolve_WithGuid_NotFound_ReturnsFailure()
    {
        var result = WebResourceNameResolver.Resolve("99999999-9999-9999-9999-999999999999", TestResources);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Resolve_WithExactName_ReturnsExactMatch()
    {
        var result = WebResourceNameResolver.Resolve("new_/scripts/app.js", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.Matches[0].Id);
    }

    [Fact]
    public void Resolve_WithPartialName_SingleMatch_ReturnsSuccess()
    {
        var result = WebResourceNameResolver.Resolve("utils.js", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
        Assert.Equal("new_/scripts/utils.js", result.Matches[0].Name);
    }

    [Fact]
    public void Resolve_WithPartialName_MultipleMatches_ReturnsAllMatches()
    {
        var result = WebResourceNameResolver.Resolve("app.js", TestResources);
        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public void Resolve_WithPartialName_NoMatch_ReturnsFailure()
    {
        var result = WebResourceNameResolver.Resolve("notfound.js", TestResources);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var result = WebResourceNameResolver.Resolve("MAIN.CSS", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void Filter_WithPartialName_ReturnsAllMatches()
    {
        var result = WebResourceNameResolver.Filter("app.js", TestResources);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_WithPrefix_ReturnsAllUnderPrefix()
    {
        var result = WebResourceNameResolver.Filter("new_/scripts/", TestResources);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_WithNoMatch_ReturnsEmpty()
    {
        var result = WebResourceNameResolver.Filter("notfound", TestResources);
        Assert.Empty(result);
    }
}
