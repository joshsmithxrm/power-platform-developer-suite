using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class SetSitemapXmlCommandTests
{
    private readonly Command _command;

    public SetSitemapXmlCommandTests()
    {
        _command = SetSitemapXmlCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("set-sitemap-xml", _command.Name);
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
    public void Create_HasXmlOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--xml");
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--solution");
    }

    [Fact]
    public void Create_HasPublishOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--publish");
    }
}
