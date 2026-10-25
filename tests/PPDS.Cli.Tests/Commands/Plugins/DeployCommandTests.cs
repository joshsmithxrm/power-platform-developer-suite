using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Cli.Tests.Plugins;
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

    #region NuGet package preflight tests (#1411)

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployAssemblyAsync_MismatchedPackageAssembly_FailsBeforeAnyUpload(bool dryRun)
    {
        var packagePath = CreateRuntimePluginPackage();
        try
        {
            var mock = new Mock<IPluginRegistrationService>();
            var config = new PluginAssemblyConfig
            {
                Name = "ppds_RuntimePackage.1.0.0",
                Type = "Nuget",
                PackagePath = packagePath
            };

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                Path.GetTempPath(),
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            Assert.Contains("No package was uploaded", result.Error);
            Assert.Contains("Contoso.RuntimePlugins", result.Error);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task DeployAssemblyAsync_PathReplacedAfterBuffering_RejectsBufferedMismatchBeforeUpload()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-buffered-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        string? replacementPath = null;

        try
        {
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment-package.nupkg",
                "ppds_RuntimePackage",
                new TestPackageAssembly(
                    "Contoso.BufferedPayload",
                    "Contoso.BufferedPayload.dll",
                    """
                    namespace Contoso.Buffered
                    {
                        public sealed class BufferedPayload { }
                    }
                    """));
            var replacementPackagePath = CreateRuntimePluginPackage();
            replacementPath = replacementPackagePath;
            var mock = new Mock<IPluginRegistrationService>();
            var config = new PluginAssemblyConfig
            {
                Name = "Contoso.RuntimePlugins",
                Type = "Nuget",
                PackagePath = deploymentPath
            };

            async Task<byte[]> ReadThenReplaceAsync(string path, CancellationToken cancellationToken)
            {
                var bufferedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
                File.Copy(replacementPackagePath, path, overwrite: true);
                return bufferedBytes;
            }

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None,
                ReadThenReplaceAsync);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            Assert.Contains("Contoso.BufferedPayload", result.Error);
            Assert.Contains("No package was uploaded", result.Error);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            if (replacementPath != null)
                File.Delete(replacementPath);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeployAssemblyAsync_RebuiltPackageAddsPlausibleAssembly_FailsBeforeLookupOrUpload(
        bool dryRun,
        bool useRegistrationMetadata)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-ambiguous-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        try
        {
            const string firstPluginSource = """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.First
                {
                    public sealed class FirstPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """;
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment-package.nupkg",
                "ppds_RuntimePackage",
                new TestPackageAssembly(
                    "Contoso.FirstPlugins",
                    "Contoso.FirstPlugins.dll",
                    firstPluginSource,
                    ReferencesSdk: true));
            var config = NupkgExtractor.Extract(deploymentPath);

            var secondAssembly = useRegistrationMetadata
                ? new TestPackageAssembly(
                    "Contoso.SecondPlugins",
                    "Contoso.SecondPlugins.dll",
                    """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    using PPDS.Plugins;
                    namespace Contoso.Second
                    {
                        [PluginStep(
                            Message = "Create",
                            EntityLogicalName = "account",
                            Stage = PluginStage.PreOperation)]
                        public sealed class SecondPlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """,
                    ReferencesSdk: true,
                    ReferencesPpdsPlugins: true)
                : new TestPackageAssembly(
                    "Contoso.SecondPlugins",
                    "Contoso.SecondPlugins.dll",
                    """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Second
                    {
                        public sealed class SecondPlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """,
                    ReferencesSdk: true);
            var rebuiltPath = PluginPackageTestFixture.Create(
                scratch,
                "rebuilt-package.nupkg",
                "ppds_RuntimePackage",
                new TestPackageAssembly(
                    "Contoso.FirstPlugins",
                    "Contoso.FirstPlugins.dll",
                    firstPluginSource,
                    ReferencesSdk: true),
                secondAssembly);
            File.Copy(rebuiltPath, deploymentPath, overwrite: true);

            var mock = new Mock<IPluginRegistrationService>();
            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyAmbiguous, result.ErrorCode);
            Assert.Contains("Contoso.FirstPlugins", result.Error);
            Assert.Contains("Contoso.SecondPlugins", result.Error);
            Assert.Contains("No package was uploaded", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployAssemblyAsync_RebuiltPackageRemovesConfiguredType_FailsBeforeLookupOrUpload(
        bool dryRun)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-missing-type-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        try
        {
            const string firstPluginSource = """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class FirstPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }

                    public sealed class RemovedPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """;
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment-package.nupkg",
                "ppds_RuntimePackage",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    firstPluginSource,
                    ReferencesSdk: true));
            var config = NupkgExtractor.Extract(deploymentPath);
            Assert.Contains("Contoso.Plugins.RemovedPlugin", config.AllTypeNames);

            var rebuiltPath = PluginPackageTestFixture.Create(
                scratch,
                "rebuilt-package.nupkg",
                "ppds_RuntimePackage",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Plugins
                    {
                        public sealed class FirstPlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """,
                    ReferencesSdk: true));
            File.Copy(rebuiltPath, deploymentPath, overwrite: true);

            var mock = new Mock<IPluginRegistrationService>();
            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            Assert.Contains("Contoso.Plugins.RemovedPlugin", result.Error);
            Assert.Contains("No package was uploaded", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PathReplacedAfterGlobalPreflight_UploadsRetainedSnapshot()
    {
        var packagePath = CreateRuntimePluginPackage();
        var replacementPath = PluginPackageTestFixture.Create(
            Path.GetTempPath(),
            $"ppds-replacement-{Guid.NewGuid():N}.nupkg",
            "ppds_ReplacementPackage",
            new TestPackageAssembly(
                "Contoso.ReplacementPlugins",
                "Contoso.ReplacementPlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                public sealed class ReplacementPlugin : IPlugin
                {
                    public void Execute(IServiceProvider serviceProvider) { }
                }
                """,
                ReferencesSdk: true));
        try
        {
            var retainedBytes = File.ReadAllBytes(packagePath);
            var config = new PluginRegistrationConfig
            {
                Assemblies = [NupkgExtractor.Extract(packagePath)]
            };
            File.WriteAllText(_tempConfigFile, System.Text.Json.JsonSerializer.Serialize(config));
            var packageId = Guid.NewGuid();
            var assemblyId = Guid.NewGuid();
            var registration = new Mock<IPluginRegistrationService>();
            registration.Setup(service => service.UpsertPackageAsync(
                    "ppds_RuntimePackage",
                    It.Is<byte[]>(bytes => bytes.SequenceEqual(retainedBytes)),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(packageId);
            registration.Setup(service => service.GetAssemblyIdForPackageAsync(
                    packageId,
                    "Contoso.RuntimePlugins",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            registration.Setup(service => service.ListTypesForAssemblyAsync(
                    assemblyId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var customApis = new Mock<ICustomApiService>();

            async Task<byte[]> ReadThenReplaceAsync(string path, CancellationToken cancellationToken)
            {
                var snapshot = await File.ReadAllBytesAsync(path, cancellationToken);
                File.Copy(replacementPath, path, overwrite: true);
                return snapshot;
            }

            var exitCode = await DeployCommand.ExecuteAsync(
                new FileInfo(_tempConfigFile),
                profile: null,
                environment: null,
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None,
                serviceProviderFactory: _ => Task.FromResult(
                    new ServiceCollection()
                        .AddSingleton<IPluginRegistrationService>(registration.Object)
                        .AddSingleton<ICustomApiService>(customApis.Object)
                        .BuildServiceProvider()),
                preflightPackageContentReader: ReadThenReplaceAsync);

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.False(File.ReadAllBytes(packagePath).SequenceEqual(retainedBytes));
            registration.Verify(service => service.UpsertPackageAsync(
                "ppds_RuntimePackage",
                It.Is<byte[]>(bytes => bytes.SequenceEqual(retainedBytes)),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(packagePath);
            File.Delete(replacementPath);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeployAssemblyAsync_RebuiltPackageAddsInheritedPlugin_FailsBeforeServerCalls(
        bool dryRun,
        bool constructedGenericBase)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-inherited-ambiguity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        try
        {
            const string firstPluginSource = """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.First
                {
                    public sealed class FirstPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """;
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment-package.nupkg",
                "ppds_RuntimePackage",
                new TestPackageAssembly(
                    "Contoso.FirstPlugins",
                    "Contoso.FirstPlugins.dll",
                    firstPluginSource,
                    ReferencesSdk: true));
            var config = NupkgExtractor.Extract(deploymentPath);

            var frameworkSource = constructedGenericBase
                ? """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Framework
                    {
                        public abstract class PluginBase<T> : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """
                : """
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Framework
                    {
                        public interface IDerivedPlugin : IPlugin { }
                    }
                    """;
            var secondPluginSource = constructedGenericBase
                ? "namespace Contoso.Second { public sealed class SecondPlugin : Contoso.Framework.PluginBase<string> { } }"
                : "namespace Contoso.Second { public sealed class SecondPlugin : Contoso.Framework.IDerivedPlugin { public void Execute(System.IServiceProvider serviceProvider) { } } }";
            var rebuiltPath = PluginPackageTestFixture.Create(
                scratch,
                "rebuilt-package.nupkg",
                "ppds_RuntimePackage",
                new TestPackageAssembly(
                    "Contoso.FirstPlugins",
                    "Contoso.FirstPlugins.dll",
                    firstPluginSource,
                    ReferencesSdk: true),
                new TestPackageAssembly(
                    "Contoso.PluginFramework",
                    "Contoso.PluginFramework.dll",
                    frameworkSource,
                    ReferencesSdk: true),
                new TestPackageAssembly(
                    "Contoso.SecondPlugins",
                    "Contoso.SecondPlugins.dll",
                    secondPluginSource,
                    ReferencesSdk: true,
                    AssemblyReferences: ["Contoso.PluginFramework"]));
            var extractionError = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(rebuiltPath));
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyAmbiguous, extractionError.ErrorCode);
            File.Copy(rebuiltPath, deploymentPath, overwrite: true);

            var mock = new Mock<IPluginRegistrationService>();
            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyAmbiguous, result.ErrorCode);
            Assert.Contains("Contoso.FirstPlugins", result.Error);
            Assert.Contains("Contoso.SecondPlugins", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeployAssemblyAsync_AddedInheritanceWithWrongTokenDependency_FailsClosedBeforeServerCalls(
        bool dryRun,
        bool derivedInterface)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-wrong-token-inheritance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            const string firstPluginSource = """
                using System;
                using Microsoft.Xrm.Sdk;
                public sealed class FirstPlugin : IPlugin
                {
                    public void Execute(IServiceProvider serviceProvider) { }
                }
                """;
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment.nupkg",
                "ppds_IdentityPackage",
                new TestPackageAssembly(
                    "Contoso.FirstPlugins",
                    "Contoso.FirstPlugins.dll",
                    firstPluginSource,
                    ReferencesSdk: true));
            var config = NupkgExtractor.Extract(deploymentPath);
            var correctPublicKey = typeof(PPDS.Plugins.PluginStepAttribute).Assembly.GetName().GetPublicKey();
            var wrongPublicKey = System.Reflection.AssemblyName
                .GetAssemblyName(Path.Combine(
                    AppContext.BaseDirectory,
                    "TestAssets",
                    "Microsoft.Xrm.Sdk.net462.dll"))
                .GetPublicKey();
            Assert.NotNull(correctPublicKey);
            Assert.NotNull(wrongPublicKey);
            var correctFrameworkSource = derivedInterface
                ? """
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Framework { public interface IDerivedPlugin : IPlugin { } }
                    """
                : """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Framework
                    {
                        public abstract class PluginBase : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """;
            var wrongFrameworkSource = derivedInterface
                ? "namespace Contoso.Framework { public interface IDerivedPlugin { } }"
                : "namespace Contoso.Framework { public abstract class PluginBase { } }";
            var secondPluginSource = derivedInterface
                ? "namespace Contoso.Second { public sealed class SecondPlugin : Contoso.Framework.IDerivedPlugin { public void Execute(System.IServiceProvider serviceProvider) { } } }"
                : "namespace Contoso.Second { public sealed class SecondPlugin : Contoso.Framework.PluginBase { } }";
            var secondPluginAssembly = derivedInterface
                ? new TestPackageAssembly(
                    "Contoso.SecondPlugins",
                    "Contoso.SecondPlugins.dll",
                    string.Empty,
                    PrecompiledImage: PluginPackageTestFixture.CreateExternalDerivedInterfaceConsumerImage(
                        "Contoso.IdentityFramework",
                        correctPublicKey))
                : new TestPackageAssembly(
                    "Contoso.SecondPlugins",
                    "Contoso.SecondPlugins.dll",
                    secondPluginSource,
                    ReferencesSdk: true,
                    AssemblyReferences: ["Contoso.IdentityFramework"]);
            var rebuiltPath = PluginPackageTestFixture.Create(
                scratch,
                "rebuilt.nupkg",
                "ppds_IdentityPackage",
                new TestPackageAssembly(
                    "Contoso.FirstPlugins",
                    "Contoso.FirstPlugins.dll",
                    firstPluginSource,
                    ReferencesSdk: true),
                new TestPackageAssembly(
                    "Contoso.IdentityFramework",
                    "correct-reference.dll",
                    correctFrameworkSource,
                    ReferencesSdk: true,
                    IncludeInPackage: false,
                    StrongNamePublicKey: correctPublicKey),
                secondPluginAssembly,
                new TestPackageAssembly(
                    "Contoso.IdentityFramework",
                    "Contoso.IdentityFramework.dll",
                    wrongFrameworkSource,
                    StrongNamePublicKey: wrongPublicKey));
            File.Copy(rebuiltPath, deploymentPath, overwrite: true);
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyAmbiguous, result.ErrorCode);
            Assert.Contains("Contoso.SecondPlugins", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FirstPreflightFailure_DoesNotCreateProfileOrMutateLaterConfiguration()
    {
        var firstPath = CreateRuntimePluginPackage();
        var secondPath = CreateRuntimePluginPackage();
        try
        {
            var config = new PluginRegistrationConfig
            {
                Assemblies =
                [
                    new PluginAssemblyConfig
                    {
                        Name = "Contoso.WrongAssembly",
                        Type = "Nuget",
                        PackagePath = firstPath
                    },
                    new PluginAssemblyConfig
                    {
                        Name = "Contoso.RuntimePlugins",
                        Type = "Nuget",
                        PackagePath = secondPath
                    }
                ],
                CustomApis =
                [
                    new CustomApiConfig
                    {
                        UniqueName = "ppds_ExistingApi",
                        PluginTypeName = "Contoso.RuntimePlugins.RuntimeOnlyPlugin"
                    }
                ]
            };
            File.WriteAllText(
                _tempConfigFile,
                System.Text.Json.JsonSerializer.Serialize(config));
            var serviceProviderFactoryCalls = 0;

            var exitCode = await DeployCommand.ExecuteAsync(
                new FileInfo(_tempConfigFile),
                profile: null,
                environment: null,
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None,
                serviceProviderFactory: _ =>
                {
                    serviceProviderFactoryCalls++;
                    throw new InvalidOperationException("Profile creation must not run after local preflight failure.");
                });

            Assert.NotEqual(ExitCodes.Success, exitCode);
            Assert.Equal(0, serviceProviderFactoryCalls);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedPluginConstructor_FailsBeforeProfileCreation()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-constructor-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var packagePath = PluginPackageTestFixture.Create(
                scratch,
                "deployment.nupkg",
                "ppds_ConstructorPreflight",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Plugins
                    {
                        public sealed class ValidPlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                        public sealed class ConfiguredPlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """,
                    ReferencesSdk: true));
            var config = new PluginRegistrationConfig
            {
                Assemblies = [NupkgExtractor.Extract(packagePath)]
            };
            var rebuiltPath = PluginPackageTestFixture.Create(
                scratch,
                "rebuilt.nupkg",
                "ppds_ConstructorPreflight",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    string.Empty,
                    PrecompiledImage: PluginPackageTestFixture.CreateMixedPluginImage(
                        TestPluginTypeShape.UnsupportedConstructor,
                        secondaryImplementsPlugin: true)));
            File.Copy(rebuiltPath, packagePath, overwrite: true);
            File.WriteAllText(_tempConfigFile, System.Text.Json.JsonSerializer.Serialize(config));
            var serviceProviderFactoryCalls = 0;

            var exitCode = await DeployCommand.ExecuteAsync(
                new FileInfo(_tempConfigFile),
                profile: null,
                environment: null,
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None,
                serviceProviderFactory: _ =>
                {
                    serviceProviderFactoryCalls++;
                    throw new InvalidOperationException("Profile creation must not run after local preflight failure.");
                });

            Assert.NotEqual(ExitCodes.Success, exitCode);
            Assert.Equal(0, serviceProviderFactoryCalls);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task PreflightAssembliesAsync_RejectsDuplicateCanonicalPath()
    {
        var packagePath = CreateRuntimePluginPackage();
        try
        {
            var first = NupkgExtractor.Extract(packagePath);
            var second = NupkgExtractor.Extract(packagePath);

            var exception = await Assert.ThrowsAsync<PpdsException>(() =>
                DeployCommand.PreflightAssembliesAsync(
                    [first, second],
                    Path.GetTempPath(),
                    CancellationToken.None));

            Assert.Equal(ErrorCodes.Validation.InvalidValue, exception.ErrorCode);
            Assert.Contains("configured more than once", exception.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task PreflightAssembliesAsync_RejectsDuplicatePackageIdAcrossPaths()
    {
        var firstPath = CreateRuntimePluginPackage();
        var secondPath = CreateRuntimePluginPackage();
        try
        {
            var first = NupkgExtractor.Extract(firstPath);
            var second = NupkgExtractor.Extract(secondPath);

            var exception = await Assert.ThrowsAsync<PpdsException>(() =>
                DeployCommand.PreflightAssembliesAsync(
                    [first, second],
                    Path.GetTempPath(),
                    CancellationToken.None));

            Assert.Equal(ErrorCodes.Validation.InvalidValue, exception.ErrorCode);
            Assert.Contains("NuGet package ID 'ppds_RuntimePackage'", exception.Message);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringPreflight_DoesNotCreateProfileOrContinue()
    {
        var firstPath = CreateRuntimePluginPackage();
        var secondPath = PluginPackageTestFixture.Create(
            Path.GetTempPath(),
            $"ppds-second-{Guid.NewGuid():N}.nupkg",
            "ppds_SecondPackage",
            new TestPackageAssembly(
                "Contoso.SecondRuntimePlugins",
                "Contoso.SecondRuntimePlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                public sealed class SecondRuntimePlugin : IPlugin
                {
                    public void Execute(IServiceProvider serviceProvider) { }
                }
                """,
                ReferencesSdk: true));
        try
        {
            var config = new PluginRegistrationConfig
            {
                Assemblies = [NupkgExtractor.Extract(firstPath), NupkgExtractor.Extract(secondPath)]
            };
            File.WriteAllText(
                _tempConfigFile,
                System.Text.Json.JsonSerializer.Serialize(config));
            using var source = new CancellationTokenSource();
            var reads = 0;
            var serviceProviderFactoryCalls = 0;

            async Task<byte[]> CancelOnSecondRead(string path, CancellationToken cancellationToken)
            {
                reads++;
                if (reads == 2)
                    source.Cancel();
                return await File.ReadAllBytesAsync(path, cancellationToken);
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DeployCommand.ExecuteAsync(
                    new FileInfo(_tempConfigFile),
                    profile: null,
                    environment: null,
                    solutionOverride: null,
                    clean: false,
                    dryRun: false,
                    new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                    source.Token,
                    serviceProviderFactory: _ =>
                    {
                        serviceProviderFactoryCalls++;
                        throw new InvalidOperationException("Profile creation must not run after cancellation.");
                    },
                    preflightPackageContentReader: CancelOnSecondRead));

            Assert.Equal(2, reads);
            Assert.Equal(0, serviceProviderFactoryCalls);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancellationAfterFirstAssembly_DoesNotTouchLaterAssemblyOrApis()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"ppds-first-{Guid.NewGuid():N}.dll");
        var secondPath = Path.Combine(Path.GetTempPath(), $"ppds-second-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(firstPath, [0x4d, 0x5a, 0x01]);
        File.WriteAllBytes(secondPath, [0x4d, 0x5a, 0x02]);
        try
        {
            var config = new PluginRegistrationConfig
            {
                Assemblies =
                [
                    new PluginAssemblyConfig
                    {
                        Name = "Contoso.FirstPlugins",
                        Type = "Assembly",
                        Path = firstPath
                    },
                    new PluginAssemblyConfig
                    {
                        Name = "Contoso.SecondPlugins",
                        Type = "Assembly",
                        Path = secondPath
                    }
                ],
                CustomApis =
                [
                    new CustomApiConfig
                    {
                        UniqueName = "ppds_CancelledApi",
                        PluginTypeName = "Contoso.SecondPlugins.ApiPlugin"
                    }
                ]
            };
            File.WriteAllText(_tempConfigFile, System.Text.Json.JsonSerializer.Serialize(config));

            using var source = new CancellationTokenSource();
            var firstAssemblyId = Guid.NewGuid();
            var registration = new Mock<IPluginRegistrationService>();
            registration.Setup(service => service.UpsertAssemblyAsync(
                    "Contoso.FirstPlugins",
                    It.IsAny<byte[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstAssemblyId);
            registration.Setup(service => service.ListTypesForAssemblyAsync(
                    firstAssemblyId,
                    It.IsAny<CancellationToken>()))
                .Callback(source.Cancel)
                .ReturnsAsync([]);
            var customApis = new Mock<ICustomApiService>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DeployCommand.ExecuteAsync(
                    new FileInfo(_tempConfigFile),
                    profile: null,
                    environment: null,
                    solutionOverride: null,
                    clean: false,
                    dryRun: false,
                    new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                    source.Token,
                    serviceProviderFactory: _ => Task.FromResult(
                        new ServiceCollection()
                            .AddSingleton<IPluginRegistrationService>(registration.Object)
                            .AddSingleton<ICustomApiService>(customApis.Object)
                            .BuildServiceProvider())));

            registration.Verify(service => service.UpsertAssemblyAsync(
                "Contoso.FirstPlugins",
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
            registration.Verify(service => service.GetAssemblyByNameAsync(
                "Contoso.SecondPlugins",
                It.IsAny<CancellationToken>()), Times.Never);
            registration.Verify(service => service.UpsertAssemblyAsync(
                "Contoso.SecondPlugins",
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
            registration.Verify(service => service.GetPluginTypeByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            customApis.VerifyNoOtherCalls();
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task DeployAssemblyAsync_CancellationDuringUpload_RethrowsAndStopsDeployment()
    {
        var packagePath = CreateRuntimePluginPackage();
        try
        {
            var config = NupkgExtractor.Extract(packagePath);
            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(service => service.UpsertPackageAsync(
                    "ppds_RuntimePackage",
                    It.IsAny<byte[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                DeployCommand.DeployAssemblyAsync(
                    mock.Object,
                    config,
                    Path.GetTempPath(),
                    solutionOverride: null,
                    clean: false,
                    dryRun: false,
                    new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                    CancellationToken.None));

            mock.Verify(service => service.GetAssemblyIdForPackageAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.ListTypesForAssemblyAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPluginTypeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task DeployAssemblyAsync_CancellationAfterStep_DoesNotInspectOrUpsertImages()
    {
        var assemblyPath = Path.Combine(Path.GetTempPath(), $"ppds-step-cancel-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(assemblyPath, [0x4d, 0x5a]);
        try
        {
            using var source = new CancellationTokenSource();
            var assemblyId = Guid.NewGuid();
            var typeId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            var filterId = Guid.NewGuid();
            var registration = new Mock<IPluginRegistrationService>();
            registration.Setup(service => service.UpsertAssemblyAsync(
                    It.IsAny<string>(),
                    It.IsAny<byte[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            registration.Setup(service => service.ListTypesForAssemblyAsync(
                    assemblyId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([new PluginTypeInfo { Id = typeId, TypeName = "Contoso.Plugin" }]);
            registration.Setup(service => service.ListStepsForTypeAsync(
                    typeId,
                    It.IsAny<PluginListOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    new PluginStepInfo
                    {
                        Id = stepId,
                        Name = "Contoso.Plugin: Update of account",
                        Message = "Update",
                        PrimaryEntity = "account",
                        Stage = "PostOperation",
                        Mode = "Synchronous"
                    }
                ]);
            registration.Setup(service => service.UpsertPluginTypeAsync(
                    assemblyId,
                    "Contoso.Plugin",
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(typeId);
            registration.Setup(service => service.GetSdkMessageIdAsync(
                    "Update",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(messageId);
            registration.Setup(service => service.GetSdkMessageFilterIdAsync(
                    messageId,
                    "account",
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(filterId);
            registration.Setup(service => service.UpsertStepAsync(
                    typeId,
                    "pluginType",
                    It.IsAny<PluginStepConfig>(),
                    messageId,
                    filterId,
                    It.IsAny<string?>(),
                    It.IsAny<StepIdentityResolution?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(source.Cancel)
                .ReturnsAsync(stepId);

            var config = new PluginAssemblyConfig
            {
                Name = "Contoso.Plugins",
                Type = "Assembly",
                Path = assemblyPath,
                Types =
                {
                    new PluginTypeConfig
                    {
                        TypeName = "Contoso.Plugin",
                        Steps =
                        {
                            new PluginStepConfig
                            {
                                Message = "Update",
                                Entity = "account",
                                Stage = "PostOperation",
                                Mode = "Synchronous",
                                Images =
                                {
                                    new PluginImageConfig
                                    {
                                        Name = "PreImage",
                                        ImageType = "PreImage"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DeployCommand.DeployAssemblyAsync(
                    registration.Object,
                    config,
                    Path.GetTempPath(),
                    solutionOverride: null,
                    clean: false,
                    dryRun: false,
                    new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                    source.Token));

            registration.Verify(service => service.ListImagesForStepAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()), Times.Never);
            registration.Verify(service => service.UpsertImageAsync(
                It.IsAny<Guid>(),
                It.IsAny<PluginImageConfig>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(assemblyPath);
        }
    }

    [Fact]
    public async Task DeployAssemblyAsync_StrippedPluginEvidence_FailsBeforeServerCalls()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-stripped-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment.nupkg",
                "ppds_StrippedPackage",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Plugins
                    {
                        public sealed class RuntimePlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """,
                    ReferencesSdk: true));
            var config = NupkgExtractor.Extract(deploymentPath);
            var strippedPath = PluginPackageTestFixture.Create(
                scratch,
                "stripped.nupkg",
                "ppds_StrippedPackage",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    "namespace Contoso.Plugins { public sealed class RuntimePlugin { } }"));
            File.Copy(strippedPath, deploymentPath, overwrite: true);
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            Assert.Contains("no longer public, concrete, closed runtime", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task DeployAssemblyAsync_SpoofedRegistrationMetadata_FailsBeforeServerCalls(
        bool dryRun,
        bool customApi,
        bool wrongTokenAssembly)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-lookalike-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var attributeName = customApi ? "CustomApiAttribute" : "PluginStepAttribute";
            var attributeUsage = attributeName.Replace("Attribute", string.Empty, StringComparison.Ordinal);
            TestPackageAssembly[] assemblies;
            if (wrongTokenAssembly)
            {
                var wrongPublicKey = System.Reflection.AssemblyName
                    .GetAssemblyName(Path.Combine(
                        AppContext.BaseDirectory,
                        "TestAssets",
                        "Microsoft.Xrm.Sdk.net462.dll"))
                    .GetPublicKey();
                Assert.NotNull(wrongPublicKey);
                assemblies =
                [
                    new TestPackageAssembly(
                        "PPDS.Plugins",
                        "PPDS.Plugins.dll",
                        $"namespace PPDS.Plugins {{ public sealed class {attributeName} : System.Attribute {{ }} }}",
                        StrongNamePublicKey: wrongPublicKey),
                    new TestPackageAssembly(
                        "Contoso.LookalikeHandler",
                        "Contoso.LookalikeHandler.dll",
                        $"[PPDS.Plugins.{attributeUsage}] public sealed class LookalikeHandler {{ }}",
                        AssemblyReferences: ["PPDS.Plugins"])
                ];
            }
            else
            {
                assemblies =
                [
                    new TestPackageAssembly(
                        "Contoso.LookalikeHandler",
                        "Contoso.LookalikeHandler.dll",
                        $$"""
                        namespace PPDS.Plugins
                        {
                            public sealed class {{attributeName}} : System.Attribute { }
                        }

                        [PPDS.Plugins.{{attributeUsage}}]
                        public sealed class LookalikeHandler { }
                        """)
                ];
            }
            var packagePath = PluginPackageTestFixture.Create(
                scratch,
                "lookalike.nupkg",
                "ppds_LookalikePackage",
                assemblies);
            var config = new PluginAssemblyConfig
            {
                Name = "Contoso.LookalikeHandler",
                Type = "Nuget",
                PackagePath = Path.GetFileName(packagePath),
                AllTypeNames = ["LookalikeHandler"]
            };
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeployAssemblyAsync_OfficialRegistrationOnNonPluginHandler_FailsBeforeServerCalls(
        bool dryRun,
        bool customApi)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-non-plugin-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var annotation = customApi
                ? "[CustomApi(UniqueName = \"ppds_Invalid\", DisplayName = \"Invalid\")]"
                : "[PluginStep(Message = \"Create\", EntityLogicalName = \"account\", Stage = PluginStage.PreOperation)]";
            var packagePath = PluginPackageTestFixture.Create(
                scratch,
                "non-plugin.nupkg",
                "ppds_NonPluginPackage",
                new TestPackageAssembly(
                    "Contoso.NonPluginHandler",
                    "Contoso.NonPluginHandler.dll",
                    $$"""
                    using PPDS.Plugins;
                    {{annotation}}
                    public sealed class NonPluginHandler { }
                    """,
                    ReferencesPpdsPlugins: true));
            var config = new PluginAssemblyConfig
            {
                Name = "Contoso.NonPluginHandler",
                Type = "Nuget",
                PackagePath = Path.GetFileName(packagePath),
                AllTypeNames = ["NonPluginHandler"]
            };
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Validation.InvalidValue, result.ErrorCode);
            Assert.Contains("cannot be proven to implement", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task DeployAssemblyAsync_InvalidSdkVersionOrCulture_FailsBeforeServerCalls(
        bool dryRun,
        bool invalidVersion)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-invalid-sdk-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var packagePath = PluginPackageTestFixture.Create(
                scratch,
                "invalid-sdk.nupkg",
                "ppds_InvalidSdkIdentity",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    string.Empty,
                    PrecompiledImage: PluginPackageTestFixture.CreateDirectPluginInterfaceConsumerImage(
                        invalidVersion ? new Version(8, 2, 0, 0) : new Version(9, 0, 0, 0),
                        invalidVersion ? null : "en-US")));
            var config = new PluginAssemblyConfig
            {
                Name = "Contoso.RuntimePlugins",
                Type = "Nuget",
                PackagePath = Path.GetFileName(packagePath),
                AllTypeNames = ["Contoso.RuntimePlugin"]
            };
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            Assert.Contains("Contoso.RuntimePlugin", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, TestPluginTypeShape.Concrete, false)]
    [InlineData(false, TestPluginTypeShape.Abstract, false)]
    [InlineData(false, TestPluginTypeShape.Interface, false)]
    [InlineData(false, TestPluginTypeShape.OpenGeneric, false)]
    [InlineData(false, TestPluginTypeShape.Static, false)]
    [InlineData(false, TestPluginTypeShape.ValueType, false)]
    [InlineData(false, TestPluginTypeShape.PrivateConstructor, false)]
    [InlineData(false, TestPluginTypeShape.UnsupportedConstructor, false)]
    [InlineData(false, TestPluginTypeShape.Concrete, true)]
    [InlineData(false, TestPluginTypeShape.Abstract, true)]
    [InlineData(false, TestPluginTypeShape.Interface, true)]
    [InlineData(false, TestPluginTypeShape.OpenGeneric, true)]
    [InlineData(false, TestPluginTypeShape.Static, true)]
    [InlineData(false, TestPluginTypeShape.ValueType, true)]
    [InlineData(false, TestPluginTypeShape.PrivateConstructor, true)]
    [InlineData(false, TestPluginTypeShape.UnsupportedConstructor, true)]
    [InlineData(true, TestPluginTypeShape.Concrete, false)]
    [InlineData(true, TestPluginTypeShape.Abstract, false)]
    [InlineData(true, TestPluginTypeShape.Interface, false)]
    [InlineData(true, TestPluginTypeShape.OpenGeneric, false)]
    [InlineData(true, TestPluginTypeShape.Static, false)]
    [InlineData(true, TestPluginTypeShape.ValueType, false)]
    [InlineData(true, TestPluginTypeShape.PrivateConstructor, false)]
    [InlineData(true, TestPluginTypeShape.UnsupportedConstructor, false)]
    [InlineData(true, TestPluginTypeShape.Concrete, true)]
    [InlineData(true, TestPluginTypeShape.Abstract, true)]
    [InlineData(true, TestPluginTypeShape.Interface, true)]
    [InlineData(true, TestPluginTypeShape.OpenGeneric, true)]
    [InlineData(true, TestPluginTypeShape.Static, true)]
    [InlineData(true, TestPluginTypeShape.ValueType, true)]
    [InlineData(true, TestPluginTypeShape.PrivateConstructor, true)]
    [InlineData(true, TestPluginTypeShape.UnsupportedConstructor, true)]
    public async Task DeployAssemblyAsync_MixedInvalidOfficialHandler_FailsBeforeServerCallsEvenWithMatchingDigest(
        bool dryRun,
        TestPluginTypeShape shape,
        bool customApi)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-mixed-invalid-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var packagePath = PluginPackageTestFixture.Create(
                scratch,
                "mixed-invalid.nupkg",
                "ppds_MixedInvalidHandler",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    string.Empty,
                    PrecompiledImage: PluginPackageTestFixture.CreateMixedPluginImage(
                        shape,
                        secondaryImplementsPlugin: shape != TestPluginTypeShape.Concrete,
                        officialAttributeName: customApi ? "CustomApiAttribute" : "PluginStepAttribute")));
            var packageBytes = File.ReadAllBytes(packagePath);
            var config = new PluginAssemblyConfig
            {
                Name = "Contoso.RuntimePlugins",
                Type = "Nuget",
                PackagePath = Path.GetFileName(packagePath),
                PackageContentSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(packageBytes)).ToLowerInvariant(),
                AllTypeNames = ["Contoso.Plugins.ValidPlugin"],
                Types = customApi
                    ? []
                    : [new PluginTypeConfig { TypeName = "Contoso.Plugins.ConfiguredPlugin", Steps = [] }],
                CustomApis = customApi
                    ?
                    [
                        new CustomApiConfig
                        {
                            UniqueName = "ppds_Invalid",
                            PluginTypeName = "Contoso.Plugins.ConfiguredPlugin"
                        }
                    ]
                    : null
            };
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Validation.InvalidValue, result.ErrorCode);
            Assert.Contains("cannot be proven to implement", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, TestPluginTypeShape.Concrete)]
    [InlineData(false, TestPluginTypeShape.Private)]
    [InlineData(false, TestPluginTypeShape.Abstract)]
    [InlineData(false, TestPluginTypeShape.OpenGeneric)]
    [InlineData(false, TestPluginTypeShape.ValueType)]
    [InlineData(false, TestPluginTypeShape.PrivateConstructor)]
    [InlineData(false, TestPluginTypeShape.UnsupportedConstructor)]
    [InlineData(true, TestPluginTypeShape.Concrete)]
    [InlineData(true, TestPluginTypeShape.Private)]
    [InlineData(true, TestPluginTypeShape.Abstract)]
    [InlineData(true, TestPluginTypeShape.OpenGeneric)]
    [InlineData(true, TestPluginTypeShape.ValueType)]
    [InlineData(true, TestPluginTypeShape.PrivateConstructor)]
    [InlineData(true, TestPluginTypeShape.UnsupportedConstructor)]
    public async Task DeployAssemblyAsync_ConfiguredTypeLosesRuntimeDeployability_FailsBeforeServerCalls(
        bool dryRun,
        TestPluginTypeShape shape)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-partial-strip-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment.nupkg",
                "ppds_PartialStrip",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.Plugins
                    {
                        public sealed class ValidPlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                        public sealed class ConfiguredPlugin : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """,
                    ReferencesSdk: true));
            var config = NupkgExtractor.Extract(deploymentPath);
            var rebuiltPath = PluginPackageTestFixture.Create(
                scratch,
                "rebuilt.nupkg",
                "ppds_PartialStrip",
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    string.Empty,
                    PrecompiledImage: PluginPackageTestFixture.CreateMixedPluginImage(
                        shape,
                        secondaryImplementsPlugin: shape != TestPluginTypeShape.Concrete)));
            File.Copy(rebuiltPath, deploymentPath, overwrite: true);
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            Assert.Contains("Contoso.Plugins.ConfiguredPlugin", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "Contoso.ExternalFramework")]
    [InlineData(true, "Contoso.ExternalFramework")]
    [InlineData(false, "System.ContosoPluginFramework")]
    [InlineData(true, "System.ContosoPluginFramework")]
    public async Task DeployAssemblyAsync_AddedUnresolvedCandidateAncestry_FailsBeforeServerCalls(
        bool dryRun,
        string missingAssemblyName)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-unresolved-candidate-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            const string knownPluginSource = """
                using System;
                using Microsoft.Xrm.Sdk;
                public sealed class KnownPlugin : IPlugin
                {
                    public void Execute(IServiceProvider serviceProvider) { }
                }
                """;
            var deploymentPath = PluginPackageTestFixture.Create(
                scratch,
                "deployment.nupkg",
                "ppds_UnresolvedCandidate",
                new TestPackageAssembly(
                    "Contoso.KnownPlugins",
                    "Contoso.KnownPlugins.dll",
                    knownPluginSource,
                    ReferencesSdk: true));
            var config = NupkgExtractor.Extract(deploymentPath);
            var rebuiltPath = PluginPackageTestFixture.Create(
                scratch,
                "rebuilt.nupkg",
                "ppds_UnresolvedCandidate",
                new TestPackageAssembly(
                    "Contoso.KnownPlugins",
                    "Contoso.KnownPlugins.dll",
                    knownPluginSource,
                    ReferencesSdk: true),
                new TestPackageAssembly(
                    missingAssemblyName,
                    $"{missingAssemblyName}.dll",
                    "namespace Contoso.External { public abstract class PluginBase { } }",
                    IncludeInPackage: false),
                new TestPackageAssembly(
                    "Contoso.UnresolvedCandidate",
                    "Contoso.UnresolvedCandidate.dll",
                    "namespace Contoso { public sealed class PossiblePlugin : Contoso.External.PluginBase { } }",
                    AssemblyReferences: [missingAssemblyName]));
            File.Copy(rebuiltPath, deploymentPath, overwrite: true);
            var mock = new Mock<IPluginRegistrationService>();

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyAmbiguous, result.ErrorCode);
            Assert.Contains("Contoso.UnresolvedCandidate", result.Error);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void SelectAssemblyCustomApisForDeployment_GatesApisOnAssemblySuccess(
        bool assemblySucceeded,
        int expectedCount)
    {
        var customApi = new CustomApiConfig
        {
            UniqueName = "ppds_GatedApi",
            PluginTypeName = "Contoso.Plugins.ApiPlugin"
        };
        var assembly = new PluginAssemblyConfig
        {
            Name = "Contoso.Plugins",
            Type = "Nuget",
            PackagePath = "contoso.nupkg",
            CustomApis = [customApi]
        };
        var result = new DeployCommand.DeploymentResult
        {
            AssemblyName = assembly.Name,
            Success = assemblySucceeded,
            ErrorCode = assemblySucceeded ? null : ErrorCodes.Plugin.PackageAssemblyMismatch
        };

        var selected = DeployCommand.SelectAssemblyCustomApisForDeployment([assembly], [result]);

        Assert.Equal(expectedCount, selected.Count);
        if (assemblySucceeded)
            Assert.Same(customApi, Assert.Single(selected));
    }

    [Fact]
    public async Task SelectAssemblyCustomApisForDeployment_FailedPackagePreflightSkipsAssemblyApis()
    {
        var packagePath = CreateRuntimePluginPackage();
        try
        {
            var mock = new Mock<IPluginRegistrationService>();
            var assembly = new PluginAssemblyConfig
            {
                Name = "ppds_RuntimePackage.1.0.0",
                Type = "Nuget",
                PackagePath = packagePath,
                CustomApis =
                [
                    new CustomApiConfig
                    {
                        UniqueName = "ppds_GatedApi",
                        PluginTypeName = "Contoso.RuntimePlugins.RuntimePlugin"
                    }
                ]
            };

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                assembly,
                Path.GetTempPath(),
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            var selected = DeployCommand.SelectAssemblyCustomApisForDeployment([assembly], [result]);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyMismatch, result.ErrorCode);
            Assert.Empty(selected);
            mock.Verify(service => service.GetPackageByNameAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task DeployAssemblyAsync_ConfigExtractedWithReferenceDir_DryRunDoesNotReloadDependencyGraph()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ppds-reference-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        try
        {
            var packagePath = PluginPackageTestFixture.Create(
                scratch,
                "reference-dir-package.nupkg",
                "ppds_ReferenceDirPackage",
                new TestPackageAssembly(
                    "Contoso.ExternalFramework",
                    "Contoso.ExternalFramework.dll",
                    """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso.External
                    {
                        public abstract class PluginBase : IPlugin
                        {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """,
                    ReferencesSdk: true,
                    IncludeInPackage: false),
                new TestPackageAssembly(
                    "Contoso.RuntimePlugins",
                    "Contoso.RuntimePlugins.dll",
                    """
                    using PPDS.Plugins;
                    namespace Contoso.Plugins
                    {
                        [PluginStep(
                            Message = "Create",
                            EntityLogicalName = "account",
                            Stage = PluginStage.PreOperation)]
                        public sealed class RuntimeOnlyPlugin : Contoso.External.PluginBase { }
                    }
                    """,
                    ReferencesSdk: true,
                    ReferencesPpdsPlugins: true,
                    AssemblyReferences: ["Contoso.ExternalFramework"]));

            var config = NupkgExtractor.Extract(packagePath, [scratch]);
            Assert.Equal(["Contoso.Plugins.RuntimeOnlyPlugin"], config.AllTypeNames);

            // Prove deploy preflight uses portable manifest/type identity rather than relying on
            // the extraction machine's --reference-dir still being available.
            File.Delete(Path.Combine(scratch, "Contoso.ExternalFramework.dll"));

            var packageId = Guid.NewGuid();
            var assemblyId = Guid.NewGuid();
            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(service => service.GetPackageByNameAsync(
                    "ppds_ReferenceDirPackage",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginPackageInfo { Id = packageId, Name = "ppds_ReferenceDirPackage" });
            mock.Setup(service => service.GetAssemblyIdForPackageAsync(
                    packageId,
                    "Contoso.RuntimePlugins",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            mock.Setup(service => service.ListTypesForAssemblyAsync(
                    assemblyId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                scratch,
                solutionOverride: null,
                clean: false,
                dryRun: true,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Null(result.Error);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task DeployAssemblyAsync_PackageAssemblyNameComparison_IsCaseInsensitive()
    {
        var packagePath = CreateRuntimePluginPackage();
        try
        {
            var packageId = Guid.NewGuid();
            var assemblyId = Guid.NewGuid();
            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(service => service.UpsertPackageAsync(
                    "ppds_RuntimePackage",
                    It.IsAny<byte[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(packageId);
            mock.Setup(service => service.GetAssemblyIdForPackageAsync(
                    packageId,
                    "Contoso.RuntimePlugins",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(assemblyId);
            mock.Setup(service => service.ListTypesForAssemblyAsync(
                    assemblyId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var config = new PluginAssemblyConfig
            {
                Name = "contoso.runtimeplugins",
                Type = "Nuget",
                PackagePath = packagePath
            };

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                Path.GetTempPath(),
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Null(result.Error);
            mock.Verify(service => service.UpsertPackageAsync(
                "ppds_RuntimePackage",
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
            mock.Verify(service => service.GetAssemblyIdForPackageAsync(
                packageId,
                "Contoso.RuntimePlugins",
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task DeployAssemblyAsync_AssemblyUnavailableAfterUpload_ReturnsStructuredRecoveryWithoutCleanup()
    {
        var packagePath = CreateRuntimePluginPackage();
        try
        {
            var packageId = Guid.NewGuid();
            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(service => service.UpsertPackageAsync(
                    "ppds_RuntimePackage",
                    It.IsAny<byte[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(packageId);
            mock.Setup(service => service.GetAssemblyIdForPackageAsync(
                    packageId,
                    "Contoso.RuntimePlugins",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(value: null);

            var config = new PluginAssemblyConfig
            {
                Name = "Contoso.RuntimePlugins",
                Type = "Nuget",
                PackagePath = packagePath
            };

            var result = await DeployCommand.DeployAssemblyAsync(
                mock.Object,
                config,
                Path.GetTempPath(),
                solutionOverride: null,
                clean: false,
                dryRun: false,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.Plugin.PackageAssemblyUnavailableAfterUpload, result.ErrorCode);
            Assert.Contains("may now be partially deployed", result.Error);
            Assert.NotNull(result.RecoveryGuidance);
            Assert.Contains("plugins get package ppds_RuntimePackage", result.RecoveryGuidance);
            Assert.Contains("plugins list --package ppds_RuntimePackage", result.RecoveryGuidance);
            Assert.Contains(packageId.ToString(), result.RecoveryGuidance);
            Assert.Contains("destructive", result.RecoveryGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("did not attempt automatic cleanup", result.RecoveryGuidance);
            Assert.DoesNotContain("preview", result.RecoveryGuidance, StringComparison.OrdinalIgnoreCase);
            mock.Verify(service => service.UpsertPackageAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
            mock.Verify(service => service.UnregisterPackageAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    private static string CreateRuntimePluginPackage()
    {
        return PluginPackageTestFixture.Create(
            Path.GetTempPath(),
            $"ppds-runtime-{Guid.NewGuid():N}.nupkg",
            "ppds_RuntimePackage",
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "renamed-binary.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true));
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
