using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class UnregisterCommandTests
{
    private readonly Command _command;

    public UnregisterCommandTests()
    {
        _command = UnregisterCommand.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("unregister", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Unregister", _command.Description);
    }

    [Fact]
    public void Create_HasTypeArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "type");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Create_HasNameOrIdArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "name-or-id");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Create_HasOptionalForceOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-f", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    #endregion

    #region Argument Parsing Tests

    [Theory]
    [InlineData("assembly", "MyPlugin")]
    [InlineData("package", "MyPlugin.Plugins")]
    [InlineData("type", "MyNamespace.CreateHandler")]
    [InlineData("step", "MyPlugin: Create of account")]
    [InlineData("image", "MyPlugin: Create PreImage")]
    public void Parse_WithValidTypeAndName_Succeeds(string entityType, string nameOrId)
    {
        var result = _command.Parse($"{entityType} \"{nameOrId}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithGuid_Succeeds()
    {
        var guid = Guid.NewGuid();
        var result = _command.Parse($"step {guid}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithForceOption_Succeeds()
    {
        var result = _command.Parse("assembly MyPlugin --force");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithForceShortAlias_Succeeds()
    {
        var result = _command.Parse("assembly MyPlugin -f");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingArguments_HasError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingNameOrId_HasError()
    {
        var result = _command.Parse("assembly");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalProfile_Succeeds()
    {
        var result = _command.Parse("assembly MyPlugin --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalEnvironment_Succeeds()
    {
        var result = _command.Parse("assembly MyPlugin --environment https://org.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse("assembly MyPlugin --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            "assembly MyPlugin " +
            "--force " +
            "--profile dev " +
            "--environment https://org.crm.dynamics.com " +
            "--output-format Json");
        Assert.Empty(result.Errors);
    }

    #endregion
}
