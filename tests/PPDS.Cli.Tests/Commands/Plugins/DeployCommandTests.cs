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

    // Fix (#1295 follow-up): a config authored with variant stage/mode tokens ("40"/"sync") must be
    // idempotent — the second deploy updates the existing step in place rather than force-creating a
    // duplicate. The stateful mock stores canonical stage/mode on create (as Dataverse reads them back).
    [Fact]
    public async Task DeployAssemblyAsync_VariantStageModeTokens_IsIdempotent_NoDuplicateOnSecondRun()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        var dummyAssembly = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid()}.dll");
        File.WriteAllBytes(dummyAssembly, new byte[] { 0x4D, 0x5A });

        try
        {
            var envSteps = new List<PluginStepInfo>();

            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(s => s.UpsertAssemblyAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            mock.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginTypeInfo> { new() { Id = typeId, TypeName = "MyPlugin.Handler" } });
            mock.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => envSteps.ToList());
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
                .ReturnsAsync((Guid _, string _, PluginStepConfig cfg, Guid _, Guid? _, string? _, StepIdentityResolution? res, CancellationToken _) =>
                {
                    if (res?.ExistingStepId is { } existingId)
                        return existingId; // update in place

                    var id = Guid.NewGuid();
                    envSteps.Add(new PluginStepInfo
                    {
                        Id = id,
                        Name = cfg.Name!,
                        Message = cfg.Message,
                        PrimaryEntity = cfg.Entity,
                        // Dataverse stores an int and reads back the canonical string — mirror that.
                        Stage = PluginRegistrationService.MapStageFromValue(PluginRegistrationService.MapStageToValue(cfg.Stage)),
                        Mode = PluginRegistrationService.MapModeFromValue(PluginRegistrationService.MapModeToValue(cfg.Mode)),
                        ExecutionOrder = cfg.ExecutionOrder
                    });
                    return id;
                });

            PluginAssemblyConfig BuildConfig() => new()
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
                            new PluginStepConfig { Message = "Update", Entity = "account", Stage = "40", Mode = "sync", ExecutionOrder = 1 }
                        }
                    }
                }
            };

            var globalOptions = new GlobalOptionValues { OutputFormat = OutputFormat.Json };

            // First deploy creates the step.
            var first = await DeployCommand.DeployAssemblyAsync(
                mock.Object, BuildConfig(), Path.GetTempPath(), null, clean: false, dryRun: false, globalOptions, CancellationToken.None);
            Assert.True(first.Success);
            Assert.Equal(1, first.StepsCreated);
            Assert.Equal(0, first.StepsUpdated);
            Assert.Single(envSteps);

            // Second deploy of the SAME variant-token config must update in place — no duplicate.
            var second = await DeployCommand.DeployAssemblyAsync(
                mock.Object, BuildConfig(), Path.GetTempPath(), null, clean: false, dryRun: false, globalOptions, CancellationToken.None);
            Assert.True(second.Success);
            Assert.Equal(0, second.StepsCreated);
            Assert.Equal(1, second.StepsUpdated);
            Assert.Single(envSteps);
        }
        finally
        {
            File.Delete(dummyAssembly);
        }
    }

    // Fix (#1295 follow-up): a plain deploy (no --clean) must not silently leave the old step active
    // after a stage/mode change (now delete+create). Orphans are surfaced as warnings, not deleted.
    [Fact]
    public async Task DeployAssemblyAsync_WithoutClean_ReportsOrphanedStepsAsWarnings()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var orphanId = Guid.NewGuid();

        var dummyAssembly = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid()}.dll");
        File.WriteAllBytes(dummyAssembly, new byte[] { 0x4D, 0x5A });

        try
        {
            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(s => s.UpsertAssemblyAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            mock.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginTypeInfo> { new() { Id = typeId, TypeName = "MyPlugin.Handler" } });
            // Env holds the OLD PreOperation step; config declares the PostOperation step -> PreOp is orphaned.
            mock.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginStepInfo>
                {
                    new() { Id = orphanId, Name = "MyPlugin.Handler: Update of account", Message = "Update", PrimaryEntity = "account", Stage = "PreOperation", Mode = "Synchronous", ExecutionOrder = 1 }
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
                            new PluginStepConfig { Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous", ExecutionOrder = 1 }
                        }
                    }
                }
            };

            var globalOptions = new GlobalOptionValues { OutputFormat = OutputFormat.Json };

            // Act - deploy WITHOUT --clean.
            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object, assemblyConfig, Path.GetTempPath(), null, clean: false, dryRun: false, globalOptions, CancellationToken.None);

            // Assert - the orphaned PreOperation step is surfaced as a warning, and nothing is deleted.
            Assert.True(result.Success);
            Assert.Contains(result.Warnings, w => w.Contains("--clean") && w.Contains("PreOperation"));
            mock.Verify(s => s.DeleteStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(dummyAssembly);
        }
    }

    #endregion

    #region Missing Message Filter Guard Tests (#1332)

    // Bug (#1332): a configured entity that resolves no SDK message filter (typo like "acount") used
    // to force-create an unfiltered GLOBAL step whose read-back identity ("none") never matches the
    // config identity — so every deploy created another duplicate. The step must instead fail with a
    // per-step error, be skipped, and fail the assembly result; other steps still deploy; repeated
    // runs must not accumulate anything.
    [Fact]
    public async Task DeployAssemblyAsync_SpecifiedEntityWithNoMessageFilter_FailsStepAndNeverCreates()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        var dummyAssembly = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid()}.dll");
        File.WriteAllBytes(dummyAssembly, new byte[] { 0x4D, 0x5A });

        try
        {
            // Stateful environment so a second deploy sees whatever the first one created.
            var envSteps = new List<PluginStepInfo>();

            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(s => s.UpsertAssemblyAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            mock.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginTypeInfo> { new() { Id = typeId, TypeName = "MyPlugin.Handler" } });
            mock.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => envSteps.ToList());
            mock.Setup(s => s.UpsertPluginTypeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(typeId);
            mock.Setup(s => s.GetSdkMessageIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            // The valid entity resolves a filter; the typo'd entity does not.
            mock.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), "account", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            mock.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), "acount", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid?)null);
            mock.Setup(s => s.ListImagesForStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginImageInfo>());
            mock.Setup(s => s.UpsertStepAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PluginStepConfig>(), It.IsAny<Guid>(),
                    It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<StepIdentityResolution?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string _, PluginStepConfig cfg, Guid _, Guid? _, string? _, StepIdentityResolution? res, CancellationToken _) =>
                {
                    if (res?.ExistingStepId is { } existingId)
                        return existingId;

                    var id = Guid.NewGuid();
                    envSteps.Add(new PluginStepInfo
                    {
                        Id = id,
                        Name = cfg.Name!,
                        Message = cfg.Message,
                        PrimaryEntity = cfg.Entity,
                        Stage = cfg.Stage,
                        Mode = cfg.Mode,
                        ExecutionOrder = cfg.ExecutionOrder
                    });
                    return id;
                });

            PluginAssemblyConfig BuildConfig() => new()
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
                            new PluginStepConfig { Message = "Create", Entity = "acount", Stage = "PostOperation", Mode = "Synchronous" },
                            new PluginStepConfig { Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous" }
                        }
                    }
                }
            };

            var globalOptions = new GlobalOptionValues { OutputFormat = OutputFormat.Json };

            // First deploy: the typo'd step fails, the valid step deploys.
            var first = await DeployCommand.DeployAssemblyAsync(
                mock.Object, BuildConfig(), Path.GetTempPath(), null, clean: false, dryRun: false, globalOptions, CancellationToken.None);

            Assert.False(first.Success);
            Assert.NotNull(first.Error);
            Assert.Contains("acount", first.Error);
            Assert.Contains("No SDK message filter", first.Error);
            Assert.Contains("Create", first.Error);
            Assert.Equal(1, first.StepsCreated);

            // The typo'd step was never written; only the valid step was.
            mock.Verify(s => s.UpsertStepAsync(
                It.IsAny<Guid>(), It.IsAny<string>(),
                It.Is<PluginStepConfig>(c => c.Entity == "acount"),
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<StepIdentityResolution?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Single(envSteps);

            // Second deploy: still fails the same way — and accumulates nothing (the #1332 bug was one
            // new active global step per run).
            var second = await DeployCommand.DeployAssemblyAsync(
                mock.Object, BuildConfig(), Path.GetTempPath(), null, clean: false, dryRun: false, globalOptions, CancellationToken.None);

            Assert.False(second.Success);
            Assert.Equal(0, second.StepsCreated);
            Assert.Equal(1, second.StepsUpdated); // the valid step converges to an in-place update
            Assert.Single(envSteps);
        }
        finally
        {
            File.Delete(dummyAssembly);
        }
    }

    // Guard boundary (#1332): an intentionally global step — entity "none" (or empty) — legitimately
    // resolves no filter and must keep deploying with a null filter id.
    [Fact]
    public async Task DeployAssemblyAsync_GlobalStepWithoutEntity_StillDeploysWithNullFilter()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

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
                .ReturnsAsync(new List<PluginStepInfo>());
            mock.Setup(s => s.UpsertPluginTypeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(typeId);
            mock.Setup(s => s.GetSdkMessageIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            // Global messages have no filter row — the lookup returns null.
            mock.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid?)null);
            mock.Setup(s => s.ListImagesForStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PluginImageInfo>());
            mock.Setup(s => s.UpsertStepAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PluginStepConfig>(), It.IsAny<Guid>(),
                    It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<StepIdentityResolution?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());

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
                            new PluginStepConfig { Message = "Publish", Entity = "none", Stage = "PostOperation", Mode = "Synchronous" }
                        }
                    }
                }
            };

            var globalOptions = new GlobalOptionValues { OutputFormat = OutputFormat.Json };

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object, assemblyConfig, Path.GetTempPath(), null, clean: false, dryRun: false, globalOptions, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Null(result.Error);
            Assert.Equal(1, result.StepsCreated);

            // The step was written with a null filter id (a real global registration).
            mock.Verify(s => s.UpsertStepAsync(
                It.IsAny<Guid>(), It.IsAny<string>(),
                It.Is<PluginStepConfig>(c => c.Entity == "none"),
                It.IsAny<Guid>(), It.Is<Guid?>(f => f == null), It.IsAny<string?>(),
                It.IsAny<StepIdentityResolution?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(dummyAssembly);
        }
    }

    #endregion
}
