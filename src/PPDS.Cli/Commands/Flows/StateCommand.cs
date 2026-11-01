using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Flows;

namespace PPDS.Cli.Commands.Flows;

/// <summary>Thin enable/disable adapters over the shared activation service.</summary>
public static class StateCommand
{
    /// <summary>Creates the enable or disable command.</summary>
    public static Command Create(bool enable) => Create(enable, null);

    internal static Command Create(bool enable,
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
    {
        var id = new Option<Guid?>("--id") { Description = "Exact workflow ID" };
        var name = new Option<string?>("--name") { Description = "Exact flow unique/schema name" };
        var all = new Option<bool>("--all") { Description = "All eligible cloud-flow definitions in --solution" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Inspect a fresh plan without writing" };
        var confirm = new Option<string?>("--confirm-env") { Description = "Acknowledge the exact resolved environment URL" };
        var timeout = new Option<int?>("--timeout") { Description = "State-verification deadline per flow in seconds (1-3600; default 60)" };
        var maxFlows = new Option<int?>("--max-flows") { Description = "Maximum eligible flow count (default 100); exceeding it fails without writes" };
        var expected = new Option<string?>("--expected-plan-hash") { Description = "Require this dry-run plan fingerprint before writes" };
        var command = new Command(enable ? "enable" : "disable",
            enable ? "Enable solution cloud flows with preflight and verified outcomes"
                : "Disable solution cloud flows (does not cancel existing runs)")
        {
            id, name, all, dryRun, confirm, timeout, maxFlows, expected,
            FlowsCommandGroup.SolutionOption, FlowsCommandGroup.ProfileOption, FlowsCommandGroup.EnvironmentOption
        };
        GlobalOptions.AddToCommand(command);

        FlowActivationRequest Request(System.CommandLine.Parsing.CommandResult result) => new()
        {
            Id = result.GetValue(id), Name = result.GetValue(name), All = result.GetValue(all),
            Solution = result.GetValue(FlowsCommandGroup.SolutionOption),
            TargetState = enable ? FlowState.Activated : FlowState.Draft,
            ConfirmEnvironment = result.GetValue(confirm),
            VerificationTimeout = TimeSpan.FromSeconds(result.GetValue(timeout) ?? 60),
            MaxFlows = result.GetValue(maxFlows) ?? 100, ExpectedPlanHash = result.GetValue(expected)
        };
        command.Validators.Add(result =>
        {
            try { FlowActivationService.ValidateRequest(Request(result)); }
            catch (PpdsException ex) { result.AddError(ex.UserMessage); }
            catch (InvalidOperationException ex) { result.AddError(ex.Message); }
        });
        command.SetAction(async (result, ct) => await ExecuteAsync(Request(result.CommandResult), result.GetValue(dryRun),
            result.GetValue(FlowsCommandGroup.ProfileOption), result.GetValue(FlowsCommandGroup.EnvironmentOption),
            GlobalOptions.GetValues(result), ct, providerFactory));
        return command;
    }

    private static async Task<int> ExecuteAsync(FlowActivationRequest request, bool dryRun,
        string? profile, string? environment, GlobalOptionValues options, CancellationToken ct,
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
    {
        var writer = ServiceFactory.CreateOutputWriter(options);
        try
        {
            await using var provider = providerFactory is null
                ? await ProfileServiceFactory.CreateFromProfilesAsync(
                profile, environment, options.Verbose, options.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback, ct)
                : await providerFactory(profile, environment, options, ct);
            var service = provider.GetRequiredService<IFlowActivationService>();
            if (!options.IsJsonMode)
                ConsoleHeader.WriteConnectedAs(provider.GetRequiredService<ResolvedConnectionInfo>());
            var progress = new CommandProgressReporter();
            var result = dryRun ? await service.PlanAsync(request, progress, ct)
                : await service.ExecuteAsync(request, progress, ct);
            if (options.IsJsonMode)
                writer.WriteResult(ToCommandResult(result));
            else
                WriteText(result);
            return GetExitCode(result);
        }
        catch (Exception ex)
        {
            writer.WriteError(ExceptionMapper.Map(ex, "changing cloud flow state", options.Debug));
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    internal static CommandResult<FlowActivationResult> ToCommandResult(FlowActivationResult result) => new()
    {
        Success = result.Success,
        Data = result,
        Error = result.Success ? null : new StructuredError(result.ErrorCode ?? "Flow.Incomplete",
            result.Message ?? "One or more flow changes were not completed. Inspect the per-flow results.")
    };

    internal static int GetExitCode(FlowActivationResult result) => result.Success ? ExitCodes.Success
        : result.ErrorCode is not null ? ExitCodes.PreconditionFailed
        : result.Flows.Any(f => f.Status == "succeeded") ? ExitCodes.PartialSuccess : ExitCodes.Failure;

    private static void WriteText(FlowActivationResult result)
    {
        Console.WriteLine($"Environment: {result.EnvironmentUrl}");
        Console.WriteLine($"Mode: {(result.DryRun ? "dry-run" : "execute")}; requested state: {result.RequestedState}");
        Console.WriteLine($"Eligible: {result.EligibleCount} of {result.TotalCount}; excluded: {result.ExcludedCount}");
        Console.WriteLine($"Plan hash: {result.PlanHash}");
        foreach (var item in result.Flows)
        {
            Console.WriteLine($"{item.Id} {item.UniqueName}: {item.Status} ({item.Phase}); observed={item.ObservedState}");
            if (item.Message is not null) Console.WriteLine($"  {item.ErrorCode}: {item.Message}");
            foreach (var reference in item.References)
                Console.WriteLine($"  connection-reference {reference.LogicalName}: {reference.Status}");
        }
        foreach (var item in result.Excluded)
            Console.WriteLine($"{item.Id} {item.UniqueName}: excluded — {item.Reason}");
        foreach (var warning in result.Warnings) Console.Error.WriteLine($"Warning: {warning}");
        if (result.Message is not null) Console.Error.WriteLine($"{result.ErrorCode}: {result.Message}");
    }

    private sealed class CommandProgressReporter : IProgressReporter
    {
        public void ReportPhase(string phase, string? detail = null) => Console.Error.WriteLine($"{phase}: {detail}");
        public void ReportProgress(ProgressSnapshot snapshot) =>
            Console.Error.WriteLine($"{snapshot.CurrentItem}/{snapshot.TotalItems}: {snapshot.CurrentEntity} — {snapshot.StatusMessage}");
        public void ReportWarning(string message) => Console.Error.WriteLine($"Warning: {message}");
        public void ReportInfo(string message) => Console.Error.WriteLine(message);
    }
}
