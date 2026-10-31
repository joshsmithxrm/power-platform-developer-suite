using System.Text.Json;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class PluginReleaseRegressionTests
{
    [Theory]
    [InlineData("account", null, "entity 'account'")]
    [InlineData("", "contact", "secondary entity 'contact'")]
    [InlineData("none", "contact", "secondary entity 'contact'")]
    [InlineData("account", "contact", "entity 'account' with secondary entity 'contact'")]
    public void MissingFilter_DescribesOnlySpecifiedEntities(string primary, string? secondary, string expected)
    {
        var message = PluginRegistrationService.DescribeMissingMessageFilter("Associate", primary, secondary);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("entity ''", message);
        Assert.DoesNotContain("entity 'none'", message);
    }

    [Fact]
    public async Task Deploy_DoubleFault_PreservesStepAndCleanupErrorsInJson()
    {
        var file = Path.Combine(Path.GetTempPath(), $"deploy-double-fault-{Guid.NewGuid():N}.dll");
        await File.WriteAllBytesAsync(file, [0x4d, 0x5a]);
        try
        {
            var typeId = Guid.NewGuid();
            var orphanId = Guid.NewGuid();
            var mock = new Mock<IPluginRegistrationService>();
            mock.Setup(s => s.UpsertAssemblyAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            mock.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new() { Id = typeId, TypeName = "Example.Handler" }]);
            mock.Setup(s => s.ListStepsForTypeAsync(typeId, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new() { Id = orphanId, Name = "old step", Message = "Delete", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous" }]);
            mock.Setup(s => s.UpsertPluginTypeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(typeId);
            mock.Setup(s => s.GetSdkMessageIdAsync("Create", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            mock.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), "acount", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid?)null);
            mock.Setup(s => s.DeleteStepAsync(orphanId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("cleanup rejected"));
            var config = new PluginAssemblyConfig
            {
                Name = "Example", Type = "Assembly", Path = file,
                Types = [new() { TypeName = "Example.Handler", Steps = [new() { Message = "Create", Entity = "acount", Stage = "PostOperation", Mode = "Synchronous" }] }]
            };

            var result = await DeployCommand.DeployAssemblyAsync(mock.Object, config, Path.GetTempPath(),
                null, clean: true, dryRun: false, new GlobalOptionValues { OutputFormat = OutputFormat.Json }, CancellationToken.None);

            Assert.False(result.Success);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            var error = json.RootElement.GetProperty("error").GetString()!;
            Assert.Contains("acount", error);
            Assert.Contains("No SDK message filter", error);
            Assert.Contains("cleanup rejected", error);
            mock.Verify(s => s.DeleteStepAsync(orphanId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Diff_PreCancelled_DoesNotLookUpAssembly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mock = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiffCommand.ComputeDriftAsync(mock.Object, new() { Name = "Example" }, cts.Token));
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Diff_CancellationDuringStepLookup_StopsOtherTypes()
    {
        using var cts = new CancellationTokenSource();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        var mock = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        mock.Setup(s => s.GetAssemblyByNameAsync("Example", cts.Token))
            .ReturnsAsync(new PluginAssemblyInfo { Id = assemblyId, Name = "Example" });
        mock.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, cts.Token))
            .ReturnsAsync([new() { Id = first, TypeName = "First" }, new() { Id = second, TypeName = "Second" }]);
        mock.Setup(s => s.ListStepsForTypeAsync(first, null, cts.Token))
            .Returns(() => { cts.Cancel(); return Task.FromCanceled<List<PluginStepInfo>>(cts.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiffCommand.ComputeDriftAsync(mock.Object, new() { Name = "Example" }, cts.Token));

        mock.Verify(s => s.ListStepsForTypeAsync(second, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Diff_PassesTokenToMessageFilterAndImageLookups()
    {
        using var cts = new CancellationTokenSource();
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var mock = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        mock.Setup(s => s.GetAssemblyByNameAsync("Example", cts.Token))
            .ReturnsAsync(new PluginAssemblyInfo { Id = assemblyId, Name = "Example" });
        mock.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, cts.Token))
            .ReturnsAsync([new() { Id = typeId, TypeName = "Handler" }]);
        mock.Setup(s => s.ListStepsForTypeAsync(typeId, null, cts.Token))
            .ReturnsAsync([new() { Id = stepId, Name = "existing", Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous" }]);
        mock.Setup(s => s.ListImagesForStepAsync(stepId, cts.Token)).ReturnsAsync([]);
        mock.Setup(s => s.GetSdkMessageIdAsync("Create", cts.Token)).ReturnsAsync(messageId);
        mock.Setup(s => s.GetSdkMessageFilterIdAsync(messageId, "account", null, cts.Token)).ReturnsAsync((Guid?)null);
        var config = new PluginAssemblyConfig
        {
            Name = "Example", Types = [new() { TypeName = "Handler", Steps = [
                new() { Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Synchronous" },
                new() { Message = "Create", Entity = "account", Stage = "PostOperation", Mode = "Synchronous" }] }]
        };

        await DiffCommand.ComputeDriftAsync(mock.Object, config, cts.Token);
        mock.VerifyAll();
    }
}
