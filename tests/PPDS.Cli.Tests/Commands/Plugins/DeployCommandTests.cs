using System.CommandLine;
using System.CommandLine.Parsing;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class DeployCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempConfigFile;
    private readonly string _originalDir;

    public DeployCommandTests()
    {
        _command = DeployCommand.Create();

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
        Assert.Equal("deploy", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Deploy", _command.Description);
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
    public void Create_HasOptionalSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalCleanOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--clean");
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
    public void Parse_WithOptionalSolution_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --solution my_solution");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalClean_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --clean");
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
            "--solution my_solution " +
            "--clean " +
            "--dry-run " +
            "--output-format Json");
        Assert.Empty(result.Errors);
    }

    #endregion

    #region Deployment Matching Tests (#1295)

    // Environment holds two same-named steps (PreOp + PostOp); config declares the PostOp step plus a
    // brand-new Create step. Deploy must update the exact PostOp row by GUID, force-create the new
    // step, and (with --clean) delete the orphaned PreOp row by GUID — never abort on the duplicate name.
    [Fact]
    public async Task DeployAssemblyAsync_SameNamedSteps_UpdatesPaired_ForceCreatesNew_AndCleansOrphan()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var preId = Guid.NewGuid();
        const string sharedName = "MyPlugin.Handler: Update of account";

        // A real (empty) file so File.Exists passes for the classic-assembly path.
        var dummyAssembly = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid()}.dll");
        File.WriteAllBytes(dummyAssembly, new byte[] { 0x4D, 0x5A });

        try
        {
            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(s => s.UpsertAssemblyAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            mock.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginTypeInfo> { new() { Id = typeId, TypeName = "MyPlugin.Handler" } });
            mock.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginStepInfo>
                {
                    new() { Id = postId, Name = sharedName, Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous" },
                    new() { Id = preId, Name = sharedName, Message = "Update", PrimaryEntity = "account", Stage = "PreOperation", Mode = "Synchronous" }
                });
            mock.Setup(s => s.UpsertPluginTypeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(typeId);
            mock.Setup(s => s.GetSdkMessageIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            mock.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            mock.Setup(s => s.ListImagesForStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginImageInfo>());
            mock.Setup(s => s.UpsertStepAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PluginStepConfig>(), It.IsAny<Guid>(),
                    It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<StepIdentityResolution?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            mock.Setup(s => s.DeleteStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var assemblyConfig = new PluginAssemblyConfig
            {
                Name = "MyPlugin",
                Type = "Assembly",
                Path = dummyAssembly,
                Types =
                {
                    new PluginTypeConfig
                    {
                        TypeName = "MyPlugin.Handler",
                        Steps =
                        {
                            new PluginStepConfig { Name = sharedName, Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous" },
                            new PluginStepConfig { Message = "Create", Entity = "account", Stage = "PostOperation", Mode = "Synchronous" }
                        }
                    }
                }
            };

            var globalOptions = new GlobalOptionValues { OutputFormat = OutputFormat.Json };

            // Act
            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object, assemblyConfig, Path.GetTempPath(),
                solutionOverride: null, clean: true, dryRun: false, globalOptions, CancellationToken.None);

            // Assert - the assembly deployed successfully.
            Assert.True(result.Success);

            // Paired Update step updates the exact PostOperation row by GUID.
            mock.Verify(s => s.UpsertStepAsync(
                It.IsAny<Guid>(), It.IsAny<string>(),
                It.Is<PluginStepConfig>(c => c.Message == "Update"),
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.Is<StepIdentityResolution?>(r => r != null && r.ExistingStepId == postId),
                It.IsAny<CancellationToken>()), Times.Once);

            // New Create step is force-created (null step id).
            mock.Verify(s => s.UpsertStepAsync(
                It.IsAny<Guid>(), It.IsAny<string>(),
                It.Is<PluginStepConfig>(c => c.Message == "Create"),
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.Is<StepIdentityResolution?>(r => r != null && r.ExistingStepId == null),
                It.IsAny<CancellationToken>()), Times.Once);

            // The orphaned PreOperation row is deleted by its GUID.
            mock.Verify(s => s.DeleteStepAsync(preId, It.IsAny<CancellationToken>()), Times.Once);
            mock.Verify(s => s.DeleteStepAsync(postId, It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(dummyAssembly);
        }
    }

    #endregion
}
