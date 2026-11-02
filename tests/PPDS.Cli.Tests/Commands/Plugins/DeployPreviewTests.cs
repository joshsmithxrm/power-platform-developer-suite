using System.Text.Json;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Plugins;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Collection(nameof(ConsoleCaptureCollection))]
public sealed class DeployPreviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-71")]
    public async Task DryRun_ExistingAndNewImages_ReportsAccurateCountsWithoutWrites(bool package)
    {
        using var fixture = new PreviewFixture(package, existingType: true, existingStep: true);
        fixture.Service.Setup(s => s.ListImagesForStepAsync(fixture.StepId, CancellationToken.None))
            .ReturnsAsync([new PluginImageInfo { Id = Guid.NewGuid(), Name = "Before" }]);

        var result = await fixture.RunAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.StepsCreated);
        Assert.Equal(1, result.StepsUpdated);
        Assert.Equal(2, result.ImagesCreated);
        Assert.Equal(1, result.ImagesUpdated);
        Assert.Equal(0, result.StepsDeleted + result.ImagesDeleted);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(2, json.RootElement.GetProperty("imagesCreated").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("imagesUpdated").GetInt32());
        fixture.VerifyReadsOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-71")]
    public async Task DryRun_NewSteps_DoesNotReadImagesForPlaceholderIds(bool package)
    {
        using var fixture = new PreviewFixture(package, existingType: true, existingStep: false);

        var result = await fixture.RunAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.StepsCreated);
        Assert.Equal(0, result.StepsUpdated);
        Assert.Equal(3, result.ImagesCreated);
        Assert.Equal(0, result.ImagesUpdated);
        fixture.Service.Verify(s => s.ListImagesForStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.VerifyReadsOnly();
    }

    [Theory]
    [InlineData(false, "register")]
    [InlineData(true, "reuse")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-72")]
    public async Task DryRun_TypePreview_DistinguishesNewAndExistingTypes(bool existingType, string action)
    {
        using var fixture = new PreviewFixture(package: false, existingType, existingStep: false);
        using var error = new StringWriter();
        var previousError = Console.Error;
        try
        {
            Console.SetError(error);
            var result = await fixture.RunAsync(json: false);
            Assert.True(result.Success, result.Error);
            Assert.Contains($"Would {action} type: Contoso.Plugins.Handler", error.ToString());
            Assert.Equal(2, result.StepsCreated);
            Assert.Equal(3, result.ImagesCreated);
            fixture.VerifyReadsOnly();
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-71")]
    public async Task DryRun_ImageLookupFailure_DoesNotReportSuccessfulPlan()
    {
        using var fixture = new PreviewFixture(package: false, existingType: true, existingStep: true);
        fixture.Service.Setup(s => s.ListImagesForStepAsync(fixture.StepId, CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("Image inventory unavailable"));

        var result = await fixture.RunAsync();

        Assert.False(result.Success);
        Assert.Contains("Image inventory unavailable", result.Error);
        Assert.Equal(0, result.ImagesCreated + result.ImagesUpdated);
        fixture.VerifyReadsOnly();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-71")]
    public async Task DryRun_ImageLookupCancellation_StopsPreview()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new PreviewFixture(package: false, existingType: true, existingStep: true, cancellation.Token);
        fixture.Service.Setup(s => s.ListImagesForStepAsync(fixture.StepId, cancellation.Token))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<List<PluginImageInfo>>(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        fixture.Service.Verify(s => s.GetSdkMessageIdAsync("Create", It.IsAny<CancellationToken>()), Times.Never);
        fixture.VerifyReadsOnly();
    }

    private sealed class PreviewFixture : IDisposable
    {
        private readonly string _artifact;
        private readonly CancellationToken _token;
        private readonly PluginAssemblyConfig _config;
        public Mock<IPluginRegistrationService> Service { get; } = new(MockBehavior.Strict);
        public Guid StepId { get; } = Guid.NewGuid();

        public PreviewFixture(bool package, bool existingType, bool existingStep, CancellationToken token = default)
        {
            _token = token;
            var assemblyId = Guid.NewGuid();
            var typeId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            if (package)
            {
                _artifact = PluginPackageTestFixture.Create(
                    Path.GetTempPath(), $"preview-{Guid.NewGuid():N}.nupkg", "ppds_Preview",
                    new TestPackageAssembly("Contoso.RuntimePlugins", "handler.dll", """
                        using System;
                        using Microsoft.Xrm.Sdk;
                        namespace Contoso.Plugins
                        {
                            public sealed class Handler : IPlugin
                            {
                                public void Execute(IServiceProvider serviceProvider) { }
                            }
                        }
                        """, ReferencesSdk: true));
                var packageId = Guid.NewGuid();
                Service.Setup(s => s.GetPackageByNameAsync("ppds_Preview", token))
                    .ReturnsAsync(new PluginPackageInfo { Id = packageId, Name = "ppds_Preview" });
                Service.Setup(s => s.GetAssemblyIdForPackageAsync(packageId, "Contoso.RuntimePlugins", token))
                    .ReturnsAsync(assemblyId);
            }
            else
            {
                _artifact = Path.Combine(Path.GetTempPath(), $"preview-{Guid.NewGuid():N}.dll");
                File.WriteAllBytes(_artifact, [0x4d, 0x5a]);
                Service.Setup(s => s.GetAssemblyByNameAsync("Contoso.RuntimePlugins", token))
                    .ReturnsAsync(new PluginAssemblyInfo { Id = assemblyId, Name = "Contoso.RuntimePlugins" });
            }

            Service.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, token))
                .ReturnsAsync(existingType ? [new PluginTypeInfo { Id = typeId, TypeName = "Contoso.Plugins.Handler" }] : []);
            if (existingType)
                Service.Setup(s => s.ListStepsForTypeAsync(typeId, null, token))
                    .ReturnsAsync(existingStep ? [new PluginStepInfo
                    {
                        Id = StepId, Name = "Update account", Message = "Update", PrimaryEntity = "account",
                        Stage = "PostOperation", Mode = "Synchronous"
                    }] : []);
            foreach (var message in new[] { "Update", "Create" })
                Service.Setup(s => s.GetSdkMessageIdAsync(message, token)).ReturnsAsync(messageId);
            Service.Setup(s => s.GetSdkMessageFilterIdAsync(messageId, "account", null, token))
                .ReturnsAsync(Guid.NewGuid());
            _config = new PluginAssemblyConfig
            {
                Name = "Contoso.RuntimePlugins", Type = package ? "Nuget" : "Assembly",
                PackagePath = package ? _artifact : null, Path = package ? null : _artifact,
                Types = [new PluginTypeConfig
                {
                    TypeName = "Contoso.Plugins.Handler",
                    Steps =
                    [
                        new PluginStepConfig
                        {
                            Name = "Update account", Message = "Update", Entity = "account",
                            Stage = "PostOperation", Mode = "Synchronous",
                            Images = [new() { Name = "Before", ImageType = "PreImage" }, new() { Name = "After", ImageType = "PostImage" }]
                        },
                        new PluginStepConfig
                        {
                            Name = "Create account", Message = "Create", Entity = "account",
                            Stage = "PostOperation", Mode = "Synchronous",
                            Images = [new() { Name = "Created", ImageType = "PostImage" }]
                        }
                    ]
                }]
            };
        }

        public Task<DeployCommand.DeploymentResult> RunAsync(bool json = true) =>
            DeployCommand.DeployAssemblyAsync(Service.Object, _config, Path.GetTempPath(), null,
                clean: false, dryRun: true, new GlobalOptionValues
                {
                    OutputFormat = json ? OutputFormat.Json : OutputFormat.Text
                }, _token);

        public void VerifyReadsOnly()
        {
            Assert.All(Service.Invocations, call => Assert.True(
                call.Method.Name.StartsWith("Get", StringComparison.Ordinal) ||
                call.Method.Name.StartsWith("List", StringComparison.Ordinal), call.Method.Name));
        }

        public void Dispose() => File.Delete(_artifact);
    }
}
