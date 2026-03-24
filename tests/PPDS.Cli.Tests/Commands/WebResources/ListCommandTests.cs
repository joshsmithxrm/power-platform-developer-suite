using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class ListCommandTests
{
    private readonly Command _command;

    public ListCommandTests()
    {
        _command = ListCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("list", _command.Name);
    }

    [Fact]
    public void Create_HasOptionalNamePatternArgument()
    {
        var arg = _command.Arguments.FirstOrDefault(a => a.Name == "name-pattern");
        Assert.NotNull(arg);
        // Optional argument — Arity.ZeroOrOne
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasTypeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasTopOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--top");
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
