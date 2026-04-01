using System.CommandLine;
using PPDS.Cli.Commands.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
public class MetadataPublishAliasTests
{
    private readonly Command _command;

    public MetadataPublishAliasTests()
    {
        _command = PublishAliasCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("publish", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("entity", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasNamesArgument()
    {
        var arg = _command.Arguments.FirstOrDefault(a => a.Name == "names");
        Assert.NotNull(arg);
    }

    [Fact]
    public void Create_NamesArgumentIsOptional()
    {
        var arg = _command.Arguments.FirstOrDefault(a => a.Name == "names");
        Assert.NotNull(arg);
        Assert.Equal(0, arg.Arity.MinimumNumberOfValues);
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
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

    [Fact]
    public void Parse_WithEntityNames_Succeeds()
    {
        var result = _command.Parse("account contact");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithSolutionOption_Succeeds()
    {
        var result = _command.Parse("--solution MySolution");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("account --solution MySolution --profile dev --environment https://org.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithNoArguments_Succeeds()
    {
        // Bare command is allowed — validation happens in ExecuteAsync
        var result = _command.Parse("");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void MetadataCommandGroup_ContainsPublishSubcommand()
    {
        var metadataCommand = MetadataCommandGroup.Create();
        var publishSubcommand = metadataCommand.Subcommands.FirstOrDefault(c => c.Name == "publish");
        Assert.NotNull(publishSubcommand);
    }
}
