using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.WebResources;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Create a new web resource from a local file (#1207).
/// </summary>
public static class CreateCommand
{
    public static Command Create()
    {
        var fileArgument = new Argument<string>("file")
        {
            Description = "Local file to upload as the web resource content"
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Logical name for the new web resource (e.g., new_/icons/vet.svg)",
            Required = true
        };

        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "Display name (defaults to the logical name)"
        };

        var typeOption = new Option<string?>("--type")
        {
            Description = $"Web resource type override; inferred from the file extension when omitted. One of: {WebResourceTypeMap.SingleTypeAliases}"
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Solution unique name to add the web resource to"
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish the web resource after creation (required before it is visible to apps)"
        };

        var command = new Command("create", "Create a new web resource from a local file")
        {
            fileArgument,
            nameOption,
            displayNameOption,
            typeOption,
            solutionOption,
            publishOption,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArgument)!;
            var name = parseResult.GetValue(nameOption)!;
            var displayName = parseResult.GetValue(displayNameOption);
            var type = parseResult.GetValue(typeOption);
            var solution = parseResult.GetValue(solutionOption);
            var publish = parseResult.GetValue(publishOption);
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(file, name, displayName, type, solution, publish, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string file,
        string name,
        string? displayName,
        string? type,
        string? solution,
        bool publish,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate filesystem preconditions and resolve the type before authenticating —
        // avoids a wasted auth round-trip when the file is missing or the type is unknown.
        int typeCode;
        try
        {
            typeCode = ResolveType(file, type);
        }
        catch (PpdsException ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"creating web resource from '{file}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
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

            var content = await File.ReadAllBytesAsync(file, cancellationToken);
            var typeName = WebResourceInfo.GetTypeName(typeCode);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Creating web resource '{name}' ({typeName}, {content.Length} bytes)...");
            }

            var request = new CreateWebResourceRequest(name, displayName, typeCode, content, solution);
            var id = await webResourceService.CreateAsync(request, cancellationToken);

            if (publish)
            {
                await webResourceService.PublishAsync([id], cancellationToken);
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new CreateOutput
                {
                    Id = id,
                    Name = name,
                    DisplayName = displayName ?? name,
                    Type = typeCode,
                    TypeName = typeName,
                    Solution = solution,
                    Published = publish
                });
            }
            else
            {
                Console.Error.WriteLine($"Created web resource '{name}' — id {id}.");
                if (solution != null)
                {
                    Console.Error.WriteLine($"Added to solution '{solution}'.");
                }

                Console.Error.WriteLine(publish
                    ? "Published 1 web resource(s)."
                    : $"Run 'ppds webresources publish {name}' to make it visible to apps.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"creating web resource '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>
    /// Resolves the webresourcetype code from the explicit --type override or, when omitted,
    /// from the file extension. Validates the file exists. Internal for tests.
    /// </summary>
    internal static int ResolveType(string file, string? type)
    {
        if (!File.Exists(file))
        {
            throw new PpdsException(
                ErrorCodes.Validation.FileNotFound,
                $"File '{file}' does not exist.");
        }

        if (type != null)
        {
            if (!WebResourceTypeMap.TryGetSingleCode(type, out var code))
            {
                throw new PpdsException(
                    ErrorCodes.Validation.InvalidValue,
                    $"Unknown web resource type '{type}'. Supported types: {WebResourceTypeMap.SingleTypeAliases}.");
            }

            return code;
        }

        var extension = Path.GetExtension(file).TrimStart('.');
        if (extension.Length == 0 || !WebResourceTypeMap.TryGetSingleCode(extension, out var inferred))
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                $"Cannot infer the web resource type from '{file}'. Pass --type explicitly. Supported types: {WebResourceTypeMap.SingleTypeAliases}.");
        }

        return inferred;
    }

    #region Output Models

    private sealed class CreateOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("solution")]
        public string? Solution { get; set; }

        [JsonPropertyName("published")]
        public bool Published { get; set; }
    }

    #endregion
}
