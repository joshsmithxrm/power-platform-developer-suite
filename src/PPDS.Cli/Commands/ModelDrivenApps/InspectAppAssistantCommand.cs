using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Read-only "copilot doctor" — inspects a model-driven app's Copilot (bot)
/// <c>appelement</c> bindings and reports problems (non-app-assistant bots, duplicate
/// bindings, orphan rows). Makes no changes; exits non-zero when any problem is found.
/// </summary>
public static class InspectAppAssistantCommand
{
    public static Command Create()
    {
        var command = new Command("inspect-app-assistant",
            "Diagnose Copilot (app assistant) bindings for the app — read-only, no changes")
        {
            ModelDrivenAppCommandGroup.AppOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken ct)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        if (string.IsNullOrWhiteSpace(appName))
        {
            writer.WriteError(new StructuredError(ErrorCodes.Validation.RequiredField, "--app is required."));
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile, environment, globalOptions.Verbose, globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback, ct);

            var service = serviceProvider.GetRequiredService<IModelDrivenAppService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var diagnostics = await service.InspectAppAssistantAsync(appName, ct);

            if (globalOptions.IsJsonMode)
            {
                WriteJson(writer, diagnostics);
            }
            else
            {
                WriteText(diagnostics);
            }

            // Non-zero exit when problems are found so the check is scriptable in CI / pre-publish gates.
            return diagnostics.IsHealthy ? ExitCodes.Success : ExitCodes.PreconditionFailed;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"inspecting app assistant bindings for app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    // Data → stdout; status/summary → stderr (output discipline).
    private static void WriteText(AppAssistantDiagnostics diagnostics)
    {
        if (diagnostics.IsHealthy)
        {
            Console.Error.WriteLine($"No Copilot binding problems found for app '{diagnostics.AppName}'.");
            return;
        }

        Console.Error.WriteLine($"Found {diagnostics.Findings.Count} Copilot binding problem(s) in app '{diagnostics.AppName}':");
        Console.Error.WriteLine();

        foreach (var finding in diagnostics.Findings)
        {
            var botLabel = finding.BotName ?? finding.BotId?.ToString() ?? "(none)";
            var lightweight = finding.IsLightweightBot switch
            {
                true => "true",
                false => "false",
                null => "unknown"
            };
            var appElements = string.Join(", ", finding.AppElementIds);

            // The finding record is the diagnostic data → stdout.
            Console.WriteLine($"[{Describe(finding.Kind)}] {botLabel}");
            Console.WriteLine($"  botId            : {finding.BotId?.ToString() ?? "(none)"}");
            Console.WriteLine($"  isLightweightBot : {lightweight}");
            Console.WriteLine($"  appelement id(s) : {appElements}");
            // The remediation hint is advisory guidance, not pipeable data → stderr (stdout discipline).
            Console.Error.WriteLine($"  fix [{botLabel}]   : {finding.Remediation}");
        }
    }

    private static void WriteJson(IOutputWriter writer, AppAssistantDiagnostics diagnostics)
    {
        var payload = new InspectResult
        {
            App = diagnostics.AppName,
            AppModuleId = diagnostics.AppModuleId,
            Healthy = diagnostics.IsHealthy,
            FindingCount = diagnostics.Findings.Count,
            Findings = diagnostics.Findings.Select(f => new InspectFinding
            {
                Kind = f.Kind.ToString(),
                BotName = f.BotName,
                BotId = f.BotId,
                IsLightweightBot = f.IsLightweightBot,
                AppElementIds = f.AppElementIds.ToList(),
                Remediation = f.Remediation
            }).ToList()
        };

        writer.WriteSuccess(payload);
    }

    private static string Describe(AppAssistantFindingKind kind) => kind switch
    {
        AppAssistantFindingKind.NotAppAssistant => "not-app-assistant",
        AppAssistantFindingKind.DuplicateBinding => "duplicate-binding",
        AppAssistantFindingKind.OrphanAppElement => "orphan-appelement",
        _ => kind.ToString()
    };

    private sealed class InspectResult
    {
        [JsonPropertyName("app")]
        public string App { get; set; } = string.Empty;

        [JsonPropertyName("appModuleId")]
        public Guid AppModuleId { get; set; }

        [JsonPropertyName("healthy")]
        public bool Healthy { get; set; }

        [JsonPropertyName("findingCount")]
        public int FindingCount { get; set; }

        [JsonPropertyName("findings")]
        public List<InspectFinding> Findings { get; set; } = new();
    }

    private sealed class InspectFinding
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("botName")]
        public string? BotName { get; set; }

        [JsonPropertyName("botId")]
        public Guid? BotId { get; set; }

        [JsonPropertyName("isLightweightBot")]
        public bool? IsLightweightBot { get; set; }

        [JsonPropertyName("appElementIds")]
        public List<Guid> AppElementIds { get; set; } = new();

        [JsonPropertyName("remediation")]
        public string Remediation { get; set; } = string.Empty;
    }
}
