using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public sealed class InvocationSourceUpdateSurfaceTests
{
    [Theory]
    [InlineData(ErrorCodes.Plugin.UpdateNotPersisted)]
    [InlineData(ErrorCodes.Plugin.VerificationFailed)]
    public async Task CliUpdateStep_VerificationFailureIsNonSuccess(string errorCode)
    {
        var stepId = Guid.NewGuid();
        var safeMessage = errorCode == ErrorCodes.Plugin.UpdateNotPersisted
            ? $"Dataverse acknowledged the update to step '{stepId}', but the fresh invocationSource read returned 1 instead of 0. No retry or rollback was attempted."
            : $"Dataverse acknowledged the update to step '{stepId}', but invocationSource persistence could not be verified. No retry or rollback was attempted.";
        const string privateMarker = "PRIVATE-READ-FAILURE";
        using var cancellation = new CancellationTokenSource();
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.GetStepByNameOrIdAsync(stepId.ToString(), cancellation.Token))
            .ReturnsAsync(new PluginStepInfo { Id = stepId, Name = "offline-step" });
        registration.Setup(service => service.UpdateStepAsync(stepId,
                It.Is<StepUpdateRequest>(request => request.InvocationSource == "Parent"), cancellation.Token))
            .ThrowsAsync(new PpdsException(errorCode, safeMessage, new InvalidOperationException(privateMarker)));
        using var output = new StringWriter();
        var providerCalls = 0;

        var exit = await UpdateCommand.ExecuteStepUpdateAsync(
            stepId.ToString(), null, null, null, null, null, null, null, "Parent", null, null,
            new GlobalOptionValues { OutputFormat = OutputFormat.Json }, cancellation.Token,
            serviceProviderFactory: token =>
            {
                Assert.Equal(cancellation.Token, token);
                providerCalls++;
                return Task.FromResult(new ServiceCollection().AddSingleton(registration.Object).BuildServiceProvider());
            },
            outputWriter: new JsonOutputWriter(output));

        Assert.Equal(ExitCodes.Failure, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(errorCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(safeMessage, json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.False(json.RootElement.TryGetProperty("data", out _));
        Assert.DoesNotContain(privateMarker, output.ToString());
        Assert.Equal(1, providerCalls);
        registration.VerifyAll();
        Assert.Equal(2, registration.Invocations.Count); // One lookup and one attempted update; no retry.
    }

    [Fact]
    public async Task CliUpdateStep_CancelledVerificationReportsAcknowledgedUnverifiedFailure()
    {
        var stepId = Guid.NewGuid();
        const string safeMessage = "Invocation-source verification was cancelled after Dataverse acknowledged the step update; persistence remains unverified. No retry or rollback was attempted.";
        const string privateMarker = "PRIVATE-CANCELLATION-CAUSE";
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(safeMessage,
            new InvalidOperationException(privateMarker), cancellation.Token);
        var mutations = new PluginMutationJournal();
        await mutations.RunAsync("sdkmessageprocessingstep", "update", "row", stepId,
            () => Task.CompletedTask, cancellation.Token);
        mutations.Attach(failure);
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.GetStepByNameOrIdAsync(stepId.ToString(), cancellation.Token))
            .ReturnsAsync(new PluginStepInfo { Id = stepId, Name = "offline-step" });
        registration.Setup(service => service.UpdateStepAsync(stepId,
                It.Is<StepUpdateRequest>(request => request.InvocationSource == "Parent"), cancellation.Token))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(failure);
        using var output = new StringWriter();

        var exit = await UpdateCommand.ExecuteStepUpdateAsync(
            stepId.ToString(), null, null, null, null, null, null, null, "Parent", null, null,
            new GlobalOptionValues { OutputFormat = OutputFormat.Json }, cancellation.Token,
            serviceProviderFactory: _ => Task.FromResult(new ServiceCollection()
                .AddSingleton(registration.Object).BuildServiceProvider()),
            outputWriter: new JsonOutputWriter(output));

        Assert.Equal(ExitCodes.Failure, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(ErrorCodes.Operation.Cancelled, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(safeMessage, json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.False(json.RootElement.TryGetProperty("data", out _));
        Assert.DoesNotContain(privateMarker, output.ToString());
        Assert.Equal("acknowledged", Assert.Single(PluginMutationJournal.Read(failure)).Outcome);
        registration.VerifyAll();
        Assert.Equal(2, registration.Invocations.Count);
    }
}
