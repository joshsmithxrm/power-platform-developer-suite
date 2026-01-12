using System.CommandLine;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class DownloadCommandTests
{
    private readonly Command _command;

    public DownloadCommandTests()
    {
        _command = DownloadCommand.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("download", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Download", _command.Description);
    }

    [Fact]
    public void Create_HasAssemblySubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "assembly");
        Assert.NotNull(subcommand);
        Assert.Contains("DLL", subcommand.Description);
    }

    [Fact]
    public void Create_HasPackageSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "package");
        Assert.NotNull(subcommand);
        Assert.Contains("nupkg", subcommand.Description);
    }

    #endregion

    #region Assembly Subcommand Tests

    [Fact]
    public void AssemblySubcommand_HasNameOrIdArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "name-or-id");
        Assert.NotNull(argument);
    }

    [Fact]
    public void AssemblySubcommand_HasRequiredOutputOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--output");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void AssemblySubcommand_HasOptionalForceOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void AssemblySubcommand_HasOptionalProfileOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void AssemblySubcommand_HasOptionalEnvironmentOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithRequiredOptions_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var result = subcommand.Parse("MyPlugin --output ./recovered/");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithForce_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var result = subcommand.Parse("MyPlugin --output ./MyPlugin.dll --force");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithGuid_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var result = subcommand.Parse("12345678-1234-1234-1234-123456789abc --output ./recovered/");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithProfile_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var result = subcommand.Parse("MyPlugin --output ./recovered/ --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithJsonOutput_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var result = subcommand.Parse("MyPlugin --output ./recovered/ --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithoutOutput_HasErrors()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var result = subcommand.Parse("MyPlugin");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Package Subcommand Tests

    [Fact]
    public void PackageSubcommand_HasNameOrIdArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "name-or-id");
        Assert.NotNull(argument);
    }

    [Fact]
    public void PackageSubcommand_HasRequiredOutputOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--output");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void PackageSubcommand_HasOptionalForceOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void PackageSubcommand_Parse_WithRequiredOptions_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var result = subcommand.Parse("MyPlugin.Plugins --output ./recovered/");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void PackageSubcommand_Parse_WithForce_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var result = subcommand.Parse("MyPlugin.Plugins --output ./MyPlugin.1.0.0.nupkg --force");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void PackageSubcommand_Parse_WithGuid_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var result = subcommand.Parse("12345678-1234-1234-1234-123456789abc --output ./recovered/");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void PackageSubcommand_Parse_WithJsonOutput_Succeeds()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var result = subcommand.Parse("MyPackage --output ./recovered/ --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void PackageSubcommand_Parse_WithoutOutput_HasErrors()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var result = subcommand.Parse("MyPackage");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Alias Tests

    [Fact]
    public void AssemblySubcommand_OutputOption_HasShortAlias()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.First(o => o.Name == "--output");
        Assert.Contains("-o", option.Aliases);
    }

    [Fact]
    public void AssemblySubcommand_ForceOption_HasShortAlias()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.First(o => o.Name == "--force");
        Assert.Contains("-f", option.Aliases);
    }

    [Fact]
    public void PackageSubcommand_OutputOption_HasShortAlias()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var option = subcommand.Options.First(o => o.Name == "--output");
        Assert.Contains("-o", option.Aliases);
    }

    [Fact]
    public void PackageSubcommand_ForceOption_HasShortAlias()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var option = subcommand.Options.First(o => o.Name == "--force");
        Assert.Contains("-f", option.Aliases);
    }

    #endregion
}
