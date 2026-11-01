using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Flows;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Flows;

[Collection(nameof(ConsoleCaptureCollection))]
public sealed class StateCommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--all")]
    [InlineData("--all --solution test --name test_flow")]
    [InlineData("--id invalid")]
    [InlineData("--name test_flow --timeout 0")]
    [InlineData("--name test_flow --timeout 3601")]
    [InlineData("--name test_flow --max-flows 0")]
    [InlineData("--name test_flow --expected-plan-hash invalid")]
    public void InvalidSelectionFailsParsingBeforeAuthentication(string arguments)
    {
        Assert.NotEmpty(StateCommand.Create(true).Parse(arguments).Errors);
    }

    [Theory]
    [InlineData("--all --solution test --dry-run")]
    [InlineData("--name test_flow --solution test --confirm-env https://org.crm.dynamics.com")]
    [InlineData("--id 00000000-0000-0000-0000-000000000001 --dry-run")]
    public void BoundedSelectionParses(string arguments)
    {
        Assert.Empty(StateCommand.Create(true).Parse(arguments).Errors);
        Assert.Empty(StateCommand.Create(false).Parse(arguments).Errors);
    }

    [Fact]
    public void PartialResultRetainsDataAndNonzeroExit()
    {
        var result = new FlowActivationResult
        {
            EnvironmentUrl = "https://test.crm.dynamics.com", PlanHash = "hash", Excluded = [],
            Flows = [
                new() { Id = Guid.NewGuid(), UniqueName = "a", InitialState = FlowState.Draft,
                    RequestedState = FlowState.Activated, Status = "succeeded" },
                new() { Id = Guid.NewGuid(), UniqueName = "b", InitialState = FlowState.Draft,
                    RequestedState = FlowState.Activated, Status = "unknown" }]
        };
        var output = StateCommand.ToCommandResult(result);
        Assert.False(output.Success);
        Assert.Same(result, output.Data);
        Assert.NotNull(output.Error);
        Assert.Equal(ExitCodes.PartialSuccess, StateCommand.GetExitCode(result));
    }

    [Theory]
    [InlineData(true, true, "Json")]
    [InlineData(false, false, "Json")]
    [InlineData(true, false, "Text")]
    public async Task InvocationRoutesTargetAndOptionsAndKeepsProgressOffStdout(bool enable, bool dryRun, string format)
    {
        var target = enable ? FlowState.Activated : FlowState.Draft;
        var service = new Mock<IFlowActivationService>(MockBehavior.Strict);
        FlowActivationRequest? captured = null;
        var result = new FlowActivationResult
        {
            EnvironmentUrl = "https://fixture.crm.dynamics.com", PlanHash = "verified-plan", DryRun = dryRun,
            RequestedState = target, Excluded = [],
            Flows = [new() { Id = Guid.NewGuid(), UniqueName = "test_flow", InitialState = FlowState.Draft,
                RequestedState = target, Status = dryRun ? "would-change" : "unknown", Phase = "verification" }]
        };
        Task<FlowActivationResult> Run(FlowActivationRequest request, IProgressReporter? progress, CancellationToken ct)
        {
            captured = request;
            progress!.ReportPhase("fixture-progress", "verifying");
            return Task.FromResult(result);
        }
        if (dryRun)
            service.Setup(s => s.PlanAsync(It.IsAny<FlowActivationRequest>(), It.IsAny<IProgressReporter>(), It.IsAny<CancellationToken>()))
                .Returns((FlowActivationRequest r, IProgressReporter? p, CancellationToken c) => Run(r, p, c));
        else
            service.Setup(s => s.ExecuteAsync(It.IsAny<FlowActivationRequest>(), It.IsAny<IProgressReporter>(), It.IsAny<CancellationToken>()))
                .Returns((FlowActivationRequest r, IProgressReporter? p, CancellationToken c) => Run(r, p, c));
        var command = StateCommand.Create(enable, (profile, environment, options, ct) =>
        {
            Assert.Equal("fixture-profile", profile);
            Assert.Equal(result.EnvironmentUrl, environment);
            Assert.Equal(format == "Json", options.IsJsonMode);
            return Task.FromResult(new ServiceCollection().AddSingleton(service.Object)
                .AddSingleton(new ResolvedConnectionInfo { EnvironmentUrl = result.EnvironmentUrl, Profile = new AuthProfile() })
                .BuildServiceProvider());
        });
        var arguments = $"--name test_flow --solution fixture --profile fixture-profile --environment {result.EnvironmentUrl} " +
            $"--timeout 17 --max-flows 3 --confirm-env {result.EnvironmentUrl} --expected-plan-hash {new string('a', 64)} --output-format {format}" +
            (dryRun ? " --dry-run" : "");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var parsed = command.Parse(arguments);
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            Assert.Equal(dryRun ? ExitCodes.Success : ExitCodes.Failure, exit);
            Assert.NotNull(captured);
            Assert.Equal(target, captured.TargetState);
            Assert.Equal(TimeSpan.FromSeconds(17), captured.VerificationTimeout);
            Assert.Equal(3, captured.MaxFlows);
            Assert.Equal(result.EnvironmentUrl, captured.ConfirmEnvironment);
            Assert.Equal(new string('a', 64), captured.ExpectedPlanHash);
            Assert.Equal("fixture", captured.Solution);
            Assert.Contains("fixture-progress", stderr.ToString());
            Assert.DoesNotContain("fixture-progress", stdout.ToString());
            if (format == "Json")
            {
                using var json = JsonDocument.Parse(stdout.ToString());
                Assert.Equal(dryRun, json.RootElement.GetProperty("success").GetBoolean());
                var data = json.RootElement.GetProperty("data");
                Assert.Equal(result.EnvironmentUrl, data.GetProperty("environmentUrl").GetString());
                Assert.Equal(dryRun ? "would-change" : "unknown", data.GetProperty("flows")[0].GetProperty("status").GetString());
                Assert.Equal("verified-plan", data.GetProperty("planHash").GetString());
            }
            else
            {
                Assert.Contains("test_flow: unknown", stdout.ToString());
                Assert.Contains("Warning:", stderr.ToString());
            }
            service.VerifyAll();
            service.VerifyNoOtherCalls();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task ProviderFailureProducesStructuredErrorAndNonzeroExit()
    {
        var command = StateCommand.Create(true, (_, _, _, _) => throw new PpdsException(
            "Environment.ConfirmationRequired", "Confirm the resolved target."));
        var previousOut = Console.Out;
        using var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            var exit = await command.Parse("--name test_flow --output-format Json").InvokeAsync();
            Assert.NotEqual(ExitCodes.Success, exit);
            using var json = JsonDocument.Parse(stdout.ToString());
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Environment.ConfirmationRequired", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { Console.SetOut(previousOut); }
    }
}
