using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Displays the hierarchical sitemap navigation structure of a model-driven app.
/// </summary>
public static class SitemapCommand
{
    public static Command Create()
    {
        var command = new Command("sitemap", "Display the sitemap navigation structure of an app")
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

            var sitemap = await service.GetSitemapAsync(appName, ct);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(sitemap);
            }
            else
            {
                if (sitemap.Areas.Count == 0)
                {
                    Console.WriteLine("(no navigation configured)");
                }
                else
                {
                    foreach (var area in sitemap.Areas)
                    {
                        var areaLabel = area.Title ?? area.Id;
                        Console.WriteLine($"Area: {areaLabel}");

                        foreach (var group in area.Groups)
                        {
                            var groupLabel = group.Title ?? group.Id;
                            Console.WriteLine($"└── Group: {(string.IsNullOrEmpty(groupLabel) ? "(default)" : groupLabel)}");

                            foreach (var subArea in group.SubAreas)
                            {
                                var subLabel = subArea.Title ?? subArea.Entity ?? subArea.Id;
                                Console.WriteLine($"    └── {subLabel}");
                            }
                        }
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting sitemap for app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
