using System.CommandLine;
using PPDS.Cli.Commands.Publish;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Publish;

public class PublishCommandGroupTests
{
    private readonly Command _command;

    public PublishCommandGroupTests()
    {
        _command = PublishCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("publish", _command.Name);
    }

    [Fact]
    public void Create_HasAllOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--all");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasTypeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasNamesArgument()
    {
        var arg = _command.Arguments.FirstOrDefault(a => a.Name == "names");
        Assert.NotNull(arg);
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

    [Fact]
    public void Create_HasValidator()
    {
        // The command should have validators for flag combination rules
        Assert.NotEmpty(_command.Validators);
    }
}
