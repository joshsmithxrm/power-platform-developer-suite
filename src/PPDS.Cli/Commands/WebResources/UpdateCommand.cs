using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.WebResources;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Update an existing web resource's content from a local file (#1207).
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Web resource name (exact) or GUID"
        };

        var fileArgument = new Argument<string>("file")
        {
            Description = "Local file whose content replaces the web resource content"
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish the web resource after the update (required before the change is visible to apps)"
        };

        var command = new Command("update", "Update an existing web resource's content from a local file")
        {
            nameArgument,
            fileArgument,
            publishOption,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var file = parseResult.GetValue(fileArgument)!;
            var publish = parseResult.GetValue(publishOption);
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, file, publish, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
        string file,
        bool publish,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate the file exists before authenticating — avoids a wasted auth round-trip.
        if (!File.Exists(file))
        {
            var fileError = new PpdsException(
                ErrorCodes.Validation.FileNotFound,
                $"File '{file}' does not exist.");
            writer.WriteError(ExceptionMapper.Map(fileError, context: $"updating web resource '{name}'", debug: globalOptions.Debug));
            return ExceptionMapper.ToExitCode(fileError);
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();

            // Exact name or GUID only — partial matching is intentionally not supported for
            // mutations, so a fuzzy match can never overwrite the wrong resource. When the
            // argument parses as a GUID but no resource has that ID, fall back to an exact-name
            // lookup (a web resource can legitimately be named with a GUID-shaped string).
            var resource = Guid.TryParse(name, out var id)
                ? await webResourceService.GetAsync(id, cancellationToken)
                  ?? await webResourceService.GetByNameAsync(name, cancellationToken)
                : await webResourceService.GetByNameAsync(name, cancellationToken);

            if (resource == null)
            {
                var error = new StructuredError(
                    ErrorCodes.WebResource.NotFound,
                    $"Web resource '{name}' not found. Use 'ppds webresources create {file} --name {name}' to create it.",
                    null,
                    name);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            // The stored type is immutable on update — warn when the file extension implies
            // a different type so a mismatched upload is visible, not silent.
            var extension = Path.GetExtension(file).TrimStart('.');
            if (extension.Length > 0 &&
                WebResourceTypeMap.TryGetSingleCode(extension, out var impliedType) &&
                impliedType != resource.WebResourceType)
            {
                Console.Error.WriteLine(
                    $"Warning: file extension '.{extension}' implies {WebResourceInfo.GetTypeName(impliedType)} " +
                    $"but '{resource.Name}' is {resource.TypeName}. The type is unchanged; content uploaded as-is.");
            }

            var content = await File.ReadAllBytesAsync(file, cancellationToken);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Updating web resource '{resource.Name}' ({content.Length} bytes)...");
            }

            await webResourceService.UpdateContentAsync(resource.Id, content, cancellationToken);

            if (publish)
            {
                await webResourceService.PublishAsync([resource.Id], cancellationToken);
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new UpdateOutput
                {
                    Id = resource.Id,
                    Name = resource.Name,
                    Type = resource.WebResourceType,
                    TypeName = resource.TypeName,
                    Published = publish
                });
            }
            else
            {
                Console.Error.WriteLine($"Updated web resource '{resource.Name}'.");
                Console.Error.WriteLine(publish
                    ? "Published 1 web resource(s)."
                    : $"Run 'ppds webresources publish {resource.Name}' to make the change visible to apps.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"updating web resource '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UpdateOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("published")]
        public bool Published { get; set; }
    }

    #endregion
}
