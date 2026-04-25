using System.CommandLine;
using PPDS.Cli.Commands.Query;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

[Trait("Category", "Unit")]
public class QueryCommandGroupTests
{
    private readonly Command _command;

    public QueryCommandGroupTests()
    {
        _command = QueryCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("query", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.Contains("queries", _command.Description!.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasExactlyFourSubcommands()
    {
        Assert.Equal(4, _command.Subcommands.Count);
    }

    [Fact]
    public void Create_HasAllSubcommands()
    {
        var names = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("fetch", names);
        Assert.Contains("sql", names);
        Assert.Contains("explain", names);
        Assert.Contains("history", names);
    }
}

[Trait("Category", "Unit")]
public class QuerySharedOptionsTests
{
    [Fact]
    public void ProfileOption_HasCorrectNameAndAlias()
    {
        Assert.Equal("--profile", QueryCommandGroup.ProfileOption.Name);
        Assert.Contains("-p", QueryCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void ProfileOption_IsNotRequired()
    {
        Assert.False(QueryCommandGroup.ProfileOption.Required);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectNameAndAlias()
    {
        Assert.Equal("--environment", QueryCommandGroup.EnvironmentOption.Name);
        Assert.Contains("-env", QueryCommandGroup.EnvironmentOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_IsNotRequired()
    {
        Assert.False(QueryCommandGroup.EnvironmentOption.Required);
    }

    [Fact]
    public void TopOption_HasCorrectNameAndAlias()
    {
        Assert.Equal("--top", QueryCommandGroup.TopOption.Name);
        Assert.Contains("-t", QueryCommandGroup.TopOption.Aliases);
    }

    [Fact]
    public void TopOption_IsNotRequired()
    {
        Assert.False(QueryCommandGroup.TopOption.Required);
    }

    [Fact]
    public void PageOption_HasCorrectName()
    {
        Assert.Equal("--page", QueryCommandGroup.PageOption.Name);
    }

    [Fact]
    public void PageOption_IsNotRequired()
    {
        Assert.False(QueryCommandGroup.PageOption.Required);
    }

    [Fact]
    public void PagingCookieOption_HasCorrectName()
    {
        Assert.Equal("--paging-cookie", QueryCommandGroup.PagingCookieOption.Name);
    }

    [Fact]
    public void PagingCookieOption_IsNotRequired()
    {
        Assert.False(QueryCommandGroup.PagingCookieOption.Required);
    }

    [Fact]
    public void CountOption_HasCorrectNameAndAlias()
    {
        Assert.Equal("--count", QueryCommandGroup.CountOption.Name);
        Assert.Contains("-c", QueryCommandGroup.CountOption.Aliases);
    }

    [Fact]
    public void CountOption_IsNotRequired()
    {
        Assert.False(QueryCommandGroup.CountOption.Required);
    }
}
