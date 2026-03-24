using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class WebResourcesCommandGroupTests
{
    private readonly Command _command;

    public WebResourcesCommandGroupTests()
    {
        _command = WebResourcesCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("webresources", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("web resource", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasListSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "list");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasGetSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "get");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasUrlSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "url");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasPublishSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "publish");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", WebResourcesCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", WebResourcesCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", WebResourcesCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", WebResourcesCommandGroup.EnvironmentOption.Aliases);
    }
}
