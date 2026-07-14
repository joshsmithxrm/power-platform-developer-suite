using System.CommandLine;
using System.CommandLine.Parsing;
using Moq;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class DiffCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempConfigFile;
    private readonly string _originalDir;

    public DiffCommandTests()
    {
        _command = DiffCommand.Create();

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
        Assert.Equal("diff", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Compare", _command.Description);
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
            "--output-format Json");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Drift Computation Tests (#1295)

    private static Mock<IPluginRegistrationService> MockServiceWithSteps(
        Guid assemblyId,
        Guid typeId,
        string typeName,
        List<PluginStepInfo> envSteps)
    {
        var mock = new Mock<IPluginRegistrationService>();
        mock.Setup(s => s.GetAssemblyByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginAssemblyInfo { Id = assemblyId, Name = "MyPlugin" });
        mock.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginTypeInfo> { new() { Id = typeId, TypeName = typeName } });
        mock.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envSteps);
        mock.Setup(s => s.ListImagesForStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginImageInfo>());
        return mock;
    }

    // Previously this threw "Environment contains duplicate plugin step name ..." — the reported bug.
    [Fact]
    public async Task ComputeDriftAsync_EnvHasTwoSameNamedDifferentStage_ClassifiesInsteadOfThrowing()
    {
        var typeId = Guid.NewGuid();
        const string sharedName = "MyPlugin.Handler: Update of account";

        // Env ranks aligned with the config default (ExecutionOrder = 1) so the paired step shows no
        // rank drift; rank is a drift-tracked property, not part of identity.
        var mock = MockServiceWithSteps(Guid.NewGuid(), typeId, "MyPlugin.Handler", new List<PluginStepInfo>
        {
            new() { Id = Guid.NewGuid(), Name = sharedName, Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous", ExecutionOrder = 1 },
            new() { Id = Guid.NewGuid(), Name = sharedName, Message = "Update", PrimaryEntity = "account", Stage = "PreOperation", Mode = "Synchronous", ExecutionOrder = 1 }
        });

        var config = new PluginAssemblyConfig
        {
            Name = "MyPlugin",
            Types =
            {
                new PluginTypeConfig
                {
                    TypeName = "MyPlugin.Handler",
                    Steps =
                    {
                        new PluginStepConfig { Name = sharedName, Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous", ExecutionOrder = 1 }
                    }
                }
            }
        };

        // Act - must complete rather than throw.
        var drift = await DiffCommand.ComputeDriftAsync(mock.Object, config);

        // Assert - the PostOperation step is paired (clean); the PreOperation step is orphaned.
        Assert.Empty(drift.MissingSteps);
        Assert.Empty(drift.ModifiedSteps);
        var orphan = Assert.Single(drift.OrphanedSteps);
        Assert.Equal("PreOperation", orphan.Stage);
    }

    [Fact]
    public async Task ComputeDriftAsync_RenamedPairedStep_ReportsNameDrift()
    {
        var typeId = Guid.NewGuid();

        var mock = MockServiceWithSteps(Guid.NewGuid(), typeId, "MyPlugin.Handler", new List<PluginStepInfo>
        {
            new() { Id = Guid.NewGuid(), Name = "Old Display Name", Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous", ExecutionOrder = 1 }
        });

        var config = new PluginAssemblyConfig
        {
            Name = "MyPlugin",
            Types =
            {
                new PluginTypeConfig
                {
                    TypeName = "MyPlugin.Handler",
                    Steps =
                    {
                        new PluginStepConfig { Name = "New Display Name", Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous", ExecutionOrder = 1 }
                    }
                }
            }
        };

        var drift = await DiffCommand.ComputeDriftAsync(mock.Object, config);

        Assert.Empty(drift.MissingSteps);
        Assert.Empty(drift.OrphanedSteps);
        // The only drift on the paired step is the name change (behavior is unchanged).
        var modified = Assert.Single(drift.ModifiedSteps);
        var change = Assert.Single(modified.Changes);
        Assert.Equal("name", change.Property);
        Assert.Equal("New Display Name", change.Expected);
        Assert.Equal("Old Display Name", change.Actual);
    }

    // Bug (#1332): a missing step whose specified entity resolves no SDK message filter can never be
    // created by deploy — diff must say so instead of presenting it as ordinary missing drift. An
    // intentionally global step ("none") and a valid entity must not trigger the warning.
    [Fact]
    public async Task ComputeDriftAsync_MissingStepWithNoMessageFilter_WarnsItCannotBeCreated()
    {
        var typeId = Guid.NewGuid();

        // Empty environment: every configured step is missing.
        var mock = MockServiceWithSteps(Guid.NewGuid(), typeId, "MyPlugin.Handler", new List<PluginStepInfo>());
        mock.Setup(s => s.GetSdkMessageIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        mock.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), "account", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        mock.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), "acount", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var config = new PluginAssemblyConfig
        {
            Name = "MyPlugin",
            Types =
            {
                new PluginTypeConfig
                {
                    TypeName = "MyPlugin.Handler",
                    Steps =
                    {
                        new PluginStepConfig { Message = "Create", Entity = "acount", Stage = "PostOperation", Mode = "Synchronous" },
                        new PluginStepConfig { Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous" },
                        new PluginStepConfig { Message = "Publish", Entity = "none", Stage = "PostOperation", Mode = "Synchronous" }
                    }
                }
            }
        };

        var drift = await DiffCommand.ComputeDriftAsync(mock.Object, config);

        // All three are missing, but only the typo'd entity warrants a warning: the valid entity is
        // deployable and the global step legitimately has no filter.
        Assert.Equal(3, drift.MissingSteps.Count);
        var warning = Assert.Single(drift.Warnings);
        Assert.Contains("cannot be created", warning);
        Assert.Contains("acount", warning);
        Assert.Contains("Create", warning);
    }

    #endregion
}
