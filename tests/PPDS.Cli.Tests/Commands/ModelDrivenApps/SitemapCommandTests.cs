using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class SitemapCommandTests
{
    private readonly Command _command;

    public SitemapCommandTests()
    {
        _command = SitemapCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("sitemap", _command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        Assert.False(string.IsNullOrEmpty(_command.Description));
    }

    [Fact]
    public void Create_HasAppOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--app");
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--profile");
    }
}
