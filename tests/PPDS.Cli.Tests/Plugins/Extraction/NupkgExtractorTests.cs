using System.IO.Compression;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Tests.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Extraction;

[Trait("Category", "Unit")]
public class NupkgExtractorTests : IDisposable
{
    private readonly string _scratch;

    public NupkgExtractorTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), $"ppds-nupkg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Extract_NupkgMissingLibFolder_Throws()
    {
        // Build a minimal .nupkg-like zip with only a .nuspec at the root and no lib/ folder.
        var nupkgPath = Path.Combine(_scratch, "empty.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("empty.nuspec");
            using var entryStream = entry.Open();
            var xml = Encoding.UTF8.GetBytes("""
                <?xml version="1.0"?>
                <package><metadata><id>empty</id><version>1.0.0</version></metadata></package>
                """);
            entryStream.Write(xml, 0, xml.Length);
        }

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
        Assert.Contains("lib/net462", ex.Message);
    }

    [Fact]
    public void Extract_NupkgWithZipSlipEntry_ThrowsPpdsException()
    {
        // Handcraft a zip archive that contains an entry whose full name traverses up out of the
        // extraction directory (classic zip-slip payload). The SUT validates every canonical
        // destination before calling ExtractToDirectory so containment does not depend on the
        // runtime's built-in protection.
        var nupkgPath = Path.Combine(_scratch, "evil.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Benign nuspec for completeness.
            var nuspec = archive.CreateEntry("evil.nuspec");
            using (var entryStream = nuspec.Open())
            {
                var xml = Encoding.UTF8.GetBytes("""
                    <?xml version="1.0"?>
                    <package><metadata><id>evil</id><version>1.0.0</version></metadata></package>
                    """);
                entryStream.Write(xml, 0, xml.Length);
            }

            // Hostile entry — path escapes the temp dir via ".." segments.
            var hostile = archive.CreateEntry("../../escaped.txt");
            using (var hostileStream = hostile.Open())
            {
                var payload = Encoding.UTF8.GetBytes("pwned");
                hostileStream.Write(payload, 0, payload.Length);
            }

            var pluginAssembly = archive.CreateEntry("lib/net462/Evil.dll");
            using var pluginStream = pluginAssembly.Open();
            pluginStream.WriteByte(0);
        }

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
        Assert.Contains("escapes the extraction directory", ex.Message);
        Assert.Contains("../../escaped.txt", ex.Message);
    }

    [Fact]
    public void Extract_NupkgWithUnloadableAssembly_ThrowsInsteadOfReturningEmpty()
    {
        // A lib/net462 folder whose only DLL is not a valid assembly. Previously every
        // per-assembly load failure was swallowed and Extract returned an empty config (a
        // misleading "0 plugin types" result). The extractor now surfaces the failure so the
        // user sees the real cause instead of a silent no-op (#1294).
        var nupkgPath = Path.Combine(_scratch, "broken.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var nuspec = archive.CreateEntry("broken.nuspec");
            using (var entryStream = nuspec.Open())
            {
                var xml = Encoding.UTF8.GetBytes("""
                    <?xml version="1.0"?>
                    <package><metadata><id>broken</id><version>1.0.0</version></metadata></package>
                    """);
                entryStream.Write(xml, 0, xml.Length);
            }

            // Not a valid PE image — LoadFromAssemblyPath fails for this "assembly".
            var dll = archive.CreateEntry("lib/net462/Broken.dll");
            using var dllStream = dll.Open();
            var payload = Encoding.UTF8.GetBytes("this is not a PE file");
            dllStream.Write(payload, 0, payload.Length);
        }

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.Equal(ErrorCodes.Operation.Dependency, ex.ErrorCode);
        Assert.Contains("Broken.dll", ex.Message);
    }

    [Fact]
    public void Extract_Net48OnlyPackage_ThrowsStructuredValidationError()
    {
        var nupkgPath = Path.Combine(_scratch, "net48-plugin.nupkg");
        CreatePluginPackage(nupkgPath, "net48");

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
        Assert.Contains("lib/net462", ex.Message);
        Assert.Contains("lib/net471", ex.Message);
        Assert.Contains("lib/net48", ex.Message);
    }

    [Fact]
    public void Extract_Net471Package_SelectsSupportedPluginAssembly()
    {
        var nupkgPath = Path.Combine(_scratch, "net471-plugin.nupkg");
        CreatePluginPackage(nupkgPath, "net471");

        var config = NupkgExtractor.Extract(nupkgPath);

        Assert.Equal("Nuget", config.Type);
        var type = Assert.Single(config.Types);
        Assert.Equal("Net48Plugin", type.TypeName);
        var step = Assert.Single(type.Steps);
        Assert.Equal("Create", step.Message);
        Assert.Equal("account", step.Entity);
    }

    [Fact]
    public void Extract_EmptyNet462GroupAndNet471DllAssets_SelectsNet471()
    {
        var nupkgPath = Path.Combine(_scratch, "multi-target-plugin.nupkg");
        CreatePluginPackage(nupkgPath, "net471", emptyFramework: "net462");

        var config = NupkgExtractor.Extract(nupkgPath);

        Assert.Equal("Nuget", config.Type);
        var type = Assert.Single(config.Types);
        Assert.Equal("Net48Plugin", type.TypeName);
    }

    [Fact]
    public void Extract_ZeroAttributeRuntimePlugin_UsesManifestNameAndDoesNotInventSteps()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "ppds_RuntimePackage.1.0.0.nupkg",
            "ppds_RuntimePackage",
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "renamed-binary.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;

                namespace Contoso.Plugins
                {
                    public abstract class PluginBase : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }

                    public sealed class RuntimeOnlyPlugin : PluginBase { }
                }
                """,
                ReferencesSdk: true));

        var inspection = NupkgExtractor.Inspect(nupkgPath);
        var config = inspection.Assembly;

        Assert.Equal("Contoso.RuntimePlugins", config.Name);
        Assert.Equal("Nuget", config.Type);
        Assert.Equal("ppds_RuntimePackage.1.0.0.nupkg", config.PackagePath);
        Assert.Equal(["Contoso.Plugins.RuntimeOnlyPlugin"], config.AllTypeNames);
        Assert.Empty(config.Types);
        Assert.Equal(1, inspection.InspectedAssemblyCount);
        Assert.Equal(0, inspection.AnnotatedTypeCount);
        Assert.Equal(1, inspection.RuntimePluginTypeCount);
    }

    [Fact]
    public void Inspect_RuntimePluginWithUnresolvedUnrelatedSdkInterface_KeepsPluginCandidate()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "plugin-with-sdk-helper.nupkg",
            "ppds_PluginWithSdkHelper",
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }

                    public sealed class TraceHelper : ITracingService
                    {
                        public void Trace(string format, params object[] args) { }
                    }
                }
                """,
                ReferencesSdk: true));

        var inspection = NupkgExtractor.Inspect(nupkgPath);

        Assert.Equal("Contoso.RuntimePlugins", inspection.Assembly.Name);
        Assert.Equal(["Contoso.Plugins.RuntimeOnlyPlugin"], inspection.Assembly.AllTypeNames);
        Assert.Equal(1, inspection.RuntimePluginTypeCount);
    }

    [Fact]
    public void Inspect_RuntimePluginWithUnresolvedInterfaceBeforeResolvableBase_KeepsPluginCandidate()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "plugin-with-unresolved-interface.nupkg",
            "ppds_PluginWithUnresolvedInterface",
            new TestPackageAssembly(
                "Contoso.PluginFramework",
                "Contoso.PluginFramework.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Framework
                {
                    public abstract class PluginBase : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true),
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin :
                        Contoso.Framework.PluginBase, ITracingService
                    {
                        public void Trace(string format, params object[] args) { }
                    }
                }
                """,
                ReferencesSdk: true,
                AssemblyReferences: ["Contoso.PluginFramework"]));

        var inspection = NupkgExtractor.Inspect(nupkgPath);

        Assert.Equal("Contoso.RuntimePlugins", inspection.Assembly.Name);
        Assert.Equal(["Contoso.Plugins.RuntimeOnlyPlugin"], inspection.Assembly.AllTypeNames);
        Assert.Equal(1, inspection.RuntimePluginTypeCount);
    }

    [Fact]
    public void Extract_OpenGenericRuntimePlugin_RejectsPackageWithoutDeployableCandidate()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "open-generic-plugin.nupkg",
            "ppds_OpenGenericPlugin",
            new TestPackageAssembly(
                "Contoso.OpenGenericPlugin",
                "Contoso.OpenGenericPlugin.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class OpenPlugin<T> : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("0 runtime IPlugin types", exception.Message);
    }

    [Fact]
    public void Extract_AnnotatedOpenGenericRuntimePlugin_RejectsPackageWithoutDeployableCandidate()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "annotated-open-generic-plugin.nupkg",
            "ppds_AnnotatedOpenGenericPlugin",
            new TestPackageAssembly(
                "Contoso.AnnotatedOpenGenericPlugin",
                "Contoso.AnnotatedOpenGenericPlugin.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                using PPDS.Plugins;
                namespace Contoso.Plugins
                {
                    [PluginStep(
                        Message = "Create",
                        EntityLogicalName = "account",
                        Stage = PluginStage.PreOperation)]
                    [CustomApi(UniqueName = "ppds_OpenGeneric", DisplayName = "Open Generic")]
                    public sealed class OpenPlugin<T> : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true,
                ReferencesPpdsPlugins: true));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Validation.InvalidValue, exception.ErrorCode);
        Assert.Contains("public, concrete, closed runtime", exception.Message);
    }

    [Fact]
    public void Extract_SpoofedPluginInterfaceFromUnrelatedAssembly_RejectsPackage()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "spoofed-plugin-interface.nupkg",
            "ppds_SpoofedPluginInterface",
            new TestPackageAssembly(
                "Contoso.FakeSdk",
                "Contoso.FakeSdk.dll",
                """
                using System;
                namespace Microsoft.Xrm.Sdk
                {
                    public interface IPlugin
                    {
                        void Execute(IServiceProvider serviceProvider);
                    }
                }
                """),
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class SpoofedPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                AssemblyReferences: ["Contoso.FakeSdk"]));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("0 runtime IPlugin types", exception.Message);
    }

    [Fact]
    public void InspectConfiguredAssemblyIdentity_UsesBufferedPackageSnapshot()
    {
        var inspectedPackagePath = PluginPackageTestFixture.Create(
            _scratch,
            "inspected-package.nupkg",
            "ppds_InspectedPackage",
            new TestPackageAssembly(
                "Contoso.InspectedPlugins",
                "Contoso.InspectedPlugins.dll",
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
        var replacementPackagePath = PluginPackageTestFixture.Create(
            _scratch,
            "replacement-package.nupkg",
            "ppds_ReplacementPackage",
            new TestPackageAssembly(
                "Contoso.ReplacementPlugins",
                "Contoso.ReplacementPlugins.dll",
                """
                namespace Contoso.Plugins
                {
                    public sealed class ReplacementPlugin { }
                }
                """));
        var inspectedBytes = File.ReadAllBytes(inspectedPackagePath);
        var config = new PluginAssemblyConfig
        {
            Name = "Contoso.InspectedPlugins",
            Type = "Nuget",
            PackagePath = replacementPackagePath
        };

        var assemblyName = NupkgExtractor.InspectConfiguredAssemblyIdentity(
            inspectedBytes,
            replacementPackagePath,
            config);

        Assert.Equal("Contoso.InspectedPlugins", assemblyName);
    }

    [Fact]
    public void Extract_LocalTypeDefinitionNamedIPlugin_RejectsPackage()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "local-spoofed-plugin-interface.nupkg",
            "ppds_LocalSpoofedPluginInterface",
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                using System;
                namespace Microsoft.Xrm.Sdk
                {
                    public interface IPlugin
                    {
                        void Execute(IServiceProvider serviceProvider);
                    }
                }

                namespace Contoso.Plugins
                {
                    public sealed class SpoofedPlugin : Microsoft.Xrm.Sdk.IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("0 runtime IPlugin types", exception.Message);
    }

    [Fact]
    public void Extract_IPluginFromMicrosoftXrmSdkWithWrongToken_RejectsPackage()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "wrong-token-plugin-interface.nupkg",
            "ppds_WrongTokenPluginInterface",
            new TestPackageAssembly(
                "Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.dll",
                """
                using System;
                namespace Microsoft.Xrm.Sdk
                {
                    public interface IPlugin
                    {
                        void Execute(IServiceProvider serviceProvider);
                    }
                }
                """),
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class SpoofedPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                AssemblyReferences: ["Microsoft.Xrm.Sdk"]));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("0 runtime IPlugin types", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extract_IPluginFromMicrosoftXrmSdkWithInvalidVersionOrCulture_RejectsPackage(
        bool invalidVersion)
    {
        var image = PluginPackageTestFixture.CreateDirectPluginInterfaceConsumerImage(
            invalidVersion ? new Version(8, 2, 0, 0) : new Version(9, 0, 0, 0),
            invalidVersion ? null : "en-US");
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            $"invalid-sdk-identity-{invalidVersion}.nupkg",
            "ppds_InvalidSdkIdentity",
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                string.Empty,
                PrecompiledImage: image));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Operation.Dependency, exception.ErrorCode);
        Assert.Contains("Microsoft.Xrm.Sdk.IPlugin", exception.Message);
        Assert.Contains("--reference-dir", exception.Message);
    }

    [Fact]
    public void Extract_IPluginForwardedFromNonSdkIdentity_RejectsPackage()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "forwarded-spoofed-plugin-interface.nupkg",
            "ppds_ForwardedSpoofedPluginInterface",
            new TestPackageAssembly(
                "Contoso.ForwardedSdk",
                "Contoso.ForwardedSdk.dll",
                """
                using System;
                namespace Microsoft.Xrm.Sdk
                {
                    public interface IPlugin
                    {
                        void Execute(IServiceProvider serviceProvider);
                    }
                }
                """),
            new TestPackageAssembly(
                "Microsoft.Xrm.Sdk",
                "Microsoft.Xrm.Sdk.dll",
                """
                using System.Runtime.CompilerServices;
                [assembly: TypeForwardedTo(typeof(Microsoft.Xrm.Sdk.IPlugin))]
                """,
                AssemblyReferences: ["Contoso.ForwardedSdk"]),
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Plugins
                {
                    public sealed class SpoofedPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                AssemblyReferences: ["Microsoft.Xrm.Sdk", "Contoso.ForwardedSdk"]));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("0 runtime IPlugin types", exception.Message);
    }

    [Fact]
    public void Extract_RuntimePluginInheritedThroughExternalBase_DetectsConcreteType()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "external-base.nupkg",
            "ppds_ExternalBase",
            new TestPackageAssembly(
                "Contoso.PluginFramework",
                "Contoso.PluginFramework.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Framework
                {
                    public abstract class PluginBase : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true),
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin : Contoso.Framework.PluginBase { }
                }
                """,
                ReferencesSdk: true,
                AssemblyReferences: ["Contoso.PluginFramework"]));

        var inspection = NupkgExtractor.Inspect(nupkgPath);

        Assert.Equal("Contoso.RuntimePlugins", inspection.Assembly.Name);
        Assert.Equal(["Contoso.Plugins.RuntimeOnlyPlugin"], inspection.Assembly.AllTypeNames);
        Assert.Equal(2, inspection.InspectedAssemblyCount);
        Assert.Equal(1, inspection.RuntimePluginTypeCount);
    }

    [Fact]
    public void Inspect_RuntimePluginInheritedThroughExternalGenericBase_DetectsConcreteType()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "external-generic-base.nupkg",
            "ppds_ExternalGenericBase",
            new TestPackageAssembly(
                "Contoso.PluginFramework",
                "Contoso.PluginFramework.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Framework
                {
                    public abstract class PluginBase<T> : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true),
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin :
                        Contoso.Framework.PluginBase<string> { }
                }
                """,
                ReferencesSdk: true,
                AssemblyReferences: ["Contoso.PluginFramework"]));

        var inspection = NupkgExtractor.Inspect(nupkgPath);

        Assert.Equal("Contoso.RuntimePlugins", inspection.Assembly.Name);
        Assert.Equal(["Contoso.Plugins.RuntimeOnlyPlugin"], inspection.Assembly.AllTypeNames);
        Assert.Equal(2, inspection.InspectedAssemblyCount);
        Assert.Equal(1, inspection.RuntimePluginTypeCount);
    }

    [Fact]
    public void Extract_RuntimePluginThroughExternalDerivedInterface_DetectsConcreteType()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "external-interface.nupkg",
            "ppds_ExternalInterface",
            new TestPackageAssembly(
                "Contoso.PluginContracts",
                "Contoso.PluginContracts.dll",
                """
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Contracts
                {
                    public interface IContosoPlugin : IPlugin { }
                }
                """,
                ReferencesSdk: true),
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                """
                using System;
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin : Contoso.Contracts.IContosoPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true,
                AssemblyReferences: ["Contoso.PluginContracts"]));

        var inspection = NupkgExtractor.Inspect(nupkgPath);

        Assert.Equal("Contoso.RuntimePlugins", inspection.Assembly.Name);
        Assert.Equal(["Contoso.Plugins.RuntimeOnlyPlugin"], inspection.Assembly.AllTypeNames);
        Assert.Equal(2, inspection.InspectedAssemblyCount);
        Assert.Equal(1, inspection.RuntimePluginTypeCount);
    }

    [Fact]
    public void Extract_UnresolvedExternalBase_ExplainsReferenceDirRecovery()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "missing-external-base.nupkg",
            "ppds_MissingExternalBase",
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
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin : Contoso.External.PluginBase { }
                }
                """,
                ReferencesSdk: true,
                AssemblyReferences: ["Contoso.ExternalFramework"]));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Operation.Dependency, exception.ErrorCode);
        Assert.Contains("Contoso.External.PluginBase", exception.Message);
        Assert.Contains("--reference-dir", exception.Message);
    }

    [Fact]
    public void Extract_UnresolvedExternalGenericBase_ExplainsReferenceDirRecovery()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "missing-external-generic-base.nupkg",
            "ppds_MissingExternalGenericBase",
            new TestPackageAssembly(
                "Contoso.ExternalFramework",
                "Contoso.ExternalFramework.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.External
                {
                    public abstract class PluginBase<T> : IPlugin
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
                namespace Contoso.Plugins
                {
                    public sealed class RuntimeOnlyPlugin :
                        Contoso.External.PluginBase<string> { }
                }
                """,
                ReferencesSdk: true,
                AssemblyReferences: ["Contoso.ExternalFramework"]));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Operation.Dependency, exception.ErrorCode);
        Assert.Contains("Contoso.External.PluginBase", exception.Message);
        Assert.Contains("--reference-dir", exception.Message);
    }

    [Fact]
    public void Extract_RuntimePluginAndDependency_SelectsOnlyPluginAssembly()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "package-with-dependency.nupkg",
            "ppds_RuntimePackage",
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
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
                ReferencesSdk: true),
            new TestPackageAssembly(
                "Contoso.Dependency",
                "Contoso.Dependency.dll",
                "namespace Contoso.Dependency { public sealed class Helper { } }"));

        var inspection = NupkgExtractor.Inspect(nupkgPath);

        Assert.Equal("Contoso.RuntimePlugins", inspection.Assembly.Name);
        Assert.Equal(2, inspection.InspectedAssemblyCount);
        Assert.Equal(0, inspection.AnnotatedTypeCount);
        Assert.Equal(1, inspection.RuntimePluginTypeCount);
    }

    [Fact]
    public void Extract_MultiplePlausiblePluginAssemblies_RejectsWithCandidateNames()
    {
        static TestPackageAssembly RuntimePlugin(string assemblyName, string className) => new(
            assemblyName,
            $"{assemblyName}.dll",
            $$"""
            using System;
            using Microsoft.Xrm.Sdk;
            public sealed class {{className}} : IPlugin
            {
                public void Execute(IServiceProvider serviceProvider) { }
            }
            """,
            ReferencesSdk: true);

        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "ambiguous.nupkg",
            "ppds_Ambiguous",
            RuntimePlugin("Contoso.FirstPlugins", "FirstPlugin"),
            RuntimePlugin("Contoso.SecondPlugins", "SecondPlugin"));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyAmbiguous, exception.ErrorCode);
        Assert.Contains("Contoso.FirstPlugins", exception.Message);
        Assert.Contains("Contoso.SecondPlugins", exception.Message);
    }

    [Fact]
    public void Extract_LoadablePackageWithoutPluginAssembly_RejectsWithInspectionCounts()
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "not-a-plugin.nupkg",
            "ppds_NotAPlugin",
            new TestPackageAssembly(
                "Contoso.Dependency",
                "Contoso.Dependency.dll",
                "namespace Contoso.Dependency { public sealed class Helper { } }"));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("Inspected 1 loadable assembly", exception.Message);
        Assert.Contains("0 PPDS-annotated types", exception.Message);
        Assert.Contains("0 runtime IPlugin types", exception.Message);
        Assert.Contains("Contoso.Dependency", exception.Message);
    }

    [Fact]
    public void Inspect_BufferedSnapshotDoesNotReopenReplacedPackagePath()
    {
        var originalPath = PluginPackageTestFixture.Create(
            _scratch,
            "snapshot.nupkg",
            "ppds_SnapshotPackage",
            new TestPackageAssembly(
                "Contoso.SnapshotPlugins",
                "Contoso.SnapshotPlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                public sealed class SnapshotPlugin : IPlugin
                {
                    public void Execute(IServiceProvider serviceProvider) { }
                }
                """,
                ReferencesSdk: true));
        var snapshot = File.ReadAllBytes(originalPath);
        var replacementPath = PluginPackageTestFixture.Create(
            _scratch,
            "replacement.nupkg",
            "ppds_ReplacementPackage",
            new TestPackageAssembly(
                "Contoso.Replacement",
                "Contoso.Replacement.dll",
                "public sealed class Replacement { }"));
        File.Copy(replacementPath, originalPath, overwrite: true);

        var inspection = NupkgExtractor.Inspect(snapshot, originalPath);

        Assert.Equal("Contoso.SnapshotPlugins", inspection.Assembly.Name);
        Assert.Equal("snapshot.nupkg", inspection.Assembly.PackagePath);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshot)).ToLowerInvariant(),
            inspection.Assembly.PackageContentSha256);
    }

    [Theory]
    [InlineData("PluginStepAttribute")]
    [InlineData("CustomApiAttribute")]
    public void Extract_LookalikeRegistrationAttribute_DoesNotQualifyAssembly(string attributeName)
    {
        var fakeAttributeSource = $$"""
            namespace PPDS.Plugins
            {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
                public sealed class {{attributeName}} : System.Attribute { }
            }
            """;
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            $"lookalike-{attributeName}.nupkg",
            "ppds_LookalikeAttributes",
            new TestPackageAssembly(
                "Contoso.FakeAttributes",
                "Contoso.FakeAttributes.dll",
                fakeAttributeSource),
            new TestPackageAssembly(
                "Contoso.LookalikeHandler",
                "Contoso.LookalikeHandler.dll",
                $$"""
                [PPDS.Plugins.{{attributeName}}]
                public sealed class LookalikeHandler { }
                """,
                AssemblyReferences: ["Contoso.FakeAttributes"]));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("0 PPDS-annotated types", exception.Message);
        Assert.Contains("0 runtime IPlugin types", exception.Message);
    }

    [Fact]
    public void Extract_SameNameWrongTokenPpdsAttribute_DoesNotQualifyAssembly()
    {
        var sdkPublicKey = System.Reflection.AssemblyName
            .GetAssemblyName(Path.Combine(
                AppContext.BaseDirectory,
                "TestAssets",
                "Microsoft.Xrm.Sdk.net462.dll"))
            .GetPublicKey();
        Assert.NotNull(sdkPublicKey);
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "wrong-token-ppds-attribute.nupkg",
            "ppds_WrongTokenAttributes",
            new TestPackageAssembly(
                "PPDS.Plugins",
                "PPDS.Plugins.dll",
                """
                namespace PPDS.Plugins
                {
                    public sealed class PluginStepAttribute : System.Attribute { }
                }
                """,
                StrongNamePublicKey: sdkPublicKey),
            new TestPackageAssembly(
                "Contoso.WrongTokenHandler",
                "Contoso.WrongTokenHandler.dll",
                "[PPDS.Plugins.PluginStep] public sealed class WrongTokenHandler { }",
                AssemblyReferences: ["PPDS.Plugins"]));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Plugin.PackageAssemblyNotFound, exception.ErrorCode);
        Assert.Contains("0 PPDS-annotated types", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_WrongTokenSameNameInheritanceDependency_DoesNotProvePlugin(bool derivedInterface)
    {
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
        var pluginSource = derivedInterface
            ? "namespace Contoso { public sealed class RuntimePlugin : Contoso.Framework.IDerivedPlugin { public void Execute(System.IServiceProvider serviceProvider) { } } }"
            : "namespace Contoso { public sealed class RuntimePlugin : Contoso.Framework.PluginBase { } }";
        var runtimePluginAssembly = derivedInterface
            ? new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                string.Empty,
                PrecompiledImage: PluginPackageTestFixture.CreateExternalDerivedInterfaceConsumerImage(
                    "Contoso.IdentityFramework",
                    correctPublicKey))
            : new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                pluginSource,
                ReferencesSdk: true,
                AssemblyReferences: ["Contoso.IdentityFramework"]);
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            $"wrong-token-inheritance-{derivedInterface}.nupkg",
            "ppds_WrongTokenInheritance",
            new TestPackageAssembly(
                "Contoso.KnownPlugins",
                "Contoso.KnownPlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                namespace Contoso.Known
                {
                    public sealed class KnownPlugin : IPlugin
                    {
                        public void Execute(IServiceProvider serviceProvider) { }
                    }
                }
                """,
                ReferencesSdk: true),
            new TestPackageAssembly(
                "Contoso.IdentityFramework",
                "correct-reference.dll",
                correctFrameworkSource,
                ReferencesSdk: true,
                IncludeInPackage: false,
                StrongNamePublicKey: correctPublicKey),
            runtimePluginAssembly,
            new TestPackageAssembly(
                "Contoso.IdentityFramework",
                "Contoso.IdentityFramework.dll",
                wrongFrameworkSource,
                StrongNamePublicKey: wrongPublicKey));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Operation.Dependency, exception.ErrorCode);
        Assert.Contains("Contoso.RuntimePlugins", exception.Message);
        Assert.Contains("--reference-dir", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_OfficialRegistrationOnNonPluginHandler_RejectsPackage(bool customApi)
    {
        var annotation = customApi
            ? "[CustomApi(UniqueName = \"ppds_Invalid\", DisplayName = \"Invalid\")]"
            : "[PluginStep(Message = \"Create\", EntityLogicalName = \"account\", Stage = PluginStage.PreOperation)]";
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            $"non-plugin-handler-{customApi}.nupkg",
            "ppds_NonPluginHandler",
            new TestPackageAssembly(
                "Contoso.NonPluginHandler",
                "Contoso.NonPluginHandler.dll",
                $$"""
                using PPDS.Plugins;
                {{annotation}}
                public sealed class NonPluginHandler { }
                """,
                ReferencesPpdsPlugins: true));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Validation.InvalidValue, exception.ErrorCode);
        Assert.Contains("public, concrete, closed runtime", exception.Message);
    }

    [Theory]
    [InlineData(TestPluginTypeShape.Concrete, "PluginStepAttribute")]
    [InlineData(TestPluginTypeShape.Abstract, "PluginStepAttribute")]
    [InlineData(TestPluginTypeShape.Interface, "PluginStepAttribute")]
    [InlineData(TestPluginTypeShape.OpenGeneric, "PluginStepAttribute")]
    [InlineData(TestPluginTypeShape.Static, "PluginStepAttribute")]
    [InlineData(TestPluginTypeShape.Concrete, "CustomApiAttribute")]
    [InlineData(TestPluginTypeShape.Abstract, "CustomApiAttribute")]
    [InlineData(TestPluginTypeShape.Interface, "CustomApiAttribute")]
    [InlineData(TestPluginTypeShape.OpenGeneric, "CustomApiAttribute")]
    [InlineData(TestPluginTypeShape.Static, "CustomApiAttribute")]
    public void Extract_MixedValidPluginAndInvalidOfficialHandler_RejectsPackage(
        TestPluginTypeShape shape,
        string attributeName)
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            $"mixed-invalid-{shape}-{attributeName}.nupkg",
            "ppds_MixedInvalidHandler",
            new TestPackageAssembly(
                "Contoso.RuntimePlugins",
                "Contoso.RuntimePlugins.dll",
                string.Empty,
                PrecompiledImage: PluginPackageTestFixture.CreateMixedPluginImage(
                    shape,
                    secondaryImplementsPlugin: shape != TestPluginTypeShape.Concrete,
                    officialAttributeName: attributeName)));

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Validation.InvalidValue, exception.ErrorCode);
        Assert.Contains("Contoso.Plugins.ConfiguredPlugin", exception.Message);
        Assert.Contains("public, concrete, closed runtime", exception.Message);
    }

    [Theory]
    [InlineData("Contoso.ExternalFramework")]
    [InlineData("System.ContosoPluginFramework")]
    public void Extract_MixedPackageWithUnresolvedCandidateAncestry_FailsClosed(
        string missingAssemblyName)
    {
        var nupkgPath = PluginPackageTestFixture.Create(
            _scratch,
            "mixed-unresolved-candidate.nupkg",
            "ppds_MixedUnresolvedCandidate",
            new TestPackageAssembly(
                "Contoso.KnownPlugins",
                "Contoso.KnownPlugins.dll",
                """
                using System;
                using Microsoft.Xrm.Sdk;
                public sealed class KnownPlugin : IPlugin
                {
                    public void Execute(IServiceProvider serviceProvider) { }
                }
                """,
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

        var exception = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Operation.Dependency, exception.ErrorCode);
        Assert.Contains("Contoso.UnresolvedCandidate.dll", exception.Message);
        Assert.Contains("--reference-dir", exception.Message);
    }

    private static void CreatePluginPackage(
        string nupkgPath,
        string framework,
        string? emptyFramework = null)
    {
        var pluginAssembly = CompilePluginAssembly();
        var pluginsAssemblyPath = typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location;

        using var stream = File.Create(nupkgPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var nuspec = archive.CreateEntry("plugin.nuspec");
        using (var writer = new StreamWriter(nuspec.Open(), Encoding.UTF8))
        {
            writer.Write("""
                <?xml version="1.0"?>
                <package><metadata><id>plugin</id><version>1.0.0</version></metadata></package>
                """);
        }

        var pluginEntry = archive.CreateEntry($"lib/{framework}/Net48Plugin.dll");
        using (var entryStream = pluginEntry.Open())
        {
            entryStream.Write(pluginAssembly);
        }

        var attributesEntry = archive.CreateEntry($"lib/{framework}/PPDS.Plugins.dll");
        using (var attributesStream = attributesEntry.Open())
        using (var attributesFile = File.OpenRead(pluginsAssemblyPath))
        {
            attributesFile.CopyTo(attributesStream);
        }

        if (emptyFramework != null)
        {
            var marker = archive.CreateEntry($"lib/{emptyFramework}/_._");
            marker.Open().Dispose();
        }
    }

    private static byte[] CompilePluginAssembly()
    {
        var referenceDirectory = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory();
        Assert.NotNull(referenceDirectory);

        var syntaxTree = CSharpSyntaxTree.ParseText("""
            using System;
            using Microsoft.Xrm.Sdk;
            using PPDS.Plugins;

            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PreOperation)]
            public class Net48Plugin : IPlugin
            {
                public void Execute(IServiceProvider serviceProvider) { }
            }
            """);

        var compilation = CSharpCompilation.Create(
            "Net48Plugin",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(Path.Combine(referenceDirectory!, "mscorlib.dll")),
                MetadataReference.CreateFromFile(Path.Combine(
                    AppContext.BaseDirectory,
                    "TestAssets",
                    "Microsoft.Xrm.Sdk.net462.dll")),
                MetadataReference.CreateFromFile(typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location)
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        return output.ToArray();
    }
}
