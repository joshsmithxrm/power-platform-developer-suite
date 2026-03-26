using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Get the Maker portal URL for a web resource.
/// </summary>
public static class UrlCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Web resource name, partial name, or GUID"
        };

        var command = new Command("url", "Get the Maker portal URL for a web resource")
        {
            nameArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve name to ID
            var resources = (await webResourceService.ListAsync(cancellationToken: cancellationToken)).Items;
            var resolveResult = WebResourceNameResolver.Resolve(name, resources);

            if (!resolveResult.IsSuccess)
            {
                if (resolveResult.Matches.Count == 0)
                {
                    var error = new StructuredError(
                        ErrorCodes.WebResource.NotFound,
                        $"Web resource '{name}' not found.",
                        null,
                        name);
                    writer.WriteError(error);
                    return ExitCodes.NotFoundError;
                }
                else
                {
                    var matchNames = string.Join("\n  ", resolveResult.Matches.Select(m => m.Name));
                    var error = new StructuredError(
                        ErrorCodes.WebResource.Ambiguous,
                        $"Multiple web resources match '{name}':\n  {matchNames}\n\nSpecify a more complete name to narrow the match.",
                        null,
                        name);
                    writer.WriteError(error);
                    return ExitCodes.InvalidArguments;
                }
            }

            var resource = resolveResult.Matches[0];
            var makerUrl = DataverseUrlBuilder.BuildWebResourceEditorUrl(connectionInfo.EnvironmentUrl, resource.Id);

            if (globalOptions.IsJsonMode)
            {
                var output = new UrlOutput
                {
                    Name = resource.Name,
                    MakerUrl = makerUrl
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine(makerUrl);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting URL for web resource '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UrlOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
