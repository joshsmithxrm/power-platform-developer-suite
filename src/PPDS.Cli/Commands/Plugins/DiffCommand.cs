using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>Compares the managed plugin registration subset without mutating Dataverse.</summary>
public static class DiffCommand
{

    public static Command Create() => CreateCore(null, null, null);

    // The real parse/action/output pipeline, with only connection creation replaced for offline tests.
    internal static Command CreateForTesting(IPluginRegistrationService service, TextWriter output, TextWriter error) =>
        CreateCore(service, output, error);

    private static Command CreateCore(IPluginRegistrationService? service, TextWriter? output, TextWriter? error)
    {
        var configOption = new Option<FileInfo>("--config", "-c")
        {
            Description = "Path to registrations.json", Required = true
        }.AcceptExistingOnly();
        var hiddenOption = new Option<bool>("--include-hidden")
        {
            Description = "Include hidden registration steps in this read-only comparison"
        };
        var command = new Command("diff", "Compare managed registration fields against environment state")
        {
            configOption, hiddenOption, PluginsCommandGroup.ProfileOption, PluginsCommandGroup.EnvironmentOption
        };
        GlobalOptions.AddToCommand(command);
        command.SetAction(async (parse, ct) => await ExecuteAsync(
            parse.GetValue(configOption)!, parse.GetValue(PluginsCommandGroup.ProfileOption),
            parse.GetValue(PluginsCommandGroup.EnvironmentOption), GlobalOptions.GetValues(parse),
            parse.GetValue(hiddenOption), service, output, error ?? Console.Error, ct));
        return command;
    }

    private static async Task<int> ExecuteAsync(FileInfo file, string? profile, string? environment,
        GlobalOptionValues options, bool includeHidden, IPluginRegistrationService? service,
        TextWriter? output, TextWriter error, CancellationToken ct)
    {
        var jsonWriter = options.IsJsonMode ? new JsonOutputWriter(output, options.Debug) : null;
        IOutputWriter writer = jsonWriter ?? ServiceFactory.CreateOutputWriter(options);
        ServiceProvider? provider = null;
        try
        {
            var json = await File.ReadAllTextAsync(file.FullName, ct);
            var config = PluginRegistrationJson.Deserialize(json);
            if (config?.Assemblies is null || config.Assemblies.Count == 0)
            {
                writer.WriteError(new StructuredError(ErrorCodes.Validation.InvalidValue,
                    "No assemblies found in configuration file.", Target: file.Name));
                return ExitCodes.InvalidArguments;
            }
            config.Validate();
            if (service is null)
            {
                provider = await ProfileServiceFactory.CreateFromProfilesAsync(profile, environment,
                    options.Verbose, options.Debug, ProfileServiceFactory.DefaultDeviceCodeCallback, ct);
                service = provider.GetRequiredService<IPluginRegistrationService>();
                if (!options.IsJsonMode)
                    ConsoleHeader.WriteConnectedAs(provider.GetRequiredService<ResolvedConnectionInfo>());
            }

            var result = await new PluginDiffService(service).CompareAsync(config, includeHidden, cancellationToken: ct);
            if (jsonWriter is not null)
                jsonWriter.WriteComparison(result.Assemblies, new
                {
                    result.Status, result.IsComplete, result.HasDrift, result.Coverage
                }, result.IsComplete);
            else
                Render(result, error);
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            writer.WriteError(ExceptionMapper.Map(ex, context: "comparing plugins", debug: options.Debug));
            return ExceptionMapper.ToExitCode(ex);
        }
        finally
        {
            if (provider is not null)
                await provider.DisposeAsync();
        }
    }

    private static void Render(PluginDiffResult result, TextWriter error)
    {
        error.WriteLine(result.Status switch
        {
            "match" => "Compared managed registration fields match.",
            "drift" => "Managed registration drift detected.",
            _ => "Registration comparison incomplete."
        });
        foreach (var assembly in result.Assemblies)
        {
            error.WriteLine($"Assembly: {assembly.AssemblyName}");
            if (assembly.AssemblyMissing) error.WriteLine("  [MISSING] Assembly not registered in environment");
            foreach (var item in assembly.MissingSteps) error.WriteLine($"  [+] Missing step: {item.StepName}");
            foreach (var item in assembly.OrphanedSteps) error.WriteLine($"  [-] Orphaned step: {item.StepName}");
            foreach (var item in assembly.ModifiedSteps)
            {
                error.WriteLine($"  [~] Modified step: {item.StepName}");
                foreach (var change in item.Changes) error.WriteLine($"      {change.Property}: {change.Expected} -> {change.Actual}");
            }
            foreach (var item in assembly.MissingImages) error.WriteLine($"  [+] Missing image: {item.ImageName} on step {item.StepName}");
            foreach (var item in assembly.OrphanedImages) error.WriteLine($"  [-] Orphaned image: {item.ImageName} on step {item.StepName}");
            foreach (var item in assembly.ModifiedImages)
            {
                error.WriteLine($"  [~] Modified image: {item.ImageName} on step {item.StepName}");
                foreach (var change in item.Changes) error.WriteLine($"      {change.Property}: {change.Expected} -> {change.Actual}");
            }
            foreach (var warning in assembly.Warnings) error.WriteLine($"  [!] {warning}");
            RenderCoverage(assembly.Coverage, error);
        }
        RenderCoverage(result.Coverage, error);
    }

    private static void RenderCoverage(ComparisonCoverage coverage, TextWriter error)
    {
        error.WriteLine($"  Coverage: {coverage.Checked.Count} checked, {coverage.Unmanaged.Count} unmanaged, " +
            $"{coverage.Unsupported.Count} unsupported/unverifiable; {coverage.ComparedSteps} of {coverage.TotalSteps} steps compared, " +
            $"{coverage.HiddenSteps} hidden, {coverage.ExcludedHiddenSteps} hidden steps excluded, " +
            $"{coverage.UnknownHiddenStatusSteps} unknown hidden status.");
        foreach (var item in coverage.Unmanaged) error.WriteLine($"    Unmanaged: {item.Path}: {item.Field}");
        foreach (var item in coverage.Unsupported) error.WriteLine($"    Unknown: {item.Path}: {item.Field}");
        foreach (var item in coverage.Excluded) error.WriteLine($"    Excluded: {item.Path}: {item.Field}");
    }

    // Compatibility seam for existing unit callers; all business logic is in the service.
    internal static Task<AssemblyDrift> ComputeDriftAsync(IPluginRegistrationService service,
        PluginAssemblyConfig config, CancellationToken cancellationToken = default) =>
        new PluginDiffService(service).CompareAssemblyAsync(config, cancellationToken: cancellationToken);
}
