using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class UpdateCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempDllFile;
    private readonly string _tempNupkgFile;
    private readonly string _originalDir;

    public UpdateCommandTests()
    {
        _command = UpdateCommand.Create();

        // Create temp files for parsing tests
        _tempDllFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.dll");
        _tempNupkgFile = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.nupkg");
        File.WriteAllBytes(_tempDllFile, [0x00]);
        File.WriteAllBytes(_tempNupkgFile, [0x00]);

        // Change to temp directory for relative path tests
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetTempPath());
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        if (File.Exists(_tempDllFile))
            File.Delete(_tempDllFile);
        if (File.Exists(_tempNupkgFile))
            File.Delete(_tempNupkgFile);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("update", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Update", _command.Description);
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

    [Fact]
    public void Create_HasStepSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "step");
        Assert.NotNull(subcommand);
        Assert.Contains("step", subcommand.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasImageSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "image");
        Assert.NotNull(subcommand);
        Assert.Contains("image", subcommand.Description, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Assembly Subcommand Tests

    [Fact]
    public void AssemblySubcommand_HasNameArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "name");
        Assert.NotNull(argument);
    }

    [Fact]
    public void AssemblySubcommand_HasPathArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "path");
        Assert.NotNull(argument);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithValidArguments_Succeeds()
    {
        var result = _command.Parse($"assembly MyPlugin \"{_tempDllFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithProfileOption_Succeeds()
    {
        var result = _command.Parse($"assembly MyPlugin \"{_tempDllFile}\" --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssemblySubcommand_Parse_WithSolutionOption_Succeeds()
    {
        var result = _command.Parse($"assembly MyPlugin \"{_tempDllFile}\" --solution my_solution");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Package Subcommand Tests

    [Fact]
    public void PackageSubcommand_HasNameArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "name");
        Assert.NotNull(argument);
    }

    [Fact]
    public void PackageSubcommand_HasPathArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "path");
        Assert.NotNull(argument);
    }

    [Fact]
    public void PackageSubcommand_Parse_WithValidArguments_Succeeds()
    {
        var result = _command.Parse($"package MyPlugin.Plugins \"{_tempNupkgFile}\"");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Step Subcommand Tests

    [Fact]
    public void StepSubcommand_HasNameOrIdArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "name-or-id");
        Assert.NotNull(argument);
    }

    [Fact]
    public void StepSubcommand_HasModeOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--mode");
        Assert.NotNull(option);
    }

    [Fact]
    public void StepSubcommand_HasStageOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--stage");
        Assert.NotNull(option);
    }

    [Fact]
    public void StepSubcommand_HasRankOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--rank");
        Assert.NotNull(option);
    }

    [Fact]
    public void StepSubcommand_HasFilteringAttributesOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--filtering-attributes");
        Assert.NotNull(option);
    }

    [Fact]
    public void StepSubcommand_HasDescriptionOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--description");
        Assert.NotNull(option);
    }

    [Fact]
    public void StepSubcommand_Parse_WithModeOption_Succeeds()
    {
        var result = _command.Parse("step \"MyPlugin: Create of account\" --mode Async");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StepSubcommand_Parse_WithStageOption_Succeeds()
    {
        var result = _command.Parse("step \"MyPlugin: Create of account\" --stage PreOperation");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StepSubcommand_Parse_WithRankOption_Succeeds()
    {
        var result = _command.Parse("step \"MyPlugin: Create of account\" --rank 5");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StepSubcommand_Parse_WithFilteringAttributesOption_Succeeds()
    {
        var result = _command.Parse("step \"MyPlugin: Update of account\" --filtering-attributes \"name,telephone1\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StepSubcommand_Parse_WithMultipleOptions_Succeeds()
    {
        var result = _command.Parse("step \"MyPlugin: Create of account\" --mode Async --stage PreOperation --rank 5");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StepSubcommand_Parse_WithGuid_Succeeds()
    {
        var guid = Guid.NewGuid();
        var result = _command.Parse($"step {guid} --mode Sync");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Image Subcommand Tests

    [Fact]
    public void ImageSubcommand_HasNameOrIdArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "name-or-id");
        Assert.NotNull(argument);
    }

    [Fact]
    public void ImageSubcommand_HasAttributesOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--attributes");
        Assert.NotNull(option);
    }

    [Fact]
    public void ImageSubcommand_HasNameOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
    }

    [Fact]
    public void ImageSubcommand_Parse_WithAttributesOption_Succeeds()
    {
        var result = _command.Parse("image PreImage --attributes \"name,accountnumber\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ImageSubcommand_Parse_WithNameOption_Succeeds()
    {
        var result = _command.Parse("image PreImage --name NewPreImage");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ImageSubcommand_Parse_WithMultipleOptions_Succeeds()
    {
        var result = _command.Parse("image PreImage --attributes \"name,accountnumber\" --name NewPreImage");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ImageSubcommand_Parse_WithGuid_Succeeds()
    {
        var guid = Guid.NewGuid();
        var result = _command.Parse($"image {guid} --attributes \"name,accountnumber\"");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Output Format Tests

    [Fact]
    public void AssemblySubcommand_HasOutputFormatOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
    }

    [Fact]
    public void PackageSubcommand_HasOutputFormatOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
    }

    [Fact]
    public void StepSubcommand_HasOutputFormatOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
    }

    [Fact]
    public void ImageSubcommand_HasOutputFormatOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
    }

    #endregion
}
