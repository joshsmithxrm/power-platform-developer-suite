using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class RegisterCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempDllFile;
    private readonly string _tempNupkgFile;
    private readonly string _originalDir;

    public RegisterCommandTests()
    {
        _command = RegisterCommand.Create();

        // Create temp files for parsing tests
        _tempDllFile = Path.Combine(Path.GetTempPath(), $"TestPlugin-{Guid.NewGuid()}.dll");
        File.WriteAllBytes(_tempDllFile, [0x4D, 0x5A]); // MZ header for valid DLL

        // Create a minimal nupkg (zip with .nuspec)
        _tempNupkgFile = Path.Combine(Path.GetTempPath(), $"TestPlugin-{Guid.NewGuid()}.nupkg");
        CreateMinimalNupkg(_tempNupkgFile);

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

    private static void CreateMinimalNupkg(string path)
    {
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry("test.nuspec");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
              <metadata>
                <id>TestPlugin</id>
                <version>1.0.0</version>
              </metadata>
            </package>
            """);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("register", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Register", _command.Description);
    }

    [Fact]
    public void Create_HasAssemblySubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "assembly");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasPackageSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "package");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasTypeSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "type");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasStepSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "step");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasImageSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "image");
        Assert.NotNull(subcommand);
    }

    #endregion

    #region Assembly Subcommand Tests

    [Fact]
    public void Assembly_HasPathArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "path");
        Assert.NotNull(argument);
    }


    [Fact]
    public void Assembly_HasProfileOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Assembly_HasSolutionOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "assembly");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void Assembly_Parse_WithPath_Succeeds()
    {
        var result = _command.Parse($"assembly \"{_tempDllFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Assembly_Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse($"assembly \"{_tempDllFile}\" --profile dev --solution MySolution");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Package Subcommand Tests

    [Fact]
    public void Package_HasPathArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "package");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "path");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Package_Parse_WithPath_Succeeds()
    {
        var result = _command.Parse($"package \"{_tempNupkgFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Package_Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse($"package \"{_tempNupkgFile}\" --profile dev --solution MySolution");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Type Subcommand Tests

    [Fact]
    public void Type_HasAssemblyArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "type");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "assembly");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Type_HasTypenameOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "type");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--typename");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Type_Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("type MyAssembly --typename MyNamespace.MyPlugin");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Type_Parse_MissingTypename_HasError()
    {
        var result = _command.Parse("type MyAssembly");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Step Subcommand Tests

    [Fact]
    public void Step_HasTypeArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "type");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Step_HasRequiredMessageOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--message");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Step_HasRequiredEntityOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Step_HasRequiredStageOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--stage");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Step_HasOptionalModeOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--mode");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Step_HasOptionalRankOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--rank");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Step_HasOptionalFilteringAttributesOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--filtering-attributes");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Step_HasOptionalNameOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "step");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Step_Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("step MyNamespace.MyPlugin --message Create --entity account --stage PostOperation");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Step_Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            "step MyNamespace.MyPlugin " +
            "--message Update " +
            "--entity account " +
            "--stage PreOperation " +
            "--mode Sync " +
            "--rank 10 " +
            "--filtering-attributes \"name,telephone1\" " +
            "--name \"MyPlugin: Update of account\" " +
            "--solution MySolution");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Step_Parse_MissingMessage_HasError()
    {
        var result = _command.Parse("step MyPlugin --entity account --stage PostOperation");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Step_Parse_MissingEntity_HasError()
    {
        var result = _command.Parse("step MyPlugin --message Create --stage PostOperation");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Step_Parse_MissingStage_HasError()
    {
        var result = _command.Parse("step MyPlugin --message Create --entity account");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Image Subcommand Tests

    [Fact]
    public void Image_HasStepArgument()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var argument = subcommand.Arguments.FirstOrDefault(a => a.Name == "step");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Image_HasRequiredNameOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Image_HasRequiredTypeOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Image_HasOptionalAttributesOption()
    {
        var subcommand = _command.Subcommands.First(c => c.Name == "image");
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--attributes");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Image_Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("image \"MyPlugin: Create of account\" --name PreImage --type pre");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Image_Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            "image \"MyPlugin: Update of account\" " +
            "--name PreImage " +
            "--type pre " +
            "--attributes \"name,accountnumber,statecode\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Image_Parse_MissingName_HasError()
    {
        var result = _command.Parse("image \"MyPlugin: Create of account\" --type pre");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Image_Parse_MissingType_HasError()
    {
        var result = _command.Parse("image \"MyPlugin: Create of account\" --name PreImage");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Global Options Tests

    [Theory]
    [InlineData("assembly")]
    [InlineData("package")]
    [InlineData("type")]
    [InlineData("step")]
    [InlineData("image")]
    public void Subcommand_HasOutputFormatOption(string subcommandName)
    {
        var subcommand = _command.Subcommands.First(c => c.Name == subcommandName);
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("package")]
    [InlineData("type")]
    [InlineData("step")]
    [InlineData("image")]
    public void Subcommand_HasVerboseOption(string subcommandName)
    {
        var subcommand = _command.Subcommands.First(c => c.Name == subcommandName);
        var option = subcommand.Options.FirstOrDefault(o => o.Name == "--verbose");
        Assert.NotNull(option);
    }

    #endregion
}
