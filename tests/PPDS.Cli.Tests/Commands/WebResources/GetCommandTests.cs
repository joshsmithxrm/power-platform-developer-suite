using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class GetCommandTests
{
    private readonly Command _command;

    public GetCommandTests()
    {
        _command = GetCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("get", _command.Name);
    }

    [Fact]
    public void Create_HasNameArgument()
    {
        Assert.Single(_command.Arguments);
        Assert.Equal("name", _command.Arguments[0].Name);
    }

    [Fact]
    public void Create_HasUnpublishedOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--unpublished");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasOutputOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasGlobalOptions()
    {
        var formatOption = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(formatOption);
    }
}
