using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class GetCommandTests
{
    private readonly Command _command;

    public GetCommandTests()
    {
        _command = GetCommand.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("get", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Get", _command.Description);
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
    [InlineData("package", "MyPackage")]
    [InlineData("type", "MyPlugin.Plugins.AccountPlugin")]
    [InlineData("step", "MyPlugin: Create of account")]
    [InlineData("image", "PreImage")]
    public void Parse_WithValidTypeAndName_Succeeds(string type, string name)
    {
        var result = _command.Parse($"{type} \"{name}\"");
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("package")]
    [InlineData("type")]
    [InlineData("step")]
    [InlineData("image")]
    public void Parse_WithValidTypeAndGuid_Succeeds(string type)
    {
        var result = _command.Parse($"{type} 12345678-1234-1234-1234-123456789abc");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithInvalidType_HasErrors()
    {
        var result = _command.Parse("invalid MyPlugin");
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
            "--profile dev " +
            "--environment https://org.crm.dynamics.com " +
            "--output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingTypeArgument_HasErrors()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingNameOrIdArgument_HasErrors()
    {
        var result = _command.Parse("assembly");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Type Validation Tests

    [Theory]
    [InlineData("ASSEMBLY")]
    [InlineData("Assembly")]
    [InlineData("PACKAGE")]
    [InlineData("TYPE")]
    [InlineData("STEP")]
    [InlineData("IMAGE")]
    public void Parse_WithUppercaseType_Succeeds(string type)
    {
        // Type validation is case-insensitive for better UX
        var result = _command.Parse($"{type} MyPlugin");
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("assemblies")]
    [InlineData("packages")]
    [InlineData("types")]
    [InlineData("steps")]
    [InlineData("images")]
    public void Parse_WithPluralType_HasErrors(string type)
    {
        var result = _command.Parse($"{type} MyPlugin");
        Assert.NotEmpty(result.Errors);
    }

    #endregion
}
