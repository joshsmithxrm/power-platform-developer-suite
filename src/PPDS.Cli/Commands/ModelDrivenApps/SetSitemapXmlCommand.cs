using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Replaces a model-driven app's sitemap XML from a file, with XSD validation.
/// </summary>
public static class SetSitemapXmlCommand
{
    public static Command Create()
    {
        var xmlOption = new Option<string?>("--xml")
        {
            Description = "[Required] Path to sitemap XML file"
        };

        var command = new Command("set-sitemap-xml", "Replace the sitemap XML for an app (validates against XSD before writing)")
        {
            ModelDrivenAppCommandGroup.AppOption,
            xmlOption,
            ModelDrivenAppCommandGroup.SolutionOption,
            ModelDrivenAppCommandGroup.PublishOption,
            ModelDrivenAppCommandGroup.ConfirmOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var xml = parseResult.GetValue(xmlOption);
            var solution = parseResult.GetValue(ModelDrivenAppCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ModelDrivenAppCommandGroup.PublishOption);
            var confirm = parseResult.GetValue(ModelDrivenAppCommandGroup.ConfirmOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, xml, solution, publish, confirm, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
        string? xmlPath,
        string? solution,
        bool publish,
        bool confirm,
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

        if (string.IsNullOrWhiteSpace(xmlPath))
        {
            writer.WriteError(new StructuredError(ErrorCodes.Validation.RequiredField, "--xml is required."));
            return ExitCodes.InvalidArguments;
        }

        if (!File.Exists(xmlPath))
        {
            writer.WriteError(new StructuredError(ErrorCodes.Validation.FileNotFound, $"File not found: {xmlPath}"));
            return ExitCodes.InvalidArguments;
        }

        try
        {
            var xml = await File.ReadAllTextAsync(xmlPath, ct);

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

            var options = new SetSitemapOptions(solution, publish, confirm);
            await service.SetSitemapXmlAsync(appName, xml, options, null, ct);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Sitemap updated for app '{appName}'.");
            }
            else
            {
                writer.WriteSuccess(new { success = true, app = appName });
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"setting sitemap XML for app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
