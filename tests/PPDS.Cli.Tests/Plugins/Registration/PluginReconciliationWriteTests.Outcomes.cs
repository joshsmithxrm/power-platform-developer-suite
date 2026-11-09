using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

public partial class PluginReconciliationWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFollowOn_RetainsAcknowledgedRowAndUnknownPhase(bool create)
    {
        var failure = new InvalidOperationException("DO-NOT-ECHO");
        var rowId = create ? Guid.NewGuid() : _existing.Id;
        _client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>())).ReturnsAsync(rowId);
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpsertStepAsync(
            Guid.NewGuid(), "pluginType", Step(), Guid.NewGuid(), null, "solution", new StepIdentityResolution(create ? null : rowId)));
        Assert.Same(failure, error);
        var outcomes = PluginMutationJournal.Read(error);
        Assert.Contains(outcomes, o => o.Phase == "row" && o.RecordId == rowId && o.Outcome == "acknowledged" && o.Operation == (create ? "create" : "update"));
        Assert.Contains(outcomes, o => o.Phase == "solution" && o.Outcome == "unknown");
        Assert.DoesNotContain("DO-NOT-ECHO", JsonSerializer.Serialize(outcomes));
    }

    [Fact]
    public async Task SecureWriteThenRowFailure_RetainsSecureAcknowledgementWithoutPlaintextOrFalseRowCount()
    {
        var secretId = Guid.NewGuid();
        _existing["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", secretId);
        _client.Setup(c => c.UpdateAsync(It.Is<Entity>(e => e.LogicalName == "sdkmessageprocessingstep"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DO-NOT-ECHO"));
        var step = Step(); step.SecureConfiguration = "PRIVATE-SECRET";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", step, Guid.NewGuid(), null));
        var outcomes = PluginMutationJournal.Read(error);
        Assert.Contains(outcomes, o => o.Phase == "secureConfiguration" && o.RecordId == secretId && o.Outcome == "acknowledged");
        Assert.Contains(outcomes, o => o.Phase == "row" && o.Outcome == "unknown");
        Assert.DoesNotContain(outcomes, o => o.Phase == "row" && o.Outcome == "acknowledged");
        Assert.DoesNotContain("PRIVATE-SECRET", JsonSerializer.Serialize(outcomes));
    }

    [Fact]
    public async Task StateCancellation_PreservesOriginalExceptionAndAcknowledgedRow()
    {
        var step = Step(); step.Enabled = false;
        var cancellation = new OperationCanceledException("private diagnostic");
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(cancellation);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", step, Guid.NewGuid(), null));
        Assert.Same(cancellation, error);
        Assert.Contains(PluginMutationJournal.Read(error), o => o.Phase == "row" && o.Outcome == "acknowledged");
        Assert.Contains(PluginMutationJournal.Read(error), o => o.Phase == "state" && o.Outcome == "unknown");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployFollowOnFailure_CountsAcknowledgedRow_AndCancellationRetainsPreviousAssembly(bool cancel)
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"ppds-outcomes-{Guid.NewGuid():N}.dll");
        var configFile = artifact + ".json";
        File.WriteAllBytes(artifact, [0x4d, 0x5a]);
        File.WriteAllBytes(artifact + ".first.dll", [0x4d, 0x5a]);
        File.WriteAllBytes(artifact + ".last.dll", [0x4d, 0x5a]);
        try
        {
            Exception failure = cancel ? new OperationCanceledException("PRIVATE-FAILURE") : new InvalidOperationException("PRIVATE-FAILURE");
            _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            var registration = new Mock<IPluginRegistrationService>();
            var typeId = Guid.NewGuid();
            registration.Setup(s => s.UpsertAssemblyAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
            registration.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([new() { Id = typeId, TypeName = "Handler" }]);
            registration.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync([new() { Id = _existing.Id, Name = "research", Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Asynchronous" }]);
            registration.Setup(s => s.UpsertPluginTypeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(typeId);
            registration.Setup(s => s.GetSdkMessageIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
            registration.Setup(s => s.GetSdkMessageFilterIdAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
            registration.Setup(s => s.UpsertStepAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PluginStepConfig>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<StepIdentityResolution?>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, string kind, PluginStepConfig cfg, Guid message, Guid? filter, string? solution, StepIdentityResolution? identity, CancellationToken ct) => _service.UpsertStepAsync(id, kind, cfg, message, filter, solution, identity, ct));
            var second = new PluginAssemblyConfig { Name = "Second", Type = "Assembly", Path = artifact, Solution = "solution", Types = [new() { TypeName = "Handler", Steps = [Step()] }] };
            if (!cancel)
            {
                var result = await DeployCommand.DeployAssemblyAsync(registration.Object, second, Path.GetTempPath(), null, false, false, new() { OutputFormat = OutputFormat.Json }, CancellationToken.None);
                Assert.False(result.Success); Assert.True(result.Partial); Assert.Equal(1, result.StepsUpdated);
                Assert.Contains(result.Outcomes, o => o.Phase == "solution" && o.Outcome == "unknown");
                Assert.DoesNotContain("PRIVATE-FAILURE", JsonSerializer.Serialize(result));
                return;
            }
            File.WriteAllText(configFile, JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [
                new() { Name = "First", Type = "Assembly", Path = artifact + ".first.dll" }, second,
                new() { Name = "Never", Type = "Assembly", Path = artifact + ".last.dll" }] }));
            using var output = new StringWriter();
            var exit = await DeployCommand.ExecuteAsync(new(configFile), null, null, null, false, false,
                new() { OutputFormat = OutputFormat.Json }, CancellationToken.None,
                _ => Task.FromResult(new ServiceCollection().AddSingleton(registration.Object).AddSingleton(Mock.Of<ICustomApiService>()).BuildServiceProvider()),
                outputWriter: new JsonOutputWriter(output));
            Assert.True(exit == ExitCodes.Failure, output.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            var data = json.RootElement.GetProperty("data");
            Assert.True(data.GetArrayLength() == 2, output.ToString());
            Assert.Equal("completed", data[0].GetProperty("status").GetString());
            Assert.Equal("cancelled", data[1].GetProperty("status").GetString());
            Assert.True(data[1].GetProperty("partial").GetBoolean());
            Assert.Equal(1, data[1].GetProperty("stepsUpdated").GetInt32());
            Assert.DoesNotContain("PRIVATE-FAILURE", output.ToString());
            registration.Verify(s => s.UpsertAssemblyAsync("Never", It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { File.Delete(artifact); File.Delete(artifact + ".first.dll"); File.Delete(artifact + ".last.dll"); File.Delete(configFile); }
    }
}
