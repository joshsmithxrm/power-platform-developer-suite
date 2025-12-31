using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class ListCommandTests
{
    private readonly Command _command;

    public ListCommandTests()
    {
        _command = ListCommand.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("list", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("List", _command.Description);
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
    public void Create_HasOptionalAssemblyOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--assembly");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalPackageOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--package");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-pkg", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalJsonOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--json");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithNoOptions_Succeeds()
    {
        var result = _command.Parse("");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalProfile_Succeeds()
    {
        var result = _command.Parse("--profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalEnvironment_Succeeds()
    {
        var result = _command.Parse("--environment https://org.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalAssembly_Succeeds()
    {
        var result = _command.Parse("--assembly MyPlugins");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalPackage_Succeeds()
    {
        var result = _command.Parse("--package MyPackage");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithPackageShortAlias_Succeeds()
    {
        var result = _command.Parse("-pkg MyPackage");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse("--json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            "--profile dev " +
            "--environment https://org.crm.dynamics.com " +
            "--assembly MyPlugins " +
            "--json");
        Assert.Empty(result.Errors);
    }

    #endregion
}
