using System.CommandLine;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class PluginsCommandGroupTests
{
    private readonly Command _command;

    public PluginsCommandGroupTests()
    {
        _command = PluginsCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("plugins", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Plugin", _command.Description);
    }

    [Fact]
    public void Create_HasExtractSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "extract");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasDeploySubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "deploy");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasDiffSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "diff");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasListSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "list");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasCleanSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "clean");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasDownloadSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "download");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasSixSubcommands()
    {
        Assert.Equal(6, _command.Subcommands.Count);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", PluginsCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", PluginsCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", PluginsCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-env", PluginsCommandGroup.EnvironmentOption.Aliases);
    }

    [Fact]
    public void SolutionOption_HasCorrectName()
    {
        Assert.Equal("--solution", PluginsCommandGroup.SolutionOption.Name);
    }

    [Fact]
    public void SolutionOption_HasShortAlias()
    {
        Assert.Contains("-s", PluginsCommandGroup.SolutionOption.Aliases);
    }

    // Note: OutputFormatOption tests removed - option is now provided by GlobalOptions

    #endregion
}
