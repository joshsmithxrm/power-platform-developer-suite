using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Get web resource content by name or ID.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Web resource name, partial name, or GUID"
        };

        var unpublishedOption = new Option<bool>("--unpublished")
        {
            Description = "Get the unpublished (latest draft) version instead of published"
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Write content to file instead of stdout"
        };

        var command = new Command("get", "Get web resource content")
        {
            nameArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption,
            unpublishedOption,
            outputOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var unpublished = parseResult.GetValue(unpublishedOption);
            var output = parseResult.GetValue(outputOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, profile, environment, unpublished, output, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
        string? profile,
        string? environment,
        bool unpublished,
        string? outputPath,
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

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
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
                    // Ambiguous
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

            // Block binary types — service decodes content as UTF-8 text, binary would be garbled
            if (!resource.IsTextType)
            {
                var error = new StructuredError(
                    ErrorCodes.WebResource.NotEditable,
                    $"Web resource '{resource.Name}' is a {resource.TypeName} file (binary). Only text-based web resources can be retrieved.",
                    null,
                    resource.Name);
                writer.WriteError(error);
                return ExitCodes.InvalidArguments;
            }

            // Fetch content — published by default, unpublished if requested
            var content = await webResourceService.GetContentAsync(
                resource.Id,
                published: !unpublished,
                cancellationToken: cancellationToken);

            if (content?.Content == null)
            {
                var error = new StructuredError(
                    ErrorCodes.WebResource.NotFound,
                    $"Web resource '{resource.Name}' has no content.",
                    null,
                    resource.Name);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new GetOutput
                {
                    Id = resource.Id,
                    Name = resource.Name,
                    Type = resource.TypeName,
                    Content = content.Content,
                    ModifiedOn = content.ModifiedOn
                };
                writer.WriteSuccess(output);
            }
            else if (outputPath != null)
            {
                await File.WriteAllTextAsync(outputPath, content.Content, cancellationToken);
                Console.Error.WriteLine($"Written to {outputPath} ({content.Content.Length} characters)");
            }
            else
            {
                // Text content to stdout (pipeable)
                Console.WriteLine(content.Content);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting web resource '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class GetOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
