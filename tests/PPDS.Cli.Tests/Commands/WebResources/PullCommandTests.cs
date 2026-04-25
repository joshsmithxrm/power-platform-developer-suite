using System.CommandLine;
using FluentAssertions;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class PullCommandTests
{
    private readonly Command _command = PullCommand.Create();

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        _command.Name.Should().Be("pull");
    }

    [Fact]
    public void Create_HasFolderArgument()
    {
        _command.Arguments.Should().ContainSingle();
        _command.Arguments[0].Name.Should().Be("folder");
    }

    [Theory]
    [InlineData("--solution")]
    [InlineData("--type")]
    [InlineData("--name")]
    [InlineData("--strip-prefix")]
    [InlineData("--force")]
    [InlineData("--profile")]
    [InlineData("--environment")]
    public void Create_HasOption(string optionName)
    {
        _command.Options.Should().Contain(o => o.Name == optionName);
    }

    [Fact]
    public void Create_RegisteredInWebResourcesCommandGroup()
    {
        var group = WebResourcesCommandGroup.Create();
        group.Subcommands.Should().Contain(c => c.Name == "pull");
    }
}
