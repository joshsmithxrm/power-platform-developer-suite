using System.CommandLine;
using System.CommandLine.Parsing;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Collection(nameof(CurrentDirectoryMutatingCollection))]
public class CleanCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempConfigFile;
    private readonly string _originalDir;

    public CleanCommandTests()
    {
        _command = CleanCommand.Create();

        // Create temp config file for parsing tests
        _tempConfigFile = Path.Combine(Path.GetTempPath(), $"registrations-{Guid.NewGuid()}.json");
        File.WriteAllText(_tempConfigFile, "{}");

        // Change to temp directory for relative path tests
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetTempPath());
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        if (File.Exists(_tempConfigFile))
            File.Delete(_tempConfigFile);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("clean", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Remove", _command.Description);
    }

    [Fact]
    public void Create_HasRequiredConfigOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--config");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-c", option.Aliases);
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
    public void Create_HasOptionalDryRunOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--dry-run");
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

    [Fact]
    public void Parse_WithRequiredConfig_Succeeds()
    {
        var result = _command.Parse($"--config \"{_tempConfigFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingConfig_HasError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalProfile_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalEnvironment_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --environment https://org.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalDryRun_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --dry-run");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            $"-c \"{_tempConfigFile}\" " +
            "--profile dev " +
            "--environment https://org.crm.dynamics.com " +
            "--dry-run " +
            "--output-format Json");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Clean Matching Tests (#1295)

    // The environment has two same-named steps differing only in stage; the config declares the
    // PostOperation one. The PreOperation step must be detected as an orphan (previously the name-set
    // check saw its name as "configured" and skipped it).
    [Fact]
    public async Task CleanAssemblyAsync_SameNamedOrphanOfDifferentStage_IsDetected()
    {
        var typeId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var preId = Guid.NewGuid();
        const string sharedName = "MyPlugin.Handler: Update of account";

        var mock = new Mock<IPluginRegistrationService>();
        mock.Setup(s => s.GetAssemblyByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginAssemblyInfo { Id = Guid.NewGuid(), Name = "MyPlugin" });
        mock.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginTypeInfo> { new() { Id = typeId, TypeName = "MyPlugin.Handler" } });
        mock.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginStepInfo>
            {
                new() { Id = postId, Name = sharedName, Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous" },
                new() { Id = preId, Name = sharedName, Message = "Update", PrimaryEntity = "account", Stage = "PreOperation", Mode = "Synchronous" }
            });

        var assemblyConfig = new PluginAssemblyConfig
        {
            Name = "MyPlugin",
            Types =
            {
                new PluginTypeConfig
                {
                    TypeName = "MyPlugin.Handler",
                    Steps =
                    {
                        new PluginStepConfig { Name = sharedName, Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous" }
                    }
                }
            }
        };

        var globalOptions = new GlobalOptionValues { OutputFormat = OutputFormat.Json };

        // Act - dry run so no real delete is issued.
        var result = await CleanCommand.CleanAssemblyAsync(
            mock.Object, assemblyConfig, dryRun: true, globalOptions, CancellationToken.None);

        // Assert - exactly the PreOperation step is flagged orphaned; the configured PostOperation is not.
        var orphan = Assert.Single(result.OrphanedSteps);
        Assert.Equal(preId, orphan.StepId);
        Assert.DoesNotContain(result.OrphanedSteps, o => o.StepId == postId);
        mock.Verify(s => s.DeleteStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}
